using System.Diagnostics;
using Microsoft.Extensions.Logging;
using 上位机.通信.Modbus;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// 轮询引擎。按分组周期采集点位，做变化检测后分发。
///
/// <para><b>优化策略：</b></para>
/// <list type="number">
///   <item>连续地址合并为一次请求，减少往返次数；</item>
///   <item>单次请求不超过 <c>MaxRegistersPerRequest</c>（默认 100，汇川上限 123）；</item>
///   <item>采集运行在后台线程，UI 只经事件收到变化的值。</item>
/// </list>
/// </summary>
public sealed partial class 轮询引擎 : 轮询引擎接口, IAsyncDisposable
{
    private readonly Modbus客户端接口 _客户端;
    private readonly 标签仓储接口 _标签;
    private readonly IO点位服务接口 _IO点位表;
    private readonly 地址解析器接口 _解析器;
    private readonly 标签值服务 _值列表;
    private readonly 轮询选项 _轮询选项;
    private readonly PLC连接选项 _PLC选项;
    private readonly ILogger<轮询引擎> _日志;

    private readonly List<轮询分组> _分组 = new();

    /// <summary>各分组最近一轮采集耗时。三个分组并发写，故单独用小锁保护。</summary>
    private readonly Dictionary<标签分组, double> _周期毫秒分组 = new();
    private readonly object _周期同步 = new();

    private CancellationTokenSource? _取消令牌源;
    private Task? _工作线程;

    public 轮询引擎(
        Modbus客户端接口 客户端,
        标签仓储接口 标签列表,
        IO点位服务接口 IO点位表,
        地址解析器接口 解析器,
        标签值服务 值列表,
        轮询选项 轮询选项,
        PLC连接选项 PLC选项,
        ILogger<轮询引擎> 日志)
    {
        _客户端 = 客户端;
        _标签 = 标签列表;
        _IO点位表 = IO点位表;
        _解析器 = 解析器;
        _值列表 = 值列表;
        _轮询选项 = 轮询选项;
        _PLC选项 = PLC选项;
        _日志 = 日志;
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<标签分组, double> 周期毫秒分组
    {
        get
        {
            lock (_周期同步)
            {
                return new Dictionary<标签分组, double>(_周期毫秒分组);
            }
        }
    }

    /// <inheritdoc />
    public Task 异步启动(CancellationToken 取消令牌 = default)
    {
        if (_工作线程 is not null)
        {
            return Task.CompletedTask;
        }

        准备方案列表();

        _取消令牌源 = CancellationTokenSource.CreateLinkedTokenSource(取消令牌);
        _工作线程 = Task.Run(() => 异步运行(_取消令牌源.Token), CancellationToken.None);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task 异步停止(CancellationToken 取消令牌 = default)
    {
        if (_取消令牌源 is null || _工作线程 is null)
        {
            return;
        }

        await _取消令牌源.CancelAsync().ConfigureAwait(false);

        var 等待超时 = TimeSpan.FromSeconds(5);
        var 已退出 = false;

        try
        {
            await _工作线程.WaitAsync(等待超时, 取消令牌).ConfigureAwait(false);
            已退出 = true;
        }
        catch (TimeoutException)
        {
            // 5 秒内没退出 = 循环卡住的征兆，不是正常关闭，必须留下痕迹
            _日志.LogWarning("等待轮询引擎退出超时（{Seconds}s），已放弃等待", 等待超时.TotalSeconds);
        }
        catch (OperationCanceledException)
        {
            // 外部取消（程序退出），正常
            已退出 = true;
        }
        catch (Exception 异常)
        {
            // 循环本身有兜底 catch，这里只是防御：任何残留异常都不该拖住程序退出
            _日志.LogWarning(异常, "轮询引擎异常退出");
            已退出 = true;
        }
        finally
        {
            _取消令牌源.Dispose();
            _取消令牌源 = null;

            // 仅当循环确认已退出才清空方案并放行下次启动：
            // 超时后旧线程可能仍在 foreach 读取方案——此时 Clear() 会抛
            // InvalidOperationException（表现为"这一轮采集静默丢失"），
            // 且紧随的"重建方案列表"会因 _工作线程 == null 启动第二个轮询循环，
            // 两个循环同时抢占同一个串行化连接。
            if (已退出)
            {
                _工作线程 = null;
                _分组.Clear();
                _IO读取方案.Clear();

                lock (_周期同步)
                {
                    _周期毫秒分组.Clear();
                }
            }
            else
            {
                // 旧循环未退出：保留句柄让 异步启动 拒绝重复启动（fail-safe），
                // 重建操作会因此被拒绝——比两个循环抢一条连接可诊断得多
                _日志.LogWarning("轮询引擎旧循环仍未退出，已阻止重复启动；请重启程序恢复采集计划重建");
            }
        }
    }

    /// <inheritdoc />
    public async Task 异步重建方案列表(CancellationToken 取消令牌 = default)
    {
        // 走"安全停止 → 重新编译 → 重新启动"，而不是运行中增量替换读取计划：
        // RunAsync 在启动那一刻就把 _groups 分发给各分组循环，运行中改集合会引入并发读写风险。
        // 重建只需几百毫秒，而且只在管理员改地址这种低频操作时发生，代价可接受。
        await 异步停止(取消令牌).ConfigureAwait(false);
        await 异步启动(取消令牌).ConfigureAwait(false);

        _日志.LogInformation("采集计划已重建，共 {GroupCount} 个分组", _分组.Count);
    }

    /// <summary>构建各分组的读取计划。</summary>
    private void 准备方案列表()
    {
        _分组.Clear();

        var 表格项 = _IO点位表.表格;
        _启用输入.Clear();
        _启用输入.AddRange(表格项.输入.Where(输入点位 => 输入点位.启用));
        _启用输出.Clear();
        _启用输出.AddRange(表格项.输出.Where(输出点位 => 输出点位.启用));

        // IO 点地址解析一次编译为读取方案（热路径每周期复用，不再重复正则解析）
        // X/Y 区位区可经 io-points.json 元显式覆盖（部分机型物理输入在离散输入区）
        构建IO读取方案(表格项.元.X区域, 表格项.元.Y区域);

        foreach (var (group, 间隔项) in new[]
                 {
                     (标签分组.快, _轮询选项.快间隔毫秒),
                     (标签分组.慢, _轮询选项.慢间隔毫秒),
                     (标签分组.事件, _轮询选项.事件间隔毫秒),
                 })
        {
            var 分组标签 = _标签.获取分组(group);
            var 方案列表 = 构建读取方案列表(分组标签);

            if (方案列表.Count == 0 && group != 标签分组.快)
            {
                continue;
            }

            _分组.Add(new 轮询分组(group, TimeSpan.FromMilliseconds(间隔项), 方案列表, group == 标签分组.快));
        }

        foreach (var 分组项 in _分组)
        {
            _日志.LogInformation("轮询分组 {Group}：周期 {Interval}ms，{PlanCount} 个读取计划",
                分组项.分组, 分组项.间隔.TotalMilliseconds, 分组项.方案集合.Count);
        }

        _日志.LogInformation("IO 轮询：X {InputCount} 点、Y {OutputCount} 点，共 {PlanCount} 个读取计划",
            _启用输入.Count, _启用输出.Count, _IO读取方案.Count);
    }

    // 读取计划编译（构建读取方案列表 / 合并地址 / 计划记录类型）见 PollingEngine.Plans.cs（partial）。

    private async Task 异步运行(CancellationToken 取消令牌)
    {
        var 任务 = _分组.Select(分组项 => 异步运行分组(分组项, 取消令牌)).ToList();

        try
        {
            await Task.WhenAll(任务).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
        catch (Exception 异常)
        {
            _日志.LogError(异常, "轮询引擎异常退出");
        }
    }

    private async Task 异步运行分组(轮询分组 group, CancellationToken 取消令牌)
    {
        using var 计时器 = new PeriodicTimer(group.间隔);

        while (await 计时器.WaitForNextTickAsync(取消令牌).ConfigureAwait(false))
        {
            if (!_客户端.是否已连接)
            {
                continue;
            }

            var 计时 = Stopwatch.StartNew();

            try
            {
                // 快组顺带刷新 IO 点
                if (group.含IO)
                {
                    await 异步轮询IO点位(取消令牌).ConfigureAwait(false);
                }

                await 异步轮询标签(group, 取消令牌).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception 异常)
            {
                _日志.LogWarning("轮询分组 {Group} 出现异常：{Message}", group.分组, 异常.Message);
            }

            计时.Stop();

            lock (_周期同步)
            {
                _周期毫秒分组[group.分组] = 计时.Elapsed.TotalMilliseconds;
            }
        }
    }

    /// <summary>按读取计划采集点位，解码并发布。</summary>
    private async Task 异步轮询标签(轮询分组 group, CancellationToken 取消令牌)
    {
        foreach (var 方案 in group.方案集合)
        {
            try
            {
                switch (方案.区域)
                {
                    case Modbus区域.线圈:
                    {
                        var 位 = await _客户端.异步读取线圈(方案.启动地址, 方案.数量, 取消令牌)
                            .ConfigureAwait(false);
                        发布方案值列表(方案, 位, null);
                        break;
                    }

                    case Modbus区域.离散输入:
                    {
                        var 位 = await _客户端.异步读取离散输入(方案.启动地址, 方案.数量, 取消令牌)
                            .ConfigureAwait(false);
                        发布方案值列表(方案, 位, null);
                        break;
                    }

                    case Modbus区域.输入寄存器:
                    {
                        var 寄存器列表 = await _客户端.异步读取输入寄存器(方案.启动地址, 方案.数量, 取消令牌)
                            .ConfigureAwait(false);
                        发布方案值列表(方案, null, 寄存器列表);
                        break;
                    }

                    default:
                    {
                        var 寄存器列表 = await _客户端.异步读取保持寄存器(方案.启动地址, 方案.数量, 取消令牌)
                            .ConfigureAwait(false);
                        发布方案值列表(方案, null, 寄存器列表);
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception 异常)
            {
                // 单个读取计划失败只跳过它自己：曾用裸 foreach，任一计划抛异常
                // （如某段地址 PLC 回异常码 03）会中断整组本轮采集，同组后续
                // 全部点位本周期不刷新。IO 路径早已按此模式隔离，标签路径对齐。
                _日志.LogWarning(异常, "读取计划 {Region} D{Start}x{Count} 执行失败，本周期跳过该计划",
                    方案.区域, 方案.启动地址, 方案.数量);
            }
        }
    }

    // 解码值物理范围诊断与点位值发布见 PollingEngine.Publish.cs（partial）。

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await 异步停止().ConfigureAwait(false);
    }
}
