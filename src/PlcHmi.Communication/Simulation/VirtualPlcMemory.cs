namespace PlcHmi.Communication.Simulation;

/// <summary>
/// 虚拟 PLC 内存映像。模拟一个 Modbus 从站的四个数据区。
/// </summary>
public sealed class VirtualPlcMemory
{
    private readonly object _sync = new();

    private readonly bool[] _coil = new bool[65536];
    private readonly bool[] _discreteInput = new bool[65536];
    private readonly ushort[] _holdingRegister = new ushort[65536];
    private readonly ushort[] _inputRegister = new ushort[65536];

    // ── 读 ──────────────────────────────────────────────────────────────

    public bool[] ReadCoil(int addressValue, int countValue)
    {
        lock (_sync)
        {
            return Segment(_coil, addressValue, countValue);
        }
    }

    public bool[] ReadDiscreteInput(int addressValue, int countValue)
    {
        lock (_sync)
        {
            return Segment(_discreteInput, addressValue, countValue);
        }
    }

    public ushort[] ReadHoldingRegister(int addressValue, int countValue)
    {
        lock (_sync)
        {
            return Segment(_holdingRegister, addressValue, countValue);
        }
    }

    public ushort[] ReadInputRegister(int addressValue, int countValue)
    {
        lock (_sync)
        {
            return Segment(_inputRegister, addressValue, countValue);
        }
    }

    // ── 写 ──────────────────────────────────────────────────────────────

    public void WriteCoil(int addressValue, bool Value)
    {
        lock (_sync)
        {
            _coil[addressValue] = Value;
        }
    }

    public void WriteCoilItem(int addressValue, IReadOnlyList<bool> ValueList)
    {
        lock (_sync)
        {
            for (var Entry = 0; Entry < ValueList.Count; Entry++)
            {
                _coil[addressValue + Entry] = ValueList[Entry];
            }
        }
    }

    public void WriteHoldingRegister(int addressValue, ushort Value)
    {
        lock (_sync)
        {
            _holdingRegister[addressValue] = Value;
        }
    }

    public void WriteHoldingRegisterItem(int addressValue, IReadOnlyList<ushort> ValueList)
    {
        lock (_sync)
        {
            for (var Entry = 0; Entry < ValueList.Count; Entry++)
            {
                _holdingRegister[addressValue + Entry] = ValueList[Entry];
            }
        }
    }

    // ── 模拟器内部使用（不加锁直接访问，调用方自行保证）────────────────

    /// <summary>直接设置线圈值（模拟器内部使用）。</summary>
    public void UnsafeSetCoil(int addressValue, bool Value) => _coil[addressValue] = Value;

    /// <summary>直接设置离散输入值（模拟器内部使用）。</summary>
    public void UnsafeSetDiscreteInput(int addressValue, bool Value) => _discreteInput[addressValue] = Value;

    /// <summary>直接读取线圈值（模拟器内部使用）。</summary>
    public bool UnsafeGetCoil(int addressValue) => _coil[addressValue];

    /// <summary>直接读取离散输入值（模拟器内部使用）。X 是物理输入，读的是这一区。</summary>
    public bool UnsafeGetDiscreteInput(int addressValue) => _discreteInput[addressValue];

    /// <summary>直接设置保持寄存器值（模拟器内部使用）。</summary>
    public void UnsafeSetHoldingRegister(int addressValue, ushort Value) => _holdingRegister[addressValue] = Value;

    /// <summary>直接读取保持寄存器值（模拟器内部使用）。</summary>
    public ushort UnsafeGetHoldingRegister(int addressValue) => _holdingRegister[addressValue];

    /// <summary>批量设置保持寄存器（模拟器内部使用）。</summary>
    public void UnsafeSetHoldingRegisterItem(int StartAddress, ReadOnlySpan<ushort> ValueList)
    {
        for (var Entry = 0; Entry < ValueList.Length; Entry++)
        {
            _holdingRegister[StartAddress + Entry] = ValueList[Entry];
        }
    }

    /// <summary>模拟器内部写操作加锁范围。</summary>
    public void RunAloneOccupy(Action action)
    {
        lock (_sync)
        {
            action();
        }
    }

    private static T[] Segment<T>(T[] Source, int addressValue, int countValue)
    {
        if (addressValue < 0 || addressValue + countValue > Source.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(addressValue),
                $"模拟器地址越界：address={addressValue}, count={countValue}, 容量={Source.Length}");
        }

        var resultValue = new T[countValue];
        Array.Copy(Source, addressValue, resultValue, 0, countValue);
        return resultValue;
    }
}
