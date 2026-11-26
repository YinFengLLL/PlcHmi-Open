using PlcHmi.Core.Abstractions;

namespace PlcHmi.Tests.Fake;

/// <summary>
/// 可控的 <see cref="IModbusClient"/> 测试替身，用于验证连接管理器的重连状态机。
///
/// <para><b>为什么不用真实模拟器：</b>验证"指数退避"必须能精确控制失败次数与观测尝试时刻。
/// 真实 <c>VirtualPlcDevice</c> 是完整从站，无法按次数强制失败，也拿不到连接尝试的时间戳。
/// 本替身只做两件事：<b>按设定失败 N 次</b>，并<b>记录每次尝试的时间</b>。</para>
/// </summary>
public sealed class FakeModbusClient : IModbusClient
{
    private readonly object _sync = new();
    private readonly List<DateTime> _connectAttemptCount = new();
    private readonly List<(int Address, bool Value)> _coilWrite = new();
    private readonly List<(int Address, ushort Value)> _registerWrite = new();
    private readonly List<(int Address, IReadOnlyList<ushort> Value)> _registerBlockWrite = new();

    private int _remainingFailedCount;
    private volatile bool _isConnected;

    /// <inheritdoc />
    public bool IsConnected => _isConnected;

    /// <summary>连接尝试的时间戳（UTC），按发生顺序排列。</summary>
    public IReadOnlyList<DateTime> ConnectAttemptCount
    {
        get
        {
            lock (_sync)
            {
                return _connectAttemptCount.ToList();
            }
        }
    }

    /// <summary>连接尝试总次数。</summary>
    public int ConnectAttemptQuantity
    {
        get
        {
            lock (_sync)
            {
                return _connectAttemptCount.Count;
            }
        }
    }

    /// <summary>写单个线圈的记录（Modbus 地址、值），按发生顺序。</summary>
    public IReadOnlyList<(int Address, bool Value)> CoilWrite
    {
        get
        {
            lock (_sync)
            {
                return _coilWrite.ToList();
            }
        }
    }

    /// <summary>写单个寄存器的记录（Modbus 地址、值），按发生顺序。</summary>
    public IReadOnlyList<(int Address, ushort Value)> RegisterWrite
    {
        get
        {
            lock (_sync)
            {
                return _registerWrite.ToList();
            }
        }
    }

    /// <summary>
    /// 写多个寄存器的记录（起始地址、寄存器列表），按发生顺序。
    /// <para>供 32 位（DINT / Float32）下发验证字数与字序——红线 R1 要求字序可被独立复算。</para>
    /// </summary>
    public IReadOnlyList<(int Address, IReadOnlyList<ushort> Value)> RegisterBlockWrite
    {
        get
        {
            lock (_sync)
            {
                return _registerBlockWrite.ToList();
            }
        }
    }

    /// <summary>让接下来 <paramref name="countValue"/> 次连接尝试失败；之后的尝试会成功。</summary>
    public void FailedNextConnect(int countValue)
    {
        lock (_sync)
        {
            _remainingFailedCount = countValue;
        }
    }

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            _connectAttemptCount.Add(DateTime.UtcNow);

            if (_remainingFailedCount > 0)
            {
                _remainingFailedCount--;
                _isConnected = false;
                return Task.FromException(new IOException("模拟：连接 PLC 失败"));
            }

            _isConnected = true;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DisconnectAsync()
    {
        _isConnected = false;
        return Task.CompletedTask;
    }

    // ── 读写：返回空数据即可，重连测试不依赖取值 ──────────────────────────

    /// <summary>预设的保持寄存器值（读取时优先查表，查不到按 0）。</summary>
    private readonly Dictionary<int, ushort> _holdingRegisterTable = new();

    /// <summary>批量读取回归用：记录 <c>异步读取保持寄存器</c> 的调用次数。</summary>
    private int _holdingRegisterReadCount;

    /// <summary>保持寄存器读取请求次数（批量合并读取的性能断言用）。</summary>
    public int HoldingRegisterReadCount => Volatile.Read(ref _holdingRegisterReadCount);

    /// <summary>预设的线圈状态（读取时优先查表，查不到按 false）。</summary>
    private readonly Dictionary<int, bool> _coilTable = new();

    /// <summary>
    /// 预设某个保持寄存器的值，供"回读"类用例使用（如参数页延时回读）。
    /// <para>写入（异步写入单个寄存器 / 多个寄存器）也会同步进表，因此"写下再读回"天然一致。</para>
    /// </summary>
    public void PreSetHoldingRegister(int addressValue, ushort Value)
    {
        lock (_sync)
        {
            _holdingRegisterTable[addressValue] = Value;
        }
    }

    /// <summary>预设某个线圈的状态，供回读类用例使用。</summary>
    public void PreSetCoil(int addressValue, bool Value)
    {
        lock (_sync)
        {
            _coilTable[addressValue] = Value;
        }
    }

    /// <inheritdoc />
    public Task<bool[]> ReadCoilAsync(int addressValue, int countValue, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            var ValueList = new bool[countValue];

            for (var Sequence = 0; Sequence < countValue; Sequence++)
            {
                ValueList[Sequence] = _coilTable.TryGetValue(addressValue + Sequence, out var Value) && Value;
            }

            return Task.FromResult(ValueList);
        }
    }

    /// <inheritdoc />
    public Task<bool[]> ReadDiscreteInputAsync(int addressValue, int countValue, CancellationToken cancellationToken = default) =>
        Task.FromResult(new bool[countValue]);

    /// <inheritdoc />
    public Task<ushort[]> ReadHoldingRegisterAsync(int addressValue, int countValue, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _holdingRegisterReadCount);

        lock (_sync)
        {
            var ValueList = new ushort[countValue];

            for (var Sequence = 0; Sequence < countValue; Sequence++)
            {
                ValueList[Sequence] = _holdingRegisterTable.TryGetValue(addressValue + Sequence, out var Value) ? Value : (ushort)0;
            }

            return Task.FromResult(ValueList);
        }
    }

    /// <inheritdoc />
    public Task<ushort[]> ReadInputRegisterAsync(int addressValue, int countValue, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ushort[countValue]);

    /// <inheritdoc />
    public Task WriteSingleCoilAsync(int addressValue, bool Value, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _coilWrite.Add((addressValue, Value));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task WriteSingleRegisterAsync(int addressValue, ushort Value, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _registerWrite.Add((addressValue, Value));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task WriteMultiCoilAsync(
        int addressValue, IReadOnlyList<bool> ValueList, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task WriteMultiRegisterAsync(
        int addressValue, IReadOnlyList<ushort> ValueList, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _registerBlockWrite.Add((addressValue, ValueList.ToList()));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _isConnected = false;
        return ValueTask.CompletedTask;
    }
}
