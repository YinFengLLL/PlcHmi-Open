using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using 上位机.通信.Modbus;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// 机种（配方）服务。
///
/// <para><b>数据边界：</b>机种 = D2500–D3999 的寄存器快照（<c>recipes.json</c>）；
/// 区段之外的参数是全局记忆（<c>parameters.json</c>），切机种不触碰。</para>
///
/// <para><b>写入时机硬约束：</b>写 PLC 只允许在——
/// ① 操作员按下「下发到 PLC」/「下发机种」；② 切换机种后自动下发一次区段快照。
/// 输入框改动只改内存，绝不直接落 PLC。</para>
/// </summary>
public sealed partial class 配方服务 : 配方服务接口
{
    /// <summary>逐项写参数之间的间隔，避免把 PLC 缓冲打满。</summary>
    private static readonly TimeSpan 写入间隔 = TimeSpan.FromMilliseconds(20);

    /// <summary>参数整数比较容差（参数是 UInt16，但配置里是 double）。</summary>
    private const double 参数容差 = 0.001;

    /// <summary>
    /// 机种区段单次读写的字数上限。
    /// <para>Modbus 单次最多 125 个寄存器，这里取 100 留余量——
    /// 一段 1001 个字要分 11 次读写，不能一把梭。</para>
    /// </summary>
    private const int 区段块大小 = 100;

    private readonly Modbus客户端接口 _客户端;
    private readonly 配方存储 _存储;
    private readonly 参数存储 _参数库;
    private readonly 配方配置 _配置;
    private readonly ILogger<配方服务> _日志;

    /// <summary>切机种 / 下发参数属工程师及以上：都会真实改变设备的运行数据。</summary>
    private readonly 权限守卫接口 _权限;

    private int _当前号 = 1;

    public 配方服务(
        Modbus客户端接口 客户端,
        配方存储 存储,
        参数存储 参数库,
        配方配置 配置对象,
        权限守卫接口 权限,
        ILogger<配方服务> 日志)
    {
        _客户端 = 客户端;
        _存储 = 存储;
        _参数库 = 参数库;
        _配置 = 配置对象;
        _权限 = 权限;
        _日志 = 日志;
    }

    /// <inheritdoc />
    public int 配方数量 => _配置.配方数量;

    /// <inheritdoc />
    public int 当前配方号 => _当前号;

    /// <inheritdoc />
    public string 当前配方名称 => _存储.查找(_当前号)?.名称 ?? string.Empty;

    /// <inheritdoc />
    public IReadOnlyList<配方参数规格> 参数规格 => _配置.参数;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, double> 当前参数 => _参数库.参数;

    /// <inheritdoc />
    public 一致性校验报告? 最后一次一致性报告 { get; private set; }

    /// <inheritdoc />
    public IReadOnlyList<配方信息> 配方集合
    {
        get
        {
            var 列表 = new List<配方信息>(_配置.配方数量);

            for (var 号 = 1; 号 <= _配置.配方数量; 号++)
            {
                var 数据 = _存储.查找(号);

                列表.Add(new 配方信息
                {
                    号 = 号,
                    名称 = 数据?.名称 ?? string.Empty,
                    有数据 = 数据 is { } 条目项 && !条目项.是否空,
                    寄存器数量 = 数据?.区段快照.Count ?? 0,
                });
            }

            return 列表;
        }
    }

    /// <inheritdoc />
    public event EventHandler? 配方已变更;

    /// <inheritdoc />
    public event EventHandler? 参数已变更;

    // ── 初始化 ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<一致性校验报告> 异步初始化(CancellationToken 取消令牌 = default)
    {
        // 1. 机种号：优先取 PLC 实际值（断电重启后它才代表现场真相）。
        // 2026-10-21 起机种区段不参与周期采集，改为开机 / 连上后直接读一次（见 RecipeService.PlcNo.cs）。
        var PLC号 = await 异步读取PLC机种号(取消令牌).ConfigureAwait(false);
        _当前号 = PLC号 > 0 ? PLC号 : Math.Max(1, _存储.当前配方号);

        // 与存储层同步真相：示教落盘（TeachService.异步落盘）用的是
        // _存储.当前配方号。此处不同步会导致"内存按 PLC 机种号、落盘按
        // 旧本地号"两份真相分裂——示教点被写进错误机种，覆盖其点表。
        _存储.设置当前(_当前号);

        _日志.LogInformation("当前机种：{No}（来源：{Source}）",
            _当前号, PLC号 > 0 ? "PLC" : "本地文件");

        // 2. 载入该机种的参数与点表
        加载当前配方();

        // 3. 全局参数（机种区段之外的 D 区）：从 PLC 读回来刷新 —— PLC 是权威。
        //    M 区不含在内：它由轮询引擎按 tags.json 持续刷新。
        var 上报 = await 异步从PLC同步参数(取消令牌).ConfigureAwait(false);
        最后一次一致性报告 = 上报;

        if (上报.是否一致)
        {
            _日志.LogInformation("启动同步完成：{Summary}", 上报.摘要);
        }
        else
        {
            _日志.LogInformation(
                "启动同步：已按 PLC 刷新 {Count} 项全局参数{NewLine}{Details}",
                上报.差异数量, Environment.NewLine, string.Join(Environment.NewLine, 上报.差异));

            // 落盘留底（离线启动时仍能显示上次同步到的值）
            await 存储当前本地(取消令牌).ConfigureAwait(false);
            当参数已变更();
        }

        配方已变更?.Invoke(this, EventArgs.Empty);
        return 上报;
    }

    // 切换机种见 RecipeService.Switch.cs（partial）。

    // ── 参数下发 ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task 异步写入参数(CancellationToken 取消令牌 = default)
    {
        _权限.要求(权限.编辑参数值列表);

        await 异步写入参数核心(取消令牌).ConfigureAwait(false);

        // 双写要求：参数写入 PLC 的同时留底到 parameters.json
        await 存储当前本地(取消令牌).ConfigureAwait(false);

        _日志.LogInformation("全局参数已下发并留底（当前机种 {No}）", _当前号);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> 异步读取PLC参数(CancellationToken 取消令牌 = default)
    {
        // 读回会覆盖本地参数值 —— 是写本地数据的操作，不是只读
        _权限.要求(权限.编辑参数值列表);

        if (!_客户端.是否已连接)
        {
            throw new InvalidOperationException("PLC 未连接，无法读取参数。");
        }

        var 变更 = new List<string>();
        var 错误列表 = new List<string>();
        var 已解码值列表 = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var 规格项 in _配置.参数)
        {
            // 按配置声明类型读取（Float32 占 2 字），与下发编码同源
            var 寄存器列表 = await _客户端
                .异步读取保持寄存器(规格项.地址, 规格项.寄存器数量, 取消令牌)
                .ConfigureAwait(false);

            if (寄存器列表.Length < 规格项.寄存器数量)
            {
                错误列表.Add(
                    $"{规格项.显示名称}：PLC 只返回 {寄存器列表.Length} 个字"
                    + $"（D{规格项.地址} 需要 {规格项.寄存器数量} 个）。");
                continue;
            }

            var 值 = Convert.ToDouble(
                寄存器编解码器.解码(规格项.数据类型, 寄存器列表, 字顺序.小字节序),
                CultureInfo.InvariantCulture);

            // 服务层范围校验：与下发同源。PLC 里被手工改成超范围值时不允许静默搬进上位机
            if (值 < 规格项.最小 || 值 > 规格项.最大)
            {
                错误列表.Add(
                    $"{规格项.显示名称}：PLC 值 {值:0.###} 超出允许范围 [{规格项.最小:0.###}, {规格项.最大:0.###}]。");
                continue;
            }

            已解码值列表[规格项.键] = 值;

            _参数库.参数.TryGetValue(规格项.键, out var 本地);
            if (Math.Abs(值 - 本地) > 参数容差)
            {
                变更.Add($"{规格项.显示名称}：{本地:0.###} → {值:0.###}");
            }
        }

        // 任一项非法就整体拒绝：绝不允许"读回一半"造成本地与 PLC 各错一半
        if (错误列表.Count > 0)
        {
            throw new InvalidOperationException(string.Join("\n", 错误列表));
        }

        // 全部合法才覆盖内存并留底（与下发的双写对称）
        foreach (var (键值, 值) in 已解码值列表)
        {
            _参数库.参数[键值] = 值;
        }

        await 存储当前本地(取消令牌).ConfigureAwait(false);

        _日志.LogInformation(
            "已从 PLC 读取全局参数：{Total} 项，{Changed} 项与本地不同",
            _配置.参数.Count, 变更.Count);

        if (变更.Count > 0)
        {
            参数已变更?.Invoke(this, EventArgs.Empty);
        }

        return 变更;
    }

    /// <summary>把当前参数逐项写入参数区。含范围校验。</summary>
    private async Task 异步写入参数核心(CancellationToken 取消令牌)
    {
        if (!_客户端.是否已连接)
        {
            throw new InvalidOperationException("PLC 未连接，无法下发参数。");
        }

        foreach (var 规格项 in _配置.参数)
        {
            var 值 = _参数库.参数.TryGetValue(规格项.键, out var 取到的值) ? 取到的值 : 规格项.默认;

            if (!规格项.是否在范围内(值))
            {
                throw new InvalidOperationException(
                    $"{规格项.显示名称} 的值 {值:0.###} 超出允许范围 {规格项.范围文本}，已拒绝下发。");
            }

            // 按配置声明的类型编码，并按占用字数选择单字 / 多字写入。
            // Float32 占 2 字：若仍用 WriteSingleRegisterAsync，只会写进低字，
            // PLC 读到的是错位的值（这正是 2026-09-12 现场实测发现的问题）。
            var 寄存器列表 = 寄存器编解码器.编码(规格项.数据类型, 值, 字顺序.小字节序);

            if (寄存器列表.Length == 1)
            {
                await _客户端.异步写入单个寄存器(规格项.地址, 寄存器列表[0], 取消令牌)
                    .ConfigureAwait(false);
            }
            else
            {
                await _客户端.异步写入多个寄存器(规格项.地址, 寄存器列表, 取消令牌)
                    .ConfigureAwait(false);
            }

            await Task.Delay(写入间隔, 取消令牌).ConfigureAwait(false);
        }

        _日志.LogInformation("已下发 {Count} 项参数（机种 {No}）", _配置.参数.Count, _当前号);
    }

    // ── 本地持久化 ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task 异步保存本地(CancellationToken 取消令牌 = default) =>
        _参数库.异步保存(取消令牌);

    /// <inheritdoc />
    public void 设置参数(string 键值, double 值)
    {
        // 只改内存值（界面绑定回调会在初始化时触发），真正写 PLC 的
        // 异步写入参数 / 异步读取PLC参数 才有 编辑参数值列表 守卫。
        // 这里加守卫会在「用户尚未登录、Watcher 绑定首值」时抛异常，
        // 打断 async void OnStartup，导致 --smoke 与正常启动都卡死。
        var 规格项 = _配置.查找(键值);
        if (规格项 is null)
        {
            _日志.LogWarning("忽略未知参数键：{Key}", 键值);
            return;
        }

        _参数库.参数[键值] = 值;
        当参数已变更();
    }

    /// <inheritdoc />
    public async Task 异步重命名(int 配方号, string 名称值, CancellationToken 取消令牌 = default)
    {
        // 写本地机种数据（落盘 recipes.json），与读取PLC参数同级：服务层二次校验
        _权限.要求(权限.编辑参数值列表);

        校验配方号(配方号);

        var 数据 = _存储.获取或创建(配方号);
        数据.名称 = 名称值 ?? string.Empty;

        await _存储.异步保存(取消令牌).ConfigureAwait(false);

        _日志.LogInformation("机种 {No} 已命名为「{Name}」", 配方号, 数据.名称);
        配方已变更?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void 恢复默认值()
    {
        // 同上：只改内存值，写 PLC 的下发路径已有权限守卫。
        foreach (var 规格项 in _配置.参数)
        {
            _参数库.参数[规格项.键] = 规格项.默认;
        }

        _日志.LogInformation("全局参数已恢复为默认值（尚未下发 PLC）");
        当参数已变更();
    }

    // ── CSV 导入导出见 RecipeService.Csv.cs（partial）──────────────────

    // ── 内部 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 把全局参数补齐（缺失项用配置默认值）。
    /// <para>参数与机种无关，所以这里只做"补默认"，不做任何按机种的覆盖。</para>
    /// </summary>
    private void 加载当前配方()
    {
        var 参数值 = _参数库.参数;

        foreach (var 规格项 in _配置.参数)
        {
            if (!参数值.ContainsKey(规格项.键))
            {
                参数值[规格项.键] = 规格项.默认;
            }
        }

        _日志.LogDebug("全局参数已就绪：{ParamCount} 项（当前机种 {No}）", 参数值.Count, _当前号);
    }

    /// <summary>把内存中的全局参数写回 <c>parameters.json</c>。</summary>
    private Task 存储当前本地(CancellationToken 取消令牌 = default) =>
        _参数库.异步保存(取消令牌);

    private async Task 异步写入寄存器(int 地址值, int 值, CancellationToken 取消令牌)
    {
        if (!_客户端.是否已连接)
        {
            _日志.LogWarning("PLC 未连接，寄存器 D{Address} 写入被跳过", 地址值);
            return;
        }

        await _客户端.异步写入单个寄存器(地址值, (ushort)值, 取消令牌)
            .ConfigureAwait(false);
    }

    private void 当参数已变更() => 参数已变更?.Invoke(this, EventArgs.Empty);

    private void 校验配方号(int 配方号)
    {
        if (配方号 < 1 || 配方号 > _配置.配方数量)
        {
            throw new ArgumentOutOfRangeException(
                nameof(配方号), 配方号, $"机种号必须在 1 – {_配置.配方数量} 之间。");
        }
    }
}
