using PlcHmi.Core.Abstractions;

namespace PlcHmi.Tests.Fake;

/// <summary>位区对 X / Y 地址段的行为模式（配合 <see cref="FakeBitAreaClient"/> 描述真机差异）。</summary>
public enum BitAreaRowAs
{
    /// <summary>按物理状态表返回（标准固件 / 有真实数据的位区）。</summary>
    ByPhysicalState,

    /// <summary>读取成功但恒为 0（地址在从站里存在、却不承载物理 IO）。</summary>
    AlwaysReadZero,

    /// <summary>读请求被拒绝（非法地址 / 非法功能 → 读取抛异常）。</summary>
    ThrowException,
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
/// （<see cref="XYCoilRowAs"/> / <see cref="XYDiscreteRowAs"/>），其余地址段（M 点等）一律正常应答，
/// 用于验证 X/Y 块的失败不牵连其它采集。各区读取次数带计数，供断言"位区切换收敛后不再试错"。</para>
/// </summary>
public sealed class FakeBitAreaClient : IModbusClient
{
    /// <summary>X 区 Modbus 基址（X0 = 63488）。自此地址起视为物理 IO 段。</summary>
    public const int XBaseAddress = 63488;

    /// <summary>Y 区 Modbus 基址（Y0 = 64512）。X 段线圈读 = 地址在 [X基址, Y基址)。</summary>
    public const int YBaseAddress = 64512;

    private readonly Dictionary<int, bool> _physicalState = new();
    private readonly Dictionary<int, bool> _discreteAdditional = new();
    private readonly Dictionary<int, bool> _coilAdditional = new();
    private readonly object _sync = new();

    private int _physicalIoCoilReadCount;
    private int _physicalIoDiscreteReadCount;
    private int _tagCoilReadCount;
    private int _xSectionCoilReadCount;
    private int _xSectionDiscreteReadCount;

    /// <summary>线圈区（FC01）对 X/Y 地址段的行为。</summary>
    public BitAreaRowAs XYCoilRowAs { private get; set; } = BitAreaRowAs.ByPhysicalState;

    /// <summary>离散输入区（FC02）对 X/Y 地址段的行为。</summary>
    public BitAreaRowAs XYDiscreteRowAs { private get; set; } = BitAreaRowAs.ByPhysicalState;

    /// <summary>物理 IO 点当前的真实状态（按 Modbus 地址存放，两个位区共用一份）。</summary>
    public void SetPhysicalState(int addressValue, bool Value)
    {
        lock (_sync)
        {
            _physicalState[addressValue] = Value;
        }
    }

    /// <summary>
    /// 设置<b>仅离散输入区可见</b>的附加位（按 Modbus 地址存放）。
    /// <para>用于复刻"备用位区里有无关数据"：当 X 的主位区是离散输入区（FC02）时，
    /// 无关位在线圈区；反之亦然。回退逻辑必须只信任"当前位区从未有过信号"这个前提。</para>
    /// </summary>
    public void SetDiscreteAdditionalBit(int addressValue, bool Value)
    {
        lock (_sync)
        {
            _discreteAdditional[addressValue] = Value;
        }
    }

    /// <summary>设置<b>仅线圈区可见</b>的附加位（按 Modbus 地址存放）。语义同 <see cref="SetDiscreteAdditionalBit"/>。</summary>
    public void SetCoilAdditionalBit(int addressValue, bool Value)
    {
        lock (_sync)
        {
            _coilAdditional[addressValue] = Value;
        }
    }

    /// <summary>在 X / Y 地址段尝试读线圈（FC01）的次数。</summary>
    public int PhysicalIoCoilReadCount => Volatile.Read(ref _physicalIoCoilReadCount);

    /// <summary>在 X / Y 地址段读离散输入（FC02）的次数。</summary>
    public int PhysicalIoDiscreteReadCount => Volatile.Read(ref _physicalIoDiscreteReadCount);

    /// <summary>非物理 IO 段（如 M 点）的线圈读取次数 —— 用于证明其它采集照常进行。</summary>
    public int TagCoilReadCount => Volatile.Read(ref _tagCoilReadCount);

    /// <summary>
    /// X 地址段（&lt; Y 基址 64512）的线圈读取次数 —— 用于验证 X 块是否走了 FC01
    /// （Y 块按缺省仍在线圈区，用总计数区分不了两者）。
    /// </summary>
    public int XSectionCoilReadCount => Volatile.Read(ref _xSectionCoilReadCount);

    /// <summary>X 地址段（&lt; Y 基址 64512）的离散输入读取次数 —— 同上，用于验证 X 块是否走了 FC02。</summary>
    public int XSectionDiscreteReadCount => Volatile.Read(ref _xSectionDiscreteReadCount);

    /// <inheritdoc />
    public bool IsConnected => true;

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task DisconnectAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public Task<bool[]> ReadCoilAsync(int addressValue, int countValue, CancellationToken cancellationToken = default)
    {
        if (addressValue >= XBaseAddress)
        {
            Interlocked.Increment(ref _physicalIoCoilReadCount);

            if (addressValue < YBaseAddress)
            {
                Interlocked.Increment(ref _xSectionCoilReadCount);
            }

            return Task.FromResult(ReadByRowAs(XYCoilRowAs, addressValue, countValue, _coilAdditional));
        }

        Interlocked.Increment(ref _tagCoilReadCount);
        return Task.FromResult(new bool[countValue]);
    }

    /// <inheritdoc />
    public Task<bool[]> ReadDiscreteInputAsync(int addressValue, int countValue, CancellationToken cancellationToken = default)
    {
        if (addressValue >= XBaseAddress)
        {
            Interlocked.Increment(ref _physicalIoDiscreteReadCount);

            if (addressValue < YBaseAddress)
            {
                Interlocked.Increment(ref _xSectionDiscreteReadCount);
            }

            return Task.FromResult(ReadByRowAs(XYDiscreteRowAs, addressValue, countValue, _discreteAdditional));
        }

        return Task.FromResult(new bool[countValue]);
    }

    /// <summary>按行为模式生成一段位读取结果（抛异常 / 恒 0 / 按物理状态）。</summary>
    private bool[] ReadByRowAs(BitAreaRowAs rowAs, int addressValue, int countValue, Dictionary<int, bool> additionalBitTable)
    {
        if (rowAs == BitAreaRowAs.ThrowException)
        {
            throw new InvalidOperationException("Modbus 异常响应：非法数据地址");
        }

        var ValueList = new bool[countValue];

        if (rowAs == BitAreaRowAs.AlwaysReadZero)
        {
            return ValueList;
        }

        lock (_sync)
        {
            for (var Entry = 0; Entry < countValue; Entry++)
            {
                // 物理状态（两位区共用）+ 仅当前位区可见的附加位
                // （附加位用于复刻"另一位区有无关数据"这一现场失效形态）
                var physical = _physicalState.TryGetValue(addressValue + Entry, out var Value) && Value;
                var Additional = additionalBitTable.TryGetValue(addressValue + Entry, out var additionalValue) && additionalValue;
                ValueList[Entry] = physical || Additional;
            }
        }

        return ValueList;
    }

    /// <inheritdoc />
    public Task<ushort[]> ReadHoldingRegisterAsync(int addressValue, int countValue, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ushort[countValue]);

    /// <inheritdoc />
    public Task<ushort[]> ReadInputRegisterAsync(int addressValue, int countValue, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ushort[countValue]);

    /// <inheritdoc />
    public Task WriteSingleCoilAsync(int addressValue, bool Value, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task WriteSingleRegisterAsync(int addressValue, ushort Value, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task WriteMultiCoilAsync(int addressValue, IReadOnlyList<bool> ValueList, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task WriteMultiRegisterAsync(int addressValue, IReadOnlyList<ushort> ValueList, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
