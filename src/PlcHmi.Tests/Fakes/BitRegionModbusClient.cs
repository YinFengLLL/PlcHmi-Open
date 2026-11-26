using 上位机.核心.抽象;

namespace 上位机.上位机测试.替身;

/// <summary>位区对 X / Y 地址段的行为模式（配合 <see cref="替身位区客户端"/> 描述真机差异）。</summary>
public enum 位区行为
{
    /// <summary>按物理状态表返回（标准固件 / 有真实数据的位区）。</summary>
    按物理状态,

    /// <summary>读取成功但恒为 0（地址在从站里存在、却不承载物理 IO）。</summary>
    恒读零,

    /// <summary>读请求被拒绝（非法地址 / 非法功能 → 读取抛异常）。</summary>
    抛异常,
}

/// <summary>
/// 可配置位区行为的 Modbus 客户端测试替身，用于钉住真机 X/Y 采集的位区差异。
///
/// <para><b>为什么不用真实模拟器</b>：现场实证（EASY522）同一台 PLC 上，X/Y 的物理状态
/// 不在地址解析给出的线圈区（0xF800 / 0xFC00），且出现过两种失效形态——线圈区读请求
/// <b>无应答</b>（抛异常），以及线圈区<b>读取成功但恒为 0</b>、真实数据在离散输入区（FC02）
/// 同地址（2026-10-13 现场日志为后者：连接稳定、无 IO 告警、X/Y 全 0）。
/// 虚拟 PLC 内存做不到"同一位区读成功却是错的"，只能用替身精确复刻。</para>
///
/// <para>线圈区（FC01）与离散输入区（FC02）对 <b>X / Y 地址段</b>（≥ 63488）的行为各自可配
/// （<see cref="XY线圈行为"/> / <see cref="XY离散行为"/>），其余地址段（M 点等）一律正常应答，
/// 用于验证 X/Y 块的失败不牵连其它采集。各区读取次数带计数，供断言"位区切换收敛后不再试错"。</para>
/// </summary>
public sealed class 替身位区客户端 : Modbus客户端接口
{
    /// <summary>X 区 Modbus 基址（X0 = 63488）。自此地址起视为物理 IO 段。</summary>
    public const int X基址 = 63488;

    /// <summary>Y 区 Modbus 基址（Y0 = 64512）。X 段线圈读 = 地址在 [X基址, Y基址)。</summary>
    public const int Y基址 = 64512;

    private readonly Dictionary<int, bool> _物理状态 = new();
    private readonly Dictionary<int, bool> _离散附加 = new();
    private readonly Dictionary<int, bool> _线圈附加 = new();
    private readonly object _同步 = new();

    private int _物理Io线圈读次数;
    private int _物理Io离散读次数;
    private int _标签线圈读次数;
    private int _X段线圈读次数;
    private int _X段离散读次数;

    /// <summary>线圈区（FC01）对 X/Y 地址段的行为。</summary>
    public 位区行为 XY线圈行为 { private get; set; } = 位区行为.按物理状态;

    /// <summary>离散输入区（FC02）对 X/Y 地址段的行为。</summary>
    public 位区行为 XY离散行为 { private get; set; } = 位区行为.按物理状态;

    /// <summary>物理 IO 点当前的真实状态（按 Modbus 地址存放，两个位区共用一份）。</summary>
    public void 设置物理状态(int 地址值, bool 值)
    {
        lock (_同步)
        {
            _物理状态[地址值] = 值;
        }
    }

    /// <summary>
    /// 设置<b>仅离散输入区可见</b>的附加位（按 Modbus 地址存放）。
    /// <para>用于复刻"备用位区里有无关数据"：当 X 的主位区是离散输入区（FC02）时，
    /// 无关位在线圈区；反之亦然。回退逻辑必须只信任"当前位区从未有过信号"这个前提。</para>
    /// </summary>
    public void 设置离散附加位(int 地址值, bool 值)
    {
        lock (_同步)
        {
            _离散附加[地址值] = 值;
        }
    }

    /// <summary>设置<b>仅线圈区可见</b>的附加位（按 Modbus 地址存放）。语义同 <see cref="设置离散附加位"/>。</summary>
    public void 设置线圈附加位(int 地址值, bool 值)
    {
        lock (_同步)
        {
            _线圈附加[地址值] = 值;
        }
    }

    /// <summary>在 X / Y 地址段尝试读线圈（FC01）的次数。</summary>
    public int 物理Io线圈读次数 => Volatile.Read(ref _物理Io线圈读次数);

    /// <summary>在 X / Y 地址段读离散输入（FC02）的次数。</summary>
    public int 物理Io离散读次数 => Volatile.Read(ref _物理Io离散读次数);

    /// <summary>非物理 IO 段（如 M 点）的线圈读取次数 —— 用于证明其它采集照常进行。</summary>
    public int 标签线圈读次数 => Volatile.Read(ref _标签线圈读次数);

    /// <summary>
    /// X 地址段（&lt; Y 基址 64512）的线圈读取次数 —— 用于验证 X 块是否走了 FC01
    /// （Y 块按缺省仍在线圈区，用总计数区分不了两者）。
    /// </summary>
    public int X段线圈读次数 => Volatile.Read(ref _X段线圈读次数);

    /// <summary>X 地址段（&lt; Y 基址 64512）的离散输入读取次数 —— 同上，用于验证 X 块是否走了 FC02。</summary>
    public int X段离散读次数 => Volatile.Read(ref _X段离散读次数);

    /// <inheritdoc />
    public bool 是否已连接 => true;

    /// <inheritdoc />
    public Task 异步连接(CancellationToken 取消令牌 = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task 异步断开() => Task.CompletedTask;

    /// <inheritdoc />
    public Task<bool[]> 异步读取线圈(int 地址值, int 数量值, CancellationToken 取消令牌 = default)
    {
        if (地址值 >= X基址)
        {
            Interlocked.Increment(ref _物理Io线圈读次数);

            if (地址值 < Y基址)
            {
                Interlocked.Increment(ref _X段线圈读次数);
            }

            return Task.FromResult(读取按行为(XY线圈行为, 地址值, 数量值, _线圈附加));
        }

        Interlocked.Increment(ref _标签线圈读次数);
        return Task.FromResult(new bool[数量值]);
    }

    /// <inheritdoc />
    public Task<bool[]> 异步读取离散输入(int 地址值, int 数量值, CancellationToken 取消令牌 = default)
    {
        if (地址值 >= X基址)
        {
            Interlocked.Increment(ref _物理Io离散读次数);

            if (地址值 < Y基址)
            {
                Interlocked.Increment(ref _X段离散读次数);
            }

            return Task.FromResult(读取按行为(XY离散行为, 地址值, 数量值, _离散附加));
        }

        return Task.FromResult(new bool[数量值]);
    }

    /// <summary>按行为模式生成一段位读取结果（抛异常 / 恒 0 / 按物理状态）。</summary>
    private bool[] 读取按行为(位区行为 行为, int 地址值, int 数量值, Dictionary<int, bool> 附加位表)
    {
        if (行为 == 位区行为.抛异常)
        {
            throw new InvalidOperationException("Modbus 异常响应：非法数据地址");
        }

        var 值列表 = new bool[数量值];

        if (行为 == 位区行为.恒读零)
        {
            return 值列表;
        }

        lock (_同步)
        {
            for (var 条目 = 0; 条目 < 数量值; 条目++)
            {
                // 物理状态（两位区共用）+ 仅当前位区可见的附加位
                // （附加位用于复刻"另一位区有无关数据"这一现场失效形态）
                var 物理 = _物理状态.TryGetValue(地址值 + 条目, out var 值) && 值;
                var 附加 = 附加位表.TryGetValue(地址值 + 条目, out var 附加值) && 附加值;
                值列表[条目] = 物理 || 附加;
            }
        }

        return 值列表;
    }

    /// <inheritdoc />
    public Task<ushort[]> 异步读取保持寄存器(int 地址值, int 数量值, CancellationToken 取消令牌 = default) =>
        Task.FromResult(new ushort[数量值]);

    /// <inheritdoc />
    public Task<ushort[]> 异步读取输入寄存器(int 地址值, int 数量值, CancellationToken 取消令牌 = default) =>
        Task.FromResult(new ushort[数量值]);

    /// <inheritdoc />
    public Task 异步写入单个线圈(int 地址值, bool 值, CancellationToken 取消令牌 = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task 异步写入单个寄存器(int 地址值, ushort 值, CancellationToken 取消令牌 = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task 异步写入多个线圈(int 地址值, IReadOnlyList<bool> 值列表, CancellationToken 取消令牌 = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task 异步写入多个寄存器(int 地址值, IReadOnlyList<ushort> 值列表, CancellationToken 取消令牌 = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
