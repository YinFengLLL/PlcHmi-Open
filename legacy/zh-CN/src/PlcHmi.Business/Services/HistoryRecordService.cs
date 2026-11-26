using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using 上位机.业务.配置;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// 历史记录服务。详见 <see cref="历史记录服务接口"/>。
///
/// <para><b>采集原理：</b>周期性读 <c>触发位</c>（M 区线圈），检测 <b>0 → 1 上升沿</b>；
/// 沿到达时把配置里各数据项的地址读回来，拼成一条记录落盘（一天一个文件）。</para>
///
/// <para><b>为什么不用轮询引擎：</b>触发位与数据项地址都要允许现场随时改，
/// 而轮询引擎的读取计划是启动时按 <c>tags.json</c> 编译好的；把可编辑地址塞进点位表，
/// 就等于"改地址必须重启程序"。这里按需直读，与示教 / 参数页读回的做法一致。</para>
///
/// <para><b>整条记录要么全有要么全无：</b>任一数据项读失败就丢弃本次记录并记 Warning ——
/// 半条记录（前几列有值、后几列空白）在追溯时比"没有这一条"更危险：看的人以为设备就是这么干的。</para>
///
/// <para>本类按职责拆成三个文件：本文件（生命周期与采集）、
/// <c>.Acquisition.cs</c>（读取计划）、<c>.Config.cs</c>（校验与配置读写）。</para>
/// </summary>
public sealed partial class 历史记录服务 : IHostedService, 历史记录服务接口
{
    /// <summary>扫描周期：与报警扫描一致，够快且不给 PLC 添压力。</summary>
    private static readonly TimeSpan 扫描间隔 = TimeSpan.FromMilliseconds(500);

    private readonly 地址解析器接口 _解析器;
    private readonly Modbus客户端接口 _客户端;
    private readonly 权限守卫接口 _权限;
    private readonly 历史记录存储 _存储;
    private readonly ILogger<历史记录服务> _日志;
    private readonly 配置健康接口? _健康;
    private readonly Func<DateTime> _时钟;

    /// <summary>
    /// 当前配置。<b>初值就是内置默认值</b>：宿主启动前（或配置文件读不出来时）
    /// <see cref="配置"/> 也必须是一份可用的数据体，否则界面会显示一个"无列可显"的空表，
    /// 让人以为是页面坏了。
    /// </summary>
    private 历史记录配置 _配置 = 创建默认值();

    /// <summary>触发位上一次的电平。<c>null</c> = 尚未建立基线（启动后第一轮不记录）。</summary>
    private bool? _上次触发;

    /// <summary>记录已落盘但触发位复位没写出去：下个周期补一次复位（绝不重复记录）。</summary>
    private bool _待复位;

    /// <summary>上一轮数据项读取失败：触发位仍为 1，持续重试直到读取成功。</summary>
    private bool _读取重试中;

    /// <summary>读取失败只提示一次（每 500ms 一条 Warning 会把日志刷爆），成功后复位。</summary>
    private bool _读取失败已提示;

    private bool _已提示配置错误;
    private CancellationTokenSource? _取消令牌源;
    private Task? _循环;

    /// <summary>扫码枪服务（工单号来源；未注册扫码枪时为空，记录里工单号就是空串）。</summary>
    private readonly 扫码枪服务接口? _扫码枪;

    public 历史记录服务(
        地址解析器接口 解析器,
        Modbus客户端接口 客户端,
        权限守卫接口 权限,
        历史记录存储 存储,
        ILogger<历史记录服务> 日志,
        配置健康接口? 健康 = null,
        Func<DateTime>? 时钟提供器 = null,
        扫码枪服务接口? 扫码枪 = null)
    {
        _解析器 = 解析器;
        _客户端 = 客户端;
        _权限 = 权限;
        _存储 = 存储;
        _日志 = 日志;
        _健康 = 健康;
        _时钟 = 时钟提供器 ?? (() => DateTime.Now);
        _扫码枪 = 扫码枪;
    }

    /// <inheritdoc />
    public 历史记录配置 配置 => _配置;

    /// <inheritdoc />
    public event EventHandler? 配置已变更;

    /// <inheritdoc />
    public event EventHandler<历史记录条目>? 记录已新增;

    // ── 生命周期 ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task 异步初始化(CancellationToken 取消令牌 = default)
    {
        await 异步加载配置(取消令牌).ConfigureAwait(false);

        // 装载当天已有记录：否则重启后第一条记录会把当天早班的数据整个覆盖
        await _存储.异步初始化(_时钟().Date, _配置.保留天数, 取消令牌).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken 取消令牌)
    {
        // 先把配置与当天已有记录装载完，再起采集循环：
        // 反过来的话，启动瞬间的第一条记录可能正好落在"写入日尚未就绪"的窗口里，
        // 而写入日未就绪时落盘会覆盖当天已有数据。
        await 异步初始化(取消令牌).ConfigureAwait(false);

        _取消令牌源 = CancellationTokenSource.CreateLinkedTokenSource(取消令牌);
        _循环 = Task.Run(() => 异步采集循环(_取消令牌源.Token), _取消令牌源.Token);

        _日志.LogInformation(
            "历史记录采集已启动：触发位 {Address}（{State}），{Count} 个数据项，周期 {Interval}ms",
            _配置.触发地址, _配置.启用 ? "已启用" : "已停用",
            _配置.数据项集合.Count, 扫描间隔.TotalMilliseconds);
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
            try
            {
                await _循环.WaitAsync(TimeSpan.FromSeconds(5), 取消令牌).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // 循环卡住是缺陷征兆，不是正常关闭，必须留痕
                _日志.LogWarning("等待历史记录采集循环退出超时（5s），已放弃等待");
            }
            catch (OperationCanceledException)
            {
                // 外部取消（程序退出），正常
            }
        }

        _取消令牌源.Dispose();
        _取消令牌源 = null;
        _日志.LogInformation("历史记录采集已停止");
    }

    // ── 采集循环 ────────────────────────────────────────────────────────

    private async Task 异步采集循环(CancellationToken 取消令牌)
    {
        while (!取消令牌.IsCancellationRequested)
        {
            try
            {
                if (_客户端.是否已连接)
                {
                    await 异步采集(取消令牌).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception 异常)
            {
                // 单轮失败不能打断循环：一次网络抖动不该让后续所有动作都不再入档
                _日志.LogWarning(异常, "历史记录采集失败，将在下个周期重试");
            }

            try
            {
                await Task.Delay(扫描间隔, 取消令牌).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 读一次触发位并推进采集状态机：
    /// <b>上升沿 → 读取数据项 → 落盘 → 复位触发位</b>（与 PLC 的握手）。
    ///
    /// <para><b>握手语义：</b>PLC 置位后只管等复位——读取失败就不复位，
    /// 位保持 1，下一周期继续重试，直到把数据完整读走才交还；任何一环失败
    /// 都不会出现"半条记录"或"PLC 干等"的单边状态。</para>
    /// </summary>
    private async Task 异步采集(CancellationToken 取消令牌)
    {
        var 配置 = _配置;

        if (!配置.启用 || 配置.数据项集合.Count == 0)
        {
            return;
        }

        if (!_解析器.尝试解析(配置.触发地址, out var 触发位) || 触发位 is null || 触发位.元素 != 软元素.M)
        {
            提示配置错误($"触发位「{配置.触发地址}」不是有效的 M 区地址，历史记录不会产生。");
            return;
        }

        var 线圈 = await _客户端
            .异步读取线圈(触发位.Modbus地址, 1, 取消令牌)
            .ConfigureAwait(false);

        var 当前 = 线圈.Length > 0 && 线圈[0];
        var 上次 = _上次触发;
        _上次触发 = 当前;

        if (!当前)
        {
            return;
        }

        // 启动后第一次读到 1（基线尚未建立）不记录：那是"位本来就亮着"，不是这次动作产生的沿
        var 是上升沿 = 上次 == false;

        if (!是上升沿 && !_待复位 && !_读取重试中)
        {
            // 位持续为 1 且没有任何待办：关闭"读取后复位"时由 PLC 管理该位，
            // 保持期间绝不能重复记录；开启时正常流程早被我们复位，不会走到这里
            return;
        }

        if (_待复位)
        {
            if (配置.读取后复位触发位)
            {
                // 上一条记录已落盘但复位没写出去：先补握手，绝不重复记录
                await 复位触发位(触发位, 取消令牌).ConfigureAwait(false);
            }
            else
            {
                // 配置已改为 PLC 自管触发位：放弃补复位
                _待复位 = false;
            }

            return;
        }

        var 值列表 = await 异步读取数据项(配置, 取消令牌).ConfigureAwait(false);

        if (值列表 is null)
        {
            // 读取失败：不复位（PLC 的握手继续等待），位仍为 1，下一周期重试。
            // "没记录"还能从日志与报警里查到原因，半条记录却会让追溯的人信以为真
            _读取重试中 = true;

            if (!_读取失败已提示)
            {
                _读取失败已提示 = true;
                _日志.LogWarning("触发位 {Address} 的数据项读取失败，本条暂不落盘也不复位，将持续重试",
                    配置.触发地址);
            }

            return;
        }

        _读取重试中 = false;
        _读取失败已提示 = false;

        // 工单号 = 触发这一刻"上一次扫码的结果"（只存内存，不落盘）
        var 条目 = new 历史记录条目
        {
            时刻 = _时钟(),
            数值集合 = 值列表,
            工单号 = _扫码枪?.工单号 ?? string.Empty,
        };

        // 无论落盘成功、达到单日上限（返回 false）还是写盘抛异常，都必须把触发位
        // 交还 PLC：否则位恒为 1，下一轮被上面的守卫直接 return，此后再无任何记录
        // （现场表现：历史记录某天起全部消失且无线索）。曾经只有 if(await ...) 的
        // 成功分支才复位——达上限与写盘失败两条路径全漏。
        var 已落盘 = false;

        try
        {
            已落盘 = await _存储.异步追加(条目, 取消令牌).ConfigureAwait(false);
        }
        finally
        {
            if (配置.读取后复位触发位)
            {
                _待复位 = true;
                await 复位触发位(触发位, CancellationToken.None).ConfigureAwait(false);
            }
        }

        if (!已落盘)
        {
            // 达到单日上限或存储拒绝：本条不推送事件，但握手已完成，PLC 可继续置位
            _日志.LogWarning("历史记录「{Title}」未落盘（单日 {Max} 条上限或存储不可用），本条已丢弃",
                配置.标题, 历史记录存储.单日最多记录条数);
            return;
        }

        _日志.LogInformation("已记录 {Title}：{Time:HH:mm:ss}，{Count} 个数据项",
            配置.标题, 条目.时刻, 值列表.Count);

        记录已新增?.Invoke(this, 条目.克隆());
    }

    /// <summary>
    /// 把触发位复位为 0（读取完成握手）。
    ///
    /// <para><b>写失败不在这里吞：</b>异常抛给采集循环统一记日志；
    /// 调用方已提前置位 <see cref="_待复位"/>，下个周期会自动补一次复位，
    /// 触发位不会卡在 1 让 PLC 干等。</para>
    /// </summary>
    private async Task 复位触发位(PLC地址 触发位, CancellationToken 取消令牌)
    {
        await _客户端
            .异步写入单个线圈(触发位.Modbus地址, false, 取消令牌)
            .ConfigureAwait(false);

        _待复位 = false;

        // 复位成功即视为电平已归零：若 PLC 随即再次置位，下一轮读到的是新的上升沿，
        // 不会因为本轮采样值恰为 1 而漏掉那条记录
        _上次触发 = false;

        _日志.LogInformation("已复位触发位 {Address}（数据读取完成）", 触发位.来源);
    }

    // ── 查询 / 导出 / 清除 ──────────────────────────────────────────────

    /// <inheritdoc />
    public Task<IReadOnlyList<历史记录条目>> 异步读取日(
        DateTime 日期, CancellationToken 取消令牌 = default) =>
        _存储.异步读取日(日期, 取消令牌);

    /// <inheritdoc />
    public IReadOnlyList<DateTime> 有记录的日期集合() => _存储.有记录的日期集合();

    /// <inheritdoc />
    public Task 异步清除全部(CancellationToken 取消令牌 = default)
    {
        _权限.要求(权限.编辑历史记录);

        _存储.清除全部();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task 异步导出CSV到文件(
        string 文件路径, DateTime 日期, CancellationToken 取消令牌 = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(文件路径);

        var 条目列表 = await _存储.异步读取日(日期, 取消令牌).ConfigureAwait(false);
        var 文本 = 历史记录CSV编解码器.编码(_配置, 条目列表);

        var 目录 = Path.GetDirectoryName(文件路径);

        if (!string.IsNullOrEmpty(目录))
        {
            Directory.CreateDirectory(目录);
        }

        // 必须带 BOM：否则 Excel 双击打开时中文会乱码
        await File.WriteAllTextAsync(
                文件路径, 文本,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                取消令牌)
            .ConfigureAwait(false);

        _日志.LogInformation("{Date:yyyy-MM-dd} 的历史记录已导出：{Path}（{Count} 条）",
            日期.Date, 文件路径, 条目列表.Count);
    }
}
