using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using 上位机.通信.Modbus;
using 上位机.通信.仿真;
using 上位机.核心.模型;

namespace 上位机.上位机测试;

/// <summary>
/// 模拟 PLC 的 Modbus TCP 从站测试。
///
/// <para><b>守的是什么：</b>「模拟模式下可用 Modbus 工具离线调试」这句承诺，全靠从站能把标准
/// Modbus TCP 帧正确翻译成虚拟内存的读写。若帧格式、位打包、字节序或异常响应有一处不对，
/// 现场表现是"工具连上了但没有数据 / 数据是乱的"—— 这种问题在真机上极难排查。</para>
///
/// <para><b>怎么验：</b>用<b>上位机自己的真实客户端</b> <see cref="FluentModbusTCP客户端"/>
/// 去连从站，跑完整往返；再补两条手工原始帧用例覆盖异常响应路径。
/// 这样从站与真实客户端互为对照组，任一方协议实现走样都会立刻红。</para>
/// </summary>
public class 虚拟PLC从站测试
{
    /// <summary>启动一台从站（端口 0 = 由系统分配，测试不会撞端口）。</summary>
    private static async Task<(虚拟PLC从站 从站, 虚拟PLC设备 设备)> 启动从站()
    {
        var 设备 = new 虚拟PLC设备(NullLogger<虚拟PLC设备>.Instance);
        var 选项 = new PLC连接选项 { 模拟从站端口 = 0, 模拟从站监听地址 = "127.0.0.1" };
        var 从站 = new 虚拟PLC从站(设备, 选项, NullLogger<虚拟PLC从站>.Instance);

        await 从站.启动Async();

        return (从站, 设备);
    }

    /// <summary>用上位机的真实 Modbus 客户端连上从站。</summary>
    private static async Task<FluentModbusTCP客户端> 连接真实客户端(int 端口)
    {
        var 选项 = new PLC连接选项
        {
            主机 = "127.0.0.1",
            端口 = 端口,
            单元标识 = 1,
            连接超时毫秒 = 3000,
            读取超时毫秒 = 3000,
        };

        var 客户端 = new FluentModbusTCP客户端(选项, NullLogger<FluentModbusTCP客户端>.Instance);
        await 客户端.异步连接();

        return 客户端;
    }

    [Fact]
    public async Task 从站_寄存器读写往返应落到同一份虚拟内存()
    {
        var (从站, 设备) = await 启动从站();
        await using var 从站释放 = 从站;
        await using var 设备释放 = 设备;

        await using var 客户端 = await 连接真实客户端(从站.端口);

        // 多寄存器写（FC10）→ 读回（FC03）
        await 客户端.异步写入多个寄存器(100, new ushort[] { 0x1234, 0xABCD });
        Assert.Equal(new ushort[] { 0x1234, 0xABCD }, await 客户端.异步读取保持寄存器(100, 2));

        // 外部工具写进去的值，必须与上位机自用的虚拟内存是同一份（不是两份副本）
        Assert.Equal(new ushort[] { 0x1234, 0xABCD }, 设备.内存.读取保持寄存器(100, 2));

        // 单寄存器写（FC06）
        await 客户端.异步写入单个寄存器(101, 0x0001);
        Assert.Equal(0x0001, 设备.内存.读取保持寄存器(101, 1)[0]);
    }

    [Fact]
    public async Task 从站_位读写往返应正确处理位打包()
    {
        var (从站, 设备) = await 启动从站();
        await using var 从站释放 = 从站;
        await using var 设备释放 = 设备;

        await using var 客户端 = await 连接真实客户端(从站.端口);

        // 单线圈写（FC05）
        await 客户端.异步写入单个线圈(63496, true);   // X10（八进制 8 → 63488 + 8）
        Assert.True((await 客户端.异步读取线圈(63496, 1))[0]);
        Assert.True(设备.内存.读取线圈(63496, 1)[0]);

        // 多线圈写（FC0F）：9 位跨 2 字节，专挑位打包边界
        bool[] 期望 = [true, false, true, false, true, false, true, false, true];
        await 客户端.异步写入多个线圈(100, 期望);
        Assert.Equal(期望, await 客户端.异步读取线圈(100, 9));
        Assert.Equal(期望, 设备.内存.读取线圈(100, 9));
    }

    [Fact]
    public async Task 从站_越界地址应回非法数据地址异常()
    {
        var (从站, 设备) = await 启动从站();
        await using var 从站释放 = 从站;
        await using var 设备释放 = 设备;

        // 起始 65535 + 数量 10 越过 65536 点上限：按 Modbus 规范应回 0x83 / 异常码 02
        var 响应 = await 发送原始帧(从站.端口, 读寄存器帧(0xFFFF, 10));

        Assert.Equal(0x83, 响应[7]);
        Assert.Equal(0x02, 响应[8]);
    }

    [Fact]
    public async Task 从站_不支持的功能码应回非法功能异常()
    {
        var (从站, 设备) = await 启动从站();
        await using var 从站释放 = 从站;
        await using var 设备释放 = 设备;

        // FC 0x11（报告从站 ID）本从站不实现 → 按规范回 0x91 / 异常码 01
        var 响应 = await 发送原始帧(从站.端口,
            [0x00, 0x02, 0x00, 0x00, 0x00, 0x06, 0x01, 0x11, 0x00, 0x00, 0x00, 0x01]);

        Assert.Equal(0x91, 响应[7]);
        Assert.Equal(0x01, 响应[8]);
    }

    [Fact]
    public async Task 从站_启动后状态文本应反映监听与客户端数()
    {
        var (从站, 设备) = await 启动从站();
        await using var 从站释放 = 从站;
        await using var 设备释放 = 设备;

        Assert.True(从站.是否运行);
        Assert.True(从站.端口 > 0, "端口配 0 时应由系统分配实际端口");
        Assert.Contains("监听中", 从站.状态文本, StringComparison.Ordinal);

        await using var 客户端 = await 连接真实客户端(从站.端口);
        _ = await 客户端.异步读取保持寄存器(0, 1);

        Assert.Equal(1, 从站.已连接客户端数);
    }

    [Fact]
    public async Task 从站_停止后应可再次启动()
    {
        var (从站, 设备) = await 启动从站();
        await using var 从站释放 = 从站;
        await using var 设备释放 = 设备;

        await 从站.异步停止();
        Assert.False(从站.是否运行);

        await 从站.启动Async();
        Assert.True(从站.是否运行);

        // 重启后仍然能正常读写
        await using var 客户端 = await 连接真实客户端(从站.端口);
        await 客户端.异步写入单个寄存器(7, 0x00FF);
        Assert.Equal(0x00FF, 设备.内存.读取保持寄存器(7, 1)[0]);
    }

    // ── 原始帧工具 ──────────────────────────────────────────────────────

    /// <summary>构造 FC03 读保持寄存器请求帧（MBAP + PDU，单元号固定 1）。</summary>
    private static byte[] 读寄存器帧(ushort 起始, ushort 数量) =>
    [
        0x00, 0x01, 0x00, 0x00, 0x00, 0x06, 0x01,
        0x03, (byte)(起始 >> 8), (byte)起始, (byte)(数量 >> 8), (byte)数量,
    ];

    /// <summary>发一帧并读回一整帧响应（MBAP + PDU）。</summary>
    private static async Task<byte[]> 发送原始帧(int 端口, byte[] 帧)
    {
        using var 客户端 = new TcpClient();
        await 客户端.ConnectAsync(IPAddress.Loopback, 端口);

        var 流 = 客户端.GetStream();
        await 流.WriteAsync(帧);

        var 报文头 = new byte[7];
        await 流.ReadExactlyAsync(报文头);

        var 长度 = BinaryPrimitives.ReadUInt16BigEndian(报文头.AsSpan(4, 2));
        var 协议数据 = new byte[长度 - 1];
        await 流.ReadExactlyAsync(协议数据);

        return [.. 报文头, .. 协议数据];
    }
}
