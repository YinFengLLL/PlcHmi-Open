using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// 报警扫描服务。
///
/// <para><b>做什么：</b>周期性把 <c>alarms.json</c> 里每条定义绑定的 PLC 地址读回来，
/// 按位或整字判断是否触发——触发则让 <see cref="报警服务接口"/> 产生报警，
/// 恢复为 0 则自动消除。这样"PLC 的某个位为 1"就映射成"界面上显示某条报警文本"。</para>
///
/// <para><b>读法：</b>M 区是位软元件，用 FC01 读线圈；D 区是字软元件，用 FC03 读保持寄存器。
/// 两者分流处理。同区且地址接近的合并为一次请求。</para>
/// </summary>
public sealed class 报警扫描服务 : IHostedService
{
    /// <summary>扫描周期。</summary>
    private static readonly TimeSpan 扫描间隔 = TimeSpan.FromMilliseconds(500);

    /// <summary>合并读取时允许的最大跨度（避免一次读太多无关寄存器）。</summary>
    private const int 最大合并间隔 = 16;

    /// <summary>单次读的上限（模板保守取值）。</summary>
    private const int 每次请求最大寄存器数 = 100;

    private readonly 报警服务接口 _报警;
    private readonly 报警定义服务接口 _定义;
    private readonly Modbus客户端接口 _客户端;
    private readonly 地址解析器接口 _解析器;
    private readonly ILogger<报警扫描服务> _日志;

    /// <summary>当前处于活动状态的定义 Id，用于判断何时该消除。</summary>
    private readonly HashSet<string> _活动标识 = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 读取计划缓存。定义很少变，此前每轮（500ms）都重新 Where/解析/排序/合并一遍，
    /// 还要对 定义集合（每次 ToArray 快照）再拷一次——纯浪费。
    /// 定义变更（增删改 / 重载）时经 <see cref="报警定义服务接口.定义已变更"/> 置空重建。
    /// </summary>
    private List<读取方案>? _方案缓存;

    private CancellationTokenSource? _取消令牌源;
    private Task? _循环;

    public 报警扫描服务(
        报警服务接口 报警列表,
        报警定义服务接口 定义列表,
        Modbus客户端接口 客户端,
        地址解析器接口 解析器,
        ILogger<报警扫描服务> 日志)
    {
        _报警 = 报警列表;
        _定义 = 定义列表;
        _客户端 = 客户端;
        _解析器 = 解析器;
        _日志 = 日志;

        _定义.定义已变更 += (_, _) => _方案缓存 = null;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken 取消令牌)
    {
        // 防重入：二次 Start 不先 Stop 会覆盖 _取消令牌源，泄漏旧 CTS 与旧循环
        if (_取消令牌源 is not null)
        {
            return Task.CompletedTask;
        }

        _取消令牌源 = CancellationTokenSource.CreateLinkedTokenSource(取消令牌);
        _循环 = Task.Run(() => 异步扫描循环(_取消令牌源.Token), _取消令牌源.Token);

        _日志.LogInformation("报警扫描已启动，周期 {Interval}ms", 扫描间隔.TotalMilliseconds);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken 取消令牌)
    {
        if (_取消令牌源 is null)
        {
            return;
        }

        await _取消令牌源.CancelAsync().ConfigureAwait(false);

        if (_循环 is not null)
        {
            var 等待超时 = TimeSpan.FromSeconds(5);

            try
            {
                // 带超时：循环若因 bug 卡住，不能让程序退出无限期挂在这里
                await _循环.WaitAsync(等待超时, 取消令牌).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // 5 秒内没退出 = 循环卡住的征兆，不是正常关闭，必须留下痕迹
                _日志.LogWarning("等待报警扫描循环退出超时（{Seconds}s），已放弃等待", 等待超时.TotalSeconds);
            }
            catch (OperationCanceledException)
            {
                // 外部取消（程序退出），正常
            }
            catch (Exception 异常)
            {
                // 循环因自身 bug 挂掉：记录但不影响退出流程
                _日志.LogWarning(异常, "报警扫描循环异常退出");
            }
        }

        _取消令牌源.Dispose();
        _取消令牌源 = null;

        _日志.LogInformation("报警扫描已停止");
    }

    // ── 扫描循环 ────────────────────────────────────────────────────────

    private async Task 异步扫描循环(CancellationToken 取消令牌)
    {
        // 2026-11-19 性能改造：Task.Delay(扫描间隔) 的实际周期 = 扫描耗时 + 间隔，会累积漂移；
        // 改用 PeriodicTimer（与轮询引擎同一套写法），节拍固定，不再随扫描耗时后移。
        using var 计时器 = new PeriodicTimer(扫描间隔);

        try
        {
            while (await 计时器.WaitForNextTickAsync(取消令牌).ConfigureAwait(false))
            {
                try
                {
                    if (_客户端.是否已连接)
                    {
                        await 异步扫描(取消令牌).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception 异常)
                {
                    // 单次扫描失败不能把循环打断，否则一次网络抖动就永远不再报警
                    _日志.LogWarning(异常, "报警扫描失败，将在下个周期重试");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
    }

    /// <summary>执行一轮扫描。</summary>
    private async Task 异步扫描(CancellationToken 取消令牌)
    {
        // 读取计划缓存：定义未变更时直接复用，免去每轮重新解析/排序/合并。
        // 2026-11-19：启用项过滤从每轮无条件执行改为只在构建方案时执行一次
        // （缓存命中时那份列表根本用不上，白做一次 Where+ToList）。
        var 方案列表 = _方案缓存;

        if (方案列表 is null)
        {
            var 定义列表 = _定义.定义集合.Where(条目项 => 条目项.启用).ToList();

            if (定义列表.Count == 0)
            {
                return;
            }

            方案列表 = 构建读取方案列表(定义列表);
            _方案缓存 = 方案列表;
        }

        foreach (var 方案 in 方案列表)
        {
            await 异步扫描方案(方案, 取消令牌).ConfigureAwait(false);
        }
    }

    private async Task 异步扫描方案(读取方案 方案, CancellationToken 取消令牌)
    {
        try
        {
            if (方案.元素 == 软元素.M)
            {
                var 线圈 = await _客户端
                    .异步读取线圈(方案.启动地址, 方案.数量, 取消令牌)
                    .ConfigureAwait(false);

                foreach (var (定义项, 地址值) in 方案.条目集合)
                {
                    var 偏移量 = 地址值 - 方案.启动地址;

                    if (偏移量 >= 0 && 偏移量 < 线圈.Length)
                    {
                        应用状态(定义项, 线圈[偏移量]);
                    }
                }
            }
            else
            {
                var 寄存器列表 = await _客户端
                    .异步读取保持寄存器(方案.启动地址, 方案.数量, 取消令牌)
                    .ConfigureAwait(false);

                foreach (var (定义项, 地址值) in 方案.条目集合)
                {
                    var 偏移量 = 地址值 - 方案.启动地址;

                    if (偏移量 < 0 || 偏移量 >= 寄存器列表.Length)
                    {
                        continue;
                    }

                    var 原始值 = 寄存器列表[偏移量];

                    var 是否开启 = 定义项.是否位模式
                        ? (原始值 & (1 << 定义项.位)) != 0
                        : 原始值 != 0;

                    应用状态(定义项, 是否开启);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception 异常)
        {
            // 报警读不回来 = "该报的警不报"，属低频且关键，现场必须看得见（默认 Information 级过滤掉 Debug）
            _日志.LogWarning(异常, "读取报警区 {Element}{Start} 失败，将在下个周期重试",
                方案.元素, 方案.启动地址);
        }
    }

    /// <summary>根据状态决定产生还是消除报警。</summary>
    private void 应用状态(报警定义 定义项, bool 是否开启)
    {
        var 活动 = _活动标识.Contains(定义项.标识);

        if (是否开启 && !活动)
        {
            _活动标识.Add(定义项.标识);
            _报警.触发(定义项.标识, 定义项.消息, 定义项.等级);
        }
        else if (!是否开启 && 活动)
        {
            _活动标识.Remove(定义项.标识);
            _报警.清空(定义项.标识);
        }
    }

    // ── 读取计划 ────────────────────────────────────────────────────────

    /// <summary>把定义解析成地址，并按「同软元件区 + 地址接近」合并成读取计划。</summary>
    private List<读取方案> 构建读取方案列表(List<报警定义> 定义列表)
    {
        var 已解析 = new List<(报警定义 定义, 软元素 元素, int 地址)>();

        foreach (var 定义项 in 定义列表)
        {
            if (_解析器.尝试解析(定义项.地址, out var PLC地址) && PLC地址 is not null)
            {
                if (PLC地址.元素 == 软元素.M && 定义项.是否位模式)
                {
                    // M 区报警走线圈路径，位号被完全忽略——配置里填了会静默无效，
                    // 现场表现是"报警该响不响"，必须留痕（构建只在缓存失效时发生，不会刷屏）
                    _日志.LogWarning(
                        "报警 {Id} 的地址 {Address} 是 M 区，位号 {Bit} 不生效（M 区按整位扫描），请删除该位号",
                        定义项.标识, 定义项.地址, 定义项.位);
                }

                已解析.Add((定义项, PLC地址.元素, PLC地址.Modbus地址));
            }
            else
            {
                // 地址解析失败 = 这条报警永远不会触发，属配置错误，必须让现场看见
                _日志.LogWarning("报警 {Id} 的地址「{Address}」无法解析，已跳过（该报警不会生效）",
                    定义项.标识, 定义项.地址);
            }
        }

        var 方案列表 = new List<读取方案>();

        // 先按软元件区、再按地址排序 —— M0 与 D0 的 Modbus 地址都是 0，
        // 不区分区就会把两个不同区域的数据混到一次请求里。
        foreach (var 条目 in 已解析
                     .OrderBy(坐标R => 坐标R.元素)
                     .ThenBy(坐标R => 坐标R.地址))
        {
            var 最后 = 方案列表.Count > 0 ? 方案列表[^1] : null;

            // 注意：last 为空（首条）时必须走新建分支。
            // 早先这里先算了 last.StartAddress 才判空，首轮扫描直接 NRE，
            // 异常被循环吞掉 → 报警永远不产生。别把判空挪到后面。
            if (最后 is not null &&
                最后.元素 == 条目.元素 &&
                条目.地址 >= 最后.启动地址 &&
                条目.地址 - 最后.启动地址 + 1 <= 每次请求最大寄存器数 &&
                条目.地址 - (最后.启动地址 + 最后.数量) <= 最大合并间隔)
            {
                最后.条目集合.Add((条目.定义, 条目.地址));
                最后.数量 = Math.Max(最后.数量, 条目.地址 - 最后.启动地址 + 1);
            }
            else
            {
                var 方案 = new 读取方案 { 启动地址 = 条目.地址, 元素 = 条目.元素 };
                方案.条目集合.Add((条目.定义, 条目.地址));
                方案.数量 = 1;
                方案列表.Add(方案);
            }
        }

        return 方案列表;
    }

    /// <summary>一次读取计划。</summary>
    private sealed class 读取方案
    {
        /// <summary>起始 Modbus 地址。</summary>
        public int 启动地址 { get; init; }

        /// <summary>读取长度（会随合并增长）。</summary>
        public int 数量 { get; set; }

        /// <summary>所属软元件区（M 用线圈、D 用保持寄存器）。</summary>
        public 软元素 元素 { get; init; }

        /// <summary>本计划覆盖的报警定义与它们的 Modbus 地址。</summary>
        public List<(报警定义 定义, int 地址)> 条目集合 { get; } = new();
    }
}
