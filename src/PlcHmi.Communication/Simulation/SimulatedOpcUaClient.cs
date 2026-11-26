using Microsoft.Extensions.Logging;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Communication.OpcUa;

namespace PlcHmi.Communication.Simulation;

/// <summary>
/// 模拟 OPC UA 客户端。与 <see cref="SimulationModbusClient"/> 共用 <see cref="VirtualPlcDevice"/> 的内存映像——
/// 同一份虚拟 PLC 数据，两条协议路径读写结果一致。
///
/// <para><b>与仿真 Modbus 客户端的差别</b>：每次读写都会先走一遍
/// <see cref="NodeIdResolver"/> 推导——即 OPC UA 模式下真实客户端“区域+地址 → NodeId”的完整路径，
/// 映射规则写错（如八进制还原）在这里立刻暴露。取值仍直接映射虚拟内存，
/// 不真正起 OPC UA 服务端（测试无需引入网络栈）。</para>
/// </summary>
public sealed class SimulationOpcUaClient : IModbusClient
{
    /// <summary>模拟的网络往返延迟，用于贴近真实响应时间。</summary>
    private static readonly TimeSpan simulationWhenDelay = TimeSpan.FromMilliseconds(3);

    private readonly VirtualPlcDevice _device;
    private readonly NodeIdResolver _resolver;
    private readonly ILogger<SimulationOpcUaClient> _log;
    private bool _connected;
    private bool _disposed;

    public SimulationOpcUaClient(VirtualPlcDevice Device, NodeIdResolver Resolver, ILogger<SimulationOpcUaClient> Log)
    {
        _device = Device;
        _resolver = Resolver;
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
        _log.LogInformation("已连接虚拟 PLC（OPC UA 模拟模式）");
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
        PreResolveSectionPoint(ModbusArea.Coil, addressValue, countValue);
        return _device.memory.ReadCoil(addressValue, countValue);
    }

    /// <inheritdoc />
    public async Task<bool[]> ReadDiscreteInputAsync(int addressValue, int countValue, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        PreResolveSectionPoint(ModbusArea.DiscreteInput, addressValue, countValue);
        return _device.memory.ReadDiscreteInput(addressValue, countValue);
    }

    /// <inheritdoc />
    public async Task<ushort[]> ReadHoldingRegisterAsync(int addressValue, int countValue, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        PreResolveSectionPoint(ModbusArea.HoldingRegister, addressValue, countValue);
        return _device.memory.ReadHoldingRegister(addressValue, countValue);
    }

    /// <inheritdoc />
    public async Task<ushort[]> ReadInputRegisterAsync(int addressValue, int countValue, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        PreResolveSectionPoint(ModbusArea.InputRegister, addressValue, countValue);
        return _device.memory.ReadInputRegister(addressValue, countValue);
    }

    // ── 写 ──────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task WriteSingleCoilAsync(int addressValue, bool Value, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        PreResolveSectionPoint(ModbusArea.Coil, addressValue, 1);
        _device.memory.WriteCoil(addressValue, Value);
    }

    /// <inheritdoc />
    public async Task WriteSingleRegisterAsync(int addressValue, ushort Value, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        PreResolveSectionPoint(ModbusArea.HoldingRegister, addressValue, 1);
        _device.memory.WriteHoldingRegister(addressValue, Value);
    }

    /// <inheritdoc />
    public async Task WriteMultiCoilAsync(int addressValue, IReadOnlyList<bool> ValueList, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        PreResolveSectionPoint(ModbusArea.Coil, addressValue, ValueList.Count);
        _device.memory.WriteCoilItem(addressValue, ValueList);
    }

    /// <inheritdoc />
    public async Task WriteMultiRegisterAsync(int addressValue, IReadOnlyList<ushort> ValueList, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        PreResolveSectionPoint(ModbusArea.HoldingRegister, addressValue, ValueList.Count);
        _device.memory.WriteHoldingRegisterItem(addressValue, ValueList);
    }

    /// <summary>
    /// 走一遍 NodeId 推导，验证本次操作涉及的全部地址都能映射到合法节点。
    /// <para>推导失败（地址不属于任何软元件区间）抛出与真实客户端一致的异常。</para>
    /// </summary>
    private void PreResolveSectionPoint(ModbusArea areaValue, int startAddress, int countValue)
    {
        for (var Entry = 0; Entry < countValue; Entry++)
        {
            _ = _resolver.Resolve(areaValue, startAddress + Entry);
        }
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
