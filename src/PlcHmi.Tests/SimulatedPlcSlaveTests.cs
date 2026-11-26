using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using PlcHmi.Communication.Modbus;
using PlcHmi.Communication.Simulation;
using PlcHmi.Core.Models;

namespace PlcHmi.Tests;

/// <summary>
/// 模拟 PLC 的 Modbus TCP 从站测试。
///
/// <para><b>守的是什么：</b>「模拟模式下可用 Modbus 工具离线调试」这句承诺，全靠从站能把标准
/// Modbus TCP 帧正确翻译成虚拟内存的读写。若帧格式、位打包、字节序或异常响应有一处不对，
/// 现场表现是"工具连上了但没有数据 / 数据是乱的"—— 这种问题在真机上极难排查。</para>
///
/// <para><b>怎么验：</b>用<b>上位机自己的真实客户端</b> <see cref="FluentModbusTCPClient"/>
/// 去连从站，跑完整往返；再补两条手工原始帧用例覆盖异常响应路径。
/// 这样从站与真实客户端互为对照组，任一方协议实现走样都会立刻红。</para>
/// </summary>
public class VirtualPlcSlaveTest
{
    /// <summary>启动一台从站（端口 0 = 由系统分配，测试不会撞端口）。</summary>
    private static async Task<(VirtualPlcSlave Slave, VirtualPlcDevice Device)> StartSlave()
    {
        var Device = new VirtualPlcDevice(NullLogger<VirtualPlcDevice>.Instance);
        var Options = new PlcConnectionOptions { SimulationSlavePort = 0, SimulationSlaveListenAddress = "127.0.0.1" };
        var Slave = new VirtualPlcSlave(Device, Options, NullLogger<VirtualPlcSlave>.Instance);

        await Slave.LaunchAsync();

        return (Slave, Device);
    }

    /// <summary>用上位机的真实 Modbus 客户端连上从站。</summary>
    private static async Task<FluentModbusTCPClient> ConnectRealClient(int Port)
    {
        var Options = new PlcConnectionOptions
        {
            Host = "127.0.0.1",
            Port = Port,
            UnitId = 1,
            ConnectTimeoutMilliseconds = 3000,
            ReadTimeoutMilliseconds = 3000,
        };

        var Client = new FluentModbusTCPClient(Options, NullLogger<FluentModbusTCPClient>.Instance);
        await Client.ConnectAsync();

        return Client;
    }

    [Fact]
    public async Task Slave_RegisterReadWriteRoundTripShouldPersistToSameOnePartVirtualMemory()
    {
        var (Slave, Device) = await StartSlave();
        await using var slaveRelease = Slave;
        await using var deviceRelease = Device;

        await using var Client = await ConnectRealClient(Slave.Port);

        // 多寄存器写（FC10）→ 读回（FC03）
        await Client.WriteMultiRegisterAsync(100, new ushort[] { 0x1234, 0xABCD });
        Assert.Equal(new ushort[] { 0x1234, 0xABCD }, await Client.ReadHoldingRegisterAsync(100, 2));

        // 外部工具写进去的值，必须与上位机自用的虚拟内存是同一份（不是两份副本）
        Assert.Equal(new ushort[] { 0x1234, 0xABCD }, Device.memory.ReadHoldingRegister(100, 2));

        // 单寄存器写（FC06）
        await Client.WriteSingleRegisterAsync(101, 0x0001);
        Assert.Equal(0x0001, Device.memory.ReadHoldingRegister(101, 1)[0]);
    }

    [Fact]
    public async Task Slave_BitReadWriteRoundTripShouldBeCorrectProcessBitPack()
    {
        var (Slave, Device) = await StartSlave();
        await using var slaveRelease = Slave;
        await using var deviceRelease = Device;

        await using var Client = await ConnectRealClient(Slave.Port);

        // 单线圈写（FC05）
        await Client.WriteSingleCoilAsync(63496, true);   // X10（八进制 8 → 63488 + 8）
        Assert.True((await Client.ReadCoilAsync(63496, 1))[0]);
        Assert.True(Device.memory.ReadCoil(63496, 1)[0]);

        // 多线圈写（FC0F）：9 位跨 2 字节，专挑位打包边界
        bool[] expected = [true, false, true, false, true, false, true, false, true];
        await Client.WriteMultiCoilAsync(100, expected);
        Assert.Equal(expected, await Client.ReadCoilAsync(100, 9));
        Assert.Equal(expected, Device.memory.ReadCoil(100, 9));
    }

    [Fact]
    public async Task Slave_BeyondBoundaryAddressShouldReturnIllegalDataAddressException()
    {
        var (Slave, Device) = await StartSlave();
        await using var slaveRelease = Slave;
        await using var deviceRelease = Device;

        // 起始 65535 + 数量 10 越过 65536 点上限：按 Modbus 规范应回 0x83 / 异常码 02
        var response = await SenderOriginalFrame(Slave.Port, ReadRegisterFrame(0xFFFF, 10));

        Assert.Equal(0x83, response[7]);
        Assert.Equal(0x02, response[8]);
    }

    [Fact]
    public async Task Slave_UnsupportedFunctionCodeShouldReturnIllegalFunctionCanException()
    {
        var (Slave, Device) = await StartSlave();
        await using var slaveRelease = Slave;
        await using var deviceRelease = Device;

        // FC 0x11（报告从站 ID）本从站不实现 → 按规范回 0x91 / 异常码 01
        var response = await SenderOriginalFrame(Slave.Port,
            [0x00, 0x02, 0x00, 0x00, 0x00, 0x06, 0x01, 0x11, 0x00, 0x00, 0x00, 0x01]);

        Assert.Equal(0x91, response[7]);
        Assert.Equal(0x01, response[8]);
    }

    [Fact]
    public async Task Slave_StartAfterStateTextShouldReflectListenAndClientNumber()
    {
        var (Slave, Device) = await StartSlave();
        await using var slaveRelease = Slave;
        await using var deviceRelease = Device;

        Assert.True(Slave.IsRun);
        Assert.True(Slave.Port > 0, "端口配 0 时应由系统分配实际端口");
        Assert.Contains("监听中", Slave.StateText, StringComparison.Ordinal);

        await using var Client = await ConnectRealClient(Slave.Port);
        _ = await Client.ReadHoldingRegisterAsync(0, 1);

        Assert.Equal(1, Slave.ConnectedClientNumber);
    }

    [Fact]
    public async Task Slave_StopAfterShouldBeAbleToAgainTimesStart()
    {
        var (Slave, Device) = await StartSlave();
        await using var slaveRelease = Slave;
        await using var deviceRelease = Device;

        await Slave.StopAsync();
        Assert.False(Slave.IsRun);

        await Slave.LaunchAsync();
        Assert.True(Slave.IsRun);

        // 重启后仍然能正常读写
        await using var Client = await ConnectRealClient(Slave.Port);
        await Client.WriteSingleRegisterAsync(7, 0x00FF);
        Assert.Equal(0x00FF, Device.memory.ReadHoldingRegister(7, 1)[0]);
    }

    // ── 原始帧工具 ──────────────────────────────────────────────────────

    /// <summary>构造 FC03 读保持寄存器请求帧（MBAP + PDU，单元号固定 1）。</summary>
    private static byte[] ReadRegisterFrame(ushort StartAt, ushort Count) =>
    [
        0x00, 0x01, 0x00, 0x00, 0x00, 0x06, 0x01,
        0x03, (byte)(StartAt >> 8), (byte)StartAt, (byte)(Count >> 8), (byte)Count,
    ];

    /// <summary>发一帧并读回一整帧响应（MBAP + PDU）。</summary>
    private static async Task<byte[]> SenderOriginalFrame(int Port, byte[] frame)
    {
        using var Client = new TcpClient();
        await Client.ConnectAsync(IPAddress.Loopback, Port);

        var stream = Client.GetStream();
        await stream.WriteAsync(frame);

        var messageHead = new byte[7];
        await stream.ReadExactlyAsync(messageHead);

        var length = BinaryPrimitives.ReadUInt16BigEndian(messageHead.AsSpan(4, 2));
        var protocolData = new byte[length - 1];
        await stream.ReadExactlyAsync(protocolData);

        return [.. messageHead, .. protocolData];
    }
}
