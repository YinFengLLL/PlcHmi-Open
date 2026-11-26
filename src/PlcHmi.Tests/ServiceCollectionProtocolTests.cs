using Microsoft.Extensions.DependencyInjection;
using PlcHmi.Business;
using PlcHmi.Business.Configuration;
using PlcHmi.Communication.Simulation;
using PlcHmi.Communication.Modbus;
using PlcHmi.Communication.OpcUa;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Tests;

/// <summary>
/// DI 装配的“协议 × 模拟器”四组合选择单测：
/// 保证 appsettings.json 的 通讯协议 字段能正确决定注入的客户端实现，
/// 且旧配置（无该字段，缺省 ModbusTCP）行为不变。
/// </summary>
public class CommunicationProtocolConfigureTest
{
    private static ServiceProvider BuildProvider(Action<PlcConnectionOptions> adjustWhole)
    {
        var Options = new PlcConnectionOptions();
        adjustWhole(Options);

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging();
        serviceCollection.AddPlcHmiService(Options, new PollingOptions(), new RecipeConfig());

        return serviceCollection.BuildServiceProvider();
    }

    [Fact]
    public async Task DefaultConfig_ShouldConfigureModbusRealClient()
    {
        // 旧配置文件没有 通讯协议 字段，反序列化后为枚举默认值 ModbusTCP
        await using var provider = BuildProvider(_ => { });

        var Client = provider.GetRequiredService<IModbusClient>();

        // Modbus 模式统一装配"可切换代理"（运行模式需在运行期切换）；
        // 缺省（使用模拟器=false）代理内部指向真实客户端
        var Proxy = Assert.IsType<SwitchableModbusClient>(Client);
        Assert.IsType<FluentModbusTCPClient>(Proxy.CurrentRealCurrent);

        // 连接管理器拿到的必须是同一份代理实例，否则切换的对象与实际通信的对象不是同一个
        Assert.Same(Proxy, provider.GetRequiredService<IModbusClient>());
    }

    [Fact]
    public async Task ModbusProtocolAddSimulator_ShouldConfigureSimulationModbusClient()
    {
        await using var provider = BuildProvider(Options =>
        {
            Options.CommunicationProtocol = CommunicationProtocol.ModbusTCP;
            Options.UseSimulator = true;
        });

        var Proxy = Assert.IsType<SwitchableModbusClient>(provider.GetRequiredService<IModbusClient>());

        Assert.IsType<SimulationModbusClient>(Proxy.CurrentRealCurrent);
    }

    [Fact]
    public async Task ModbusMode_ShouldRegisterRuntimeSwitchableRunModeManager()
    {
        await using var provider = BuildProvider(_ => { });

        var Manager = provider.GetRequiredService<IRunModeManager>();

        Assert.True(Manager.CanAtRuntimeSwitch);
        // 缺省配置（使用模拟器=false）→ 启动即真实 PLC
        Assert.Equal(RunMode.RealPlc, Manager.CurrentMode);
    }

    [Fact]
    public async Task OpcUaMode_ShouldNotRegisterRunModeManager()
    {
        // OPC UA 的客户端实现由 DI 定型，没有运行期切换能力：
        // 不注册管理器 → 界面注入可空依赖后自动隐藏入口，不会出现"能点但切不动"的假象
        await using var provider = BuildProvider(Options => Options.CommunicationProtocol = CommunicationProtocol.OPCUA);

        Assert.Null(provider.GetService<IRunModeManager>());
    }

    [Fact]
    public async Task OpcUaProtocolAddSimulator_ShouldConfigureSimulationOpcUaClient()
    {
        await using var provider = BuildProvider(Options =>
        {
            Options.CommunicationProtocol = CommunicationProtocol.OPCUA;
            Options.UseSimulator = true;
        });

        var Client = provider.GetRequiredService<IModbusClient>();

        Assert.IsType<SimulationOpcUaClient>(Client);
    }

    [Fact]
    public async Task OpcUaProtocolRealMode_ShouldConfigureOpcUaClient()
    {
        await using var provider = BuildProvider(Options =>
        {
            Options.CommunicationProtocol = CommunicationProtocol.OPCUA;
            Options.UseSimulator = false;
        });

        var Client = provider.GetRequiredService<IModbusClient>();

        Assert.IsType<OpcUaClient>(Client);
    }

    [Fact]
    public async Task OpcUaMode_NodeIdResolverShouldRegister()
    {
        await using var provider = BuildProvider(Options => Options.CommunicationProtocol = CommunicationProtocol.OPCUA);

        var Resolver = provider.GetRequiredService<NodeIdResolver>();

        Assert.NotNull(Resolver);
    }

    [Fact]
    public async Task ModbusMode_NodeIdResolverShouldNotRegister()
    {
        await using var provider = BuildProvider(_ => { });

        Assert.Null(provider.GetService<NodeIdResolver>());
    }
}
