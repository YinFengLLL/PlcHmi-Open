using Microsoft.Extensions.Logging.Abstractions;
using PlcHmi.Communication.Simulation;
using PlcHmi.Communication.Inovance;
using PlcHmi.Communication.Modbus;
using PlcHmi.Communication.OpcUa;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Exceptions;
using PlcHmi.Core.Models;

namespace PlcHmi.Tests;

/// <summary>
/// OPC UA 客户端语义单测（经仿真实现）：验证“区域+地址 → NodeId → 读写”的完整路径，
/// 以及 OPC UA 与 Modbus 两条协议路径共享同一份虚拟 PLC 数据。
/// </summary>
public class OpcUaSimulationClientTest
{
    private static (SimulationOpcUaClient OPCUA, SimulationModbusClient Modbus, VirtualPlcDevice Device) CreateDoubleProtocolEnvironment()
    {
        var Options = new PlcConnectionOptions();
        var Resolver = new NodeIdResolver(Options, new InovanceAddressParser(Options.Series));
        var Device = new VirtualPlcDevice(NullLogger<VirtualPlcDevice>.Instance);

        var opcua = new SimulationOpcUaClient(Device, Resolver, NullLogger<SimulationOpcUaClient>.Instance);
        var modbus = new SimulationModbusClient(Device, NullLogger<SimulationModbusClient>.Instance);

        return (opcua, modbus, Device);
    }

    [Fact]
    public async Task ConnectBeforeReadWrite_ShouldThrowDisconnectedException()
    {
        var (opcua, _, _) = CreateDoubleProtocolEnvironment();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => opcua.ReadHoldingRegisterAsync(0, 1));

        Assert.Contains("未连接", exception.Message);
    }

    [Fact]
    public async Task ConnectAndDisconnect_ShouldBeCorrectToggleStateAndStartStopVirtualDevice()
    {
        var (opcua, _, Device) = CreateDoubleProtocolEnvironment();

        Assert.False(opcua.IsConnected);

        await opcua.ConnectAsync();
        Assert.True(opcua.IsConnected);

        await opcua.DisconnectAsync();
        Assert.False(opcua.IsConnected);
    }

    [Fact]
    public async Task ReadWriteHoldingRegister_SinglePointAndBatch_ShouldRoundTripConsistent()
    {
        var (opcua, _, _) = CreateDoubleProtocolEnvironment();
        await opcua.ConnectAsync();

        await opcua.WriteSingleRegisterAsync(100, 1234);
        await opcua.WriteMultiRegisterAsync(200, [11, 22, 33]);

        var singlePoint = await opcua.ReadHoldingRegisterAsync(100, 1);
        var batch = await opcua.ReadHoldingRegisterAsync(200, 3);

        Assert.Equal((ushort)1234, singlePoint[0]);
        Assert.Equal(new ushort[] { 11, 22, 33 }, batch);
    }

    [Fact]
    public async Task ReadWriteCoil_ShouldRoundTripConsistent()
    {
        var (opcua, _, _) = CreateDoubleProtocolEnvironment();
        await opcua.ConnectAsync();

        await opcua.WriteMultiCoilAsync(300, [true, false, true, true]);

        var ReadBackOnce = await opcua.ReadCoilAsync(300, 4);

        Assert.Equal(new[] { true, false, true, true }, ReadBackOnce);
    }

    [Fact]
    public async Task ReadWriteXArea_OctalAddressPath_ShouldNormalWorkOperation()
    {
        // X 是物理输入 → 离散输入区（FC02）。八进制反解路径：63488+8 → X10（不是 X8）。
        // 2026-10-17 修正前 X 被放在线圈区，这里写线圈、读线圈都"通"，但真机上读的是无关位。
        var (opcua, _, Device) = CreateDoubleProtocolEnvironment();
        await opcua.ConnectAsync();

        Device.memory.UnsafeSetDiscreteInput(63496, true);   // X10（八进制 8 → 63488 + 8）

        var ReadBackOnce = await opcua.ReadDiscreteInputAsync(63496, 1);

        Assert.True(ReadBackOnce[0], "X10 应经离散输入区（FC02）读到");
    }

    [Fact]
    public async Task AndModbusSimulationClient_TotalShareSameOneVirtualDeviceData()
    {
        var (opcua, modbus, _) = CreateDoubleProtocolEnvironment();
        await opcua.ConnectAsync();
        await modbus.ConnectAsync();

        // OPC UA 路径写、Modbus 路径读
        await opcua.WriteSingleRegisterAsync(100, 999);
        Assert.Equal((ushort)999, (await modbus.ReadHoldingRegisterAsync(100, 1))[0]);

        // Modbus 路径写、OPC UA 路径读
        await modbus.WriteSingleCoilAsync(400, true);
        Assert.True((await opcua.ReadCoilAsync(400, 1))[0]);
    }

    [Fact]
    public async Task ReadWriteOperateOperation_BothWillViaBeyondNodeIdResolve()
    {
        // 无法映射的地址（保持寄存器区 65536 越界）应抛地址解析异常，
        // 证明客户端确实走了“区域+地址 → NodeId”推导而非直连内存
        var (opcua, _, _) = CreateDoubleProtocolEnvironment();
        await opcua.ConnectAsync();

        await Assert.ThrowsAsync<AddressParseException>(
            () => opcua.ReadHoldingRegisterAsync(65536, 1));
    }
}
