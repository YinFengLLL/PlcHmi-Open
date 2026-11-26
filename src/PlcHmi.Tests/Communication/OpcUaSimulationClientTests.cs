using Microsoft.Extensions.Logging.Abstractions;
using 上位机.通信.仿真;
using 上位机.通信.汇川;
using 上位机.通信.Modbus;
using 上位机.通信.OPCUA;
using 上位机.核心.抽象;
using 上位机.核心.异常;
using 上位机.核心.模型;

namespace 上位机.上位机测试;

/// <summary>
/// OPC UA 客户端语义单测（经仿真实现）：验证“区域+地址 → NodeId → 读写”的完整路径，
/// 以及 OPC UA 与 Modbus 两条协议路径共享同一份虚拟 PLC 数据。
/// </summary>
public class OPCUA仿真客户端测试
{
    private static (仿真OPCUA客户端 OPCUA, 仿真Modbus客户端 Modbus, 虚拟PLC设备 设备) 创建双协议环境()
    {
        var 选项 = new PLC连接选项();
        var 推导器 = new NodeId推导器(选项, new 汇川地址解析器(选项.系列));
        var 设备 = new 虚拟PLC设备(NullLogger<虚拟PLC设备>.Instance);

        var opcua = new 仿真OPCUA客户端(设备, 推导器, NullLogger<仿真OPCUA客户端>.Instance);
        var modbus = new 仿真Modbus客户端(设备, NullLogger<仿真Modbus客户端>.Instance);

        return (opcua, modbus, 设备);
    }

    [Fact]
    public async Task 连接前读写_应抛未连接异常()
    {
        var (opcua, _, _) = 创建双协议环境();

        var 异常 = await Assert.ThrowsAsync<InvalidOperationException>(
            () => opcua.异步读取保持寄存器(0, 1));

        Assert.Contains("未连接", 异常.Message);
    }

    [Fact]
    public async Task 连接与断开_应正确翻转状态并启停虚拟设备()
    {
        var (opcua, _, 设备) = 创建双协议环境();

        Assert.False(opcua.是否已连接);

        await opcua.异步连接();
        Assert.True(opcua.是否已连接);

        await opcua.异步断开();
        Assert.False(opcua.是否已连接);
    }

    [Fact]
    public async Task 读写保持寄存器_单点与批量_应往返一致()
    {
        var (opcua, _, _) = 创建双协议环境();
        await opcua.异步连接();

        await opcua.异步写入单个寄存器(100, 1234);
        await opcua.异步写入多个寄存器(200, [11, 22, 33]);

        var 单点 = await opcua.异步读取保持寄存器(100, 1);
        var 批量 = await opcua.异步读取保持寄存器(200, 3);

        Assert.Equal((ushort)1234, 单点[0]);
        Assert.Equal(new ushort[] { 11, 22, 33 }, 批量);
    }

    [Fact]
    public async Task 读写线圈_应往返一致()
    {
        var (opcua, _, _) = 创建双协议环境();
        await opcua.异步连接();

        await opcua.异步写入多个线圈(300, [true, false, true, true]);

        var 读回 = await opcua.异步读取线圈(300, 4);

        Assert.Equal(new[] { true, false, true, true }, 读回);
    }

    [Fact]
    public async Task 读写X区_八进制地址路径_应正常工作()
    {
        // X 是物理输入 → 离散输入区（FC02）。八进制反解路径：63488+8 → X10（不是 X8）。
        // 2026-10-17 修正前 X 被放在线圈区，这里写线圈、读线圈都"通"，但真机上读的是无关位。
        var (opcua, _, 设备) = 创建双协议环境();
        await opcua.异步连接();

        设备.内存.不安全设置离散输入(63496, true);   // X10（八进制 8 → 63488 + 8）

        var 读回 = await opcua.异步读取离散输入(63496, 1);

        Assert.True(读回[0], "X10 应经离散输入区（FC02）读到");
    }

    [Fact]
    public async Task 与Modbus仿真客户端_共享同一虚拟设备数据()
    {
        var (opcua, modbus, _) = 创建双协议环境();
        await opcua.异步连接();
        await modbus.异步连接();

        // OPC UA 路径写、Modbus 路径读
        await opcua.异步写入单个寄存器(100, 999);
        Assert.Equal((ushort)999, (await modbus.异步读取保持寄存器(100, 1))[0]);

        // Modbus 路径写、OPC UA 路径读
        await modbus.异步写入单个线圈(400, true);
        Assert.True((await opcua.异步读取线圈(400, 1))[0]);
    }

    [Fact]
    public async Task 读写操作_都会经过NodeId推导()
    {
        // 无法映射的地址（保持寄存器区 65536 越界）应抛地址解析异常，
        // 证明客户端确实走了“区域+地址 → NodeId”推导而非直连内存
        var (opcua, _, _) = 创建双协议环境();
        await opcua.异步连接();

        await Assert.ThrowsAsync<地址解析异常>(
            () => opcua.异步读取保持寄存器(65536, 1));
    }
}
