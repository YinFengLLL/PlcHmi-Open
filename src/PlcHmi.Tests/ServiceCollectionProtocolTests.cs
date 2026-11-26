using Microsoft.Extensions.DependencyInjection;
using 上位机.业务;
using 上位机.业务.配置;
using 上位机.通信.仿真;
using 上位机.通信.Modbus;
using 上位机.通信.OPCUA;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.上位机测试;

/// <summary>
/// DI 装配的“协议 × 模拟器”四组合选择单测：
/// 保证 appsettings.json 的 通讯协议 字段能正确决定注入的客户端实现，
/// 且旧配置（无该字段，缺省 ModbusTCP）行为不变。
/// </summary>
public class 通讯协议装配测试
{
    private static ServiceProvider 构建提供者(Action<PLC连接选项> 调整)
    {
        var 选项 = new PLC连接选项();
        调整(选项);

        var 服务集合 = new ServiceCollection();
        服务集合.AddLogging();
        服务集合.添加上位机服务(选项, new 轮询选项(), new 配方配置());

        return 服务集合.BuildServiceProvider();
    }

    [Fact]
    public async Task 缺省配置_应装配Modbus真实客户端()
    {
        // 旧配置文件没有 通讯协议 字段，反序列化后为枚举默认值 ModbusTCP
        await using var 提供者 = 构建提供者(_ => { });

        var 客户端 = 提供者.GetRequiredService<Modbus客户端接口>();

        // Modbus 模式统一装配"可切换代理"（运行模式需在运行期切换）；
        // 缺省（使用模拟器=false）代理内部指向真实客户端
        var 代理 = Assert.IsType<可切换Modbus客户端>(客户端);
        Assert.IsType<FluentModbusTCP客户端>(代理.当前实现);

        // 连接管理器拿到的必须是同一份代理实例，否则切换的对象与实际通信的对象不是同一个
        Assert.Same(代理, 提供者.GetRequiredService<Modbus客户端接口>());
    }

    [Fact]
    public async Task Modbus协议加模拟器_应装配仿真Modbus客户端()
    {
        await using var 提供者 = 构建提供者(选项 =>
        {
            选项.通讯协议 = 通讯协议.ModbusTCP;
            选项.使用模拟器 = true;
        });

        var 代理 = Assert.IsType<可切换Modbus客户端>(提供者.GetRequiredService<Modbus客户端接口>());

        Assert.IsType<仿真Modbus客户端>(代理.当前实现);
    }

    [Fact]
    public async Task Modbus模式_应注册可运行期切换的运行模式管理器()
    {
        await using var 提供者 = 构建提供者(_ => { });

        var 管理器 = 提供者.GetRequiredService<运行模式管理器接口>();

        Assert.True(管理器.可在运行期切换);
        // 缺省配置（使用模拟器=false）→ 启动即真实 PLC
        Assert.Equal(运行模式.真实PLC, 管理器.当前模式);
    }

    [Fact]
    public async Task OPCUA模式_不应注册运行模式管理器()
    {
        // OPC UA 的客户端实现由 DI 定型，没有运行期切换能力：
        // 不注册管理器 → 界面注入可空依赖后自动隐藏入口，不会出现"能点但切不动"的假象
        await using var 提供者 = 构建提供者(选项 => 选项.通讯协议 = 通讯协议.OPCUA);

        Assert.Null(提供者.GetService<运行模式管理器接口>());
    }

    [Fact]
    public async Task OPCUA协议加模拟器_应装配仿真OPCUA客户端()
    {
        await using var 提供者 = 构建提供者(选项 =>
        {
            选项.通讯协议 = 通讯协议.OPCUA;
            选项.使用模拟器 = true;
        });

        var 客户端 = 提供者.GetRequiredService<Modbus客户端接口>();

        Assert.IsType<仿真OPCUA客户端>(客户端);
    }

    [Fact]
    public async Task OPCUA协议真实模式_应装配OPCUA客户端()
    {
        await using var 提供者 = 构建提供者(选项 =>
        {
            选项.通讯协议 = 通讯协议.OPCUA;
            选项.使用模拟器 = false;
        });

        var 客户端 = 提供者.GetRequiredService<Modbus客户端接口>();

        Assert.IsType<OPCUA客户端>(客户端);
    }

    [Fact]
    public async Task OPCUA模式_NodeId推导器应注册()
    {
        await using var 提供者 = 构建提供者(选项 => 选项.通讯协议 = 通讯协议.OPCUA);

        var 推导器 = 提供者.GetRequiredService<NodeId推导器>();

        Assert.NotNull(推导器);
    }

    [Fact]
    public async Task Modbus模式_NodeId推导器不应注册()
    {
        await using var 提供者 = 构建提供者(_ => { });

        Assert.Null(提供者.GetService<NodeId推导器>());
    }
}
