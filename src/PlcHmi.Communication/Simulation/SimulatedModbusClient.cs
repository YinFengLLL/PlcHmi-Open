using Microsoft.Extensions.Logging;
using PlcHmi.Core.Abstractions;

namespace PlcHmi.Communication.Simulation;

/// <summary>
/// 模拟 Modbus 客户端。把读写请求直接映射到 <see cref="VirtualPlcDevice"/> 的内存映像上。
/// <para>用途：没有真实 PLC 时也能开发与自测完整业务逻辑，
/// 现场调试前先在模拟器上把流程跑通。</para>
/// </summary>
public sealed class SimulationModbusClient : IModbusClient
{
    /// <summary>模拟的网络往返延迟，用于贴近真实响应时间。</summary>
    private static readonly TimeSpan simulationWhenDelay = TimeSpan.FromMilliseconds(3);

    private readonly VirtualPlcDevice _device;
    private readonly ILogger<SimulationModbusClient> _log;
    private bool _connected;
    private bool _disposed;

    public SimulationModbusClient(VirtualPlcDevice Device, ILogger<SimulationModbusClient> Log)
    {
        _device = Device;
        _log = Log;
    }

    /// <inheritdoc />
    public bool IsConnected => _connected;

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(simulationWhenDelay, cancellationToken).ConfigureAwait(false);
        _connected = true;
        _device.Start();
        _log.LogInformation("已连接虚拟 PLC（模拟模式）");
    }

    /// <inheritdoc />
    public async Task DisconnectAsync()
    {
        await Task.Delay(simulationWhenDelay).ConfigureAwait(false);
        _connected = false;
        await _device.StopAsync().ConfigureAwait(false);
        _log.LogInformation("已断开虚拟 PLC");
    }

    // ── 读 ──────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<bool[]> ReadCoilAsync(int addressValue, int countValue, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        return _device.memory.ReadCoil(addressValue, countValue);
    }

    /// <inheritdoc />
    public async Task<bool[]> ReadDiscreteInputAsync(int addressValue, int countValue, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        return _device.memory.ReadDiscreteInput(addressValue, countValue);
    }

    /// <inheritdoc />
    public async Task<ushort[]> ReadHoldingRegisterAsync(int addressValue, int countValue, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        return _device.memory.ReadHoldingRegister(addressValue, countValue);
    }

    /// <inheritdoc />
    public async Task<ushort[]> ReadInputRegisterAsync(int addressValue, int countValue, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        return _device.memory.ReadInputRegister(addressValue, countValue);
    }

    // ── 写 ──────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task WriteSingleCoilAsync(int addressValue, bool Value, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        _device.memory.WriteCoil(addressValue, Value);
    }

    /// <inheritdoc />
    public async Task WriteSingleRegisterAsync(int addressValue, ushort Value, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        _device.memory.WriteHoldingRegister(addressValue, Value);
    }

    /// <inheritdoc />
    public async Task WriteMultiCoilAsync(int addressValue, IReadOnlyList<bool> ValueList, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        _device.memory.WriteCoilItem(addressValue, ValueList);
    }

    /// <inheritdoc />
    public async Task WriteMultiRegisterAsync(int addressValue, IReadOnlyList<ushort> ValueList, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        _device.memory.WriteHoldingRegisterItem(addressValue, ValueList);
    }

    private async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (!_connected)
        {
            throw new InvalidOperationException("虚拟 PLC 未连接，无法执行读写操作。");
        }

        await Task.Delay(simulationWhenDelay, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DisconnectAsync().ConfigureAwait(false);
    }
}
