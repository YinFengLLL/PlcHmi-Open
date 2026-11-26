using Microsoft.Extensions.Logging.Abstractions;
using PlcHmi.Business.Services;
using PlcHmi.Communication.Modbus;
using PlcHmi.Communication.Simulation;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;
using PlcHmi.Tests.Fake;

namespace PlcHmi.Tests;

/// <summary>
/// 运行模式管理器测试：真实 PLC ↔ 模拟 PLC 的运行期切换。
///
/// <para><b>守的是什么：</b>切换是"停连接 → 换客户端实现 → 起/停模拟从站 → 重连"的四步动作，
/// 任何一步顺序错、漏做或半途失败，现场表现都是"界面显示已切换，但数据还是旧的"或
/// "模拟从站没起来，工具连不上"。</para>
///
/// <para>用替身连接管理器记录启停次数，用真实的两套客户端实现与真实从站，
/// 使断言落在"到底换了谁、从站起没起"这些可观察事实上。</para>
/// </summary>
public class RunModeSwitchTest
{
    /// <summary>每个用例一套完整夹具：真实/仿真客户端各一份 + 代理 + 从站 + 替身连接管理器。</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        public required PlcConnectionOptions Options { get; init; }

        public required FakeConnectManager Connect { get; init; }

        public required VirtualPlcDevice Device { get; init; }

        public required VirtualPlcSlave Slave { get; init; }

        public required SimulationModbusClient Simulation { get; init; }

        public required FluentModbusTCPClient Real { get; init; }

        public required SwitchableModbusClient Proxy { get; init; }

        public required ModbusRunModeManager Manager { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Manager.DisposeAsync();
            await Real.DisposeAsync();
            await Simulation.DisposeAsync();
            await Device.DisposeAsync();
        }
    }

    /// <summary>未登录身份走生产用的真实守卫 —— 操作员不具备「切换运行模式」权限。</summary>
    private static IPermissionGuard NotLoginPermission() =>
        new UserPermissionGuard(new UserService(NullLogger<UserService>.Instance),
            NullLogger<UserPermissionGuard>.Instance);

    private static Fixture BuildFixture(
        bool initialSimulation = false,
        string ListenAddress = "127.0.0.1",
        CommunicationProtocol Protocol = CommunicationProtocol.ModbusTCP,
        IPermissionGuard? Permission = null)
    {
        var Options = new PlcConnectionOptions
        {
            Host = "127.0.0.1",
            Port = 502,
            UnitId = 1,
            UseSimulator = initialSimulation,
            CommunicationProtocol = Protocol,
            SimulationSlavePort = 0,
            SimulationSlaveListenAddress = ListenAddress,
            ConnectTimeoutMilliseconds = 1000,
            ReadTimeoutMilliseconds = 1000,
        };

        var Device = new VirtualPlcDevice(NullLogger<VirtualPlcDevice>.Instance);
        var Slave = new VirtualPlcSlave(Device, Options, NullLogger<VirtualPlcSlave>.Instance);
        var Simulation = new SimulationModbusClient(Device, NullLogger<SimulationModbusClient>.Instance);
        var Real = new FluentModbusTCPClient(Options, NullLogger<FluentModbusTCPClient>.Instance);
        var Proxy = new SwitchableModbusClient(initialSimulation ? Simulation : Real);
        var Connect = new FakeConnectManager();
        var Manager = new ModbusRunModeManager(
            Proxy, Real, Simulation, Slave, Connect, Options,
            Permission ?? new AllowAllPermissionGuard(),
            NullLogger<ModbusRunModeManager>.Instance);

        return new Fixture
        {
            Options = Options,
            Connect = Connect,
            Device = Device,
            Slave = Slave,
            Simulation = Simulation,
            Real = Real,
            Proxy = Proxy,
            Manager = Manager,
        };
    }

    [Fact]
    public async Task SwitchToSimulationMode_ShouldSwapUseSimulationClientAndStartDebugSlave()
    {
        await using var fixtureRealCase = BuildFixture();
        var EventTimes = 0;
        fixtureRealCase.Manager.modeChanged += (_, _) => EventTimes++;

        await fixtureRealCase.Manager.SwitchAsync(RunMode.SimulationPlc);

        Assert.Equal(RunMode.SimulationPlc, fixtureRealCase.Manager.CurrentMode);
        Assert.IsType<SimulationModbusClient>(fixtureRealCase.Proxy.CurrentRealCurrent);
        Assert.True(fixtureRealCase.Options.UseSimulator);

        // 离线调试从站必须真的起来（否则"可用工具调试"是一句空话）
        Assert.True(fixtureRealCase.Slave.IsRun);
        Assert.True(fixtureRealCase.Manager.SimulationSlaveIsRun);
        Assert.True(fixtureRealCase.Slave.Port > 0, "端口配 0 应由系统分配实际端口");
        Assert.Contains("监听中", fixtureRealCase.Manager.SimulationSlaveStateText, StringComparison.Ordinal);

        // 连接被"停一次、起一次"——换实现必须走断连重连，否则旧连接会继续用旧实现
        Assert.Equal(1, fixtureRealCase.Connect.StopCount);
        Assert.Equal(1, fixtureRealCase.Connect.StartCount);
        Assert.Equal(1, EventTimes);
    }

    [Fact]
    public async Task FromSimulationSwitchBackReal_ShouldSwitchBackRealClientAndStopUseDebugSlave()
    {
        await using var fixtureRealCase = BuildFixture();
        await fixtureRealCase.Manager.SwitchAsync(RunMode.SimulationPlc);
        Assert.True(fixtureRealCase.Slave.IsRun);

        var EventTimes = 0;
        fixtureRealCase.Manager.modeChanged += (_, _) => EventTimes++;

        await fixtureRealCase.Manager.SwitchAsync(RunMode.RealPlc);

        Assert.Equal(RunMode.RealPlc, fixtureRealCase.Manager.CurrentMode);
        Assert.IsType<FluentModbusTCPClient>(fixtureRealCase.Proxy.CurrentRealCurrent);
        Assert.False(fixtureRealCase.Options.UseSimulator);

        // 回到真实 PLC 后调试从站必须关闭：留着一个可写的虚拟 PLC 监听口是安全隐患
        Assert.False(fixtureRealCase.Slave.IsRun);
        Assert.Equal(2, fixtureRealCase.Connect.StopCount);
        Assert.Equal(2, fixtureRealCase.Connect.StartCount);
        Assert.Equal(1, EventTimes);
    }

    [Fact]
    public async Task TargetAndCurrentConsistentWhenShouldIdempotent_NotRestartConnect()
    {
        await using var fixtureRealCase = BuildFixture();
        var EventTimes = 0;
        fixtureRealCase.Manager.modeChanged += (_, _) => EventTimes++;

        await fixtureRealCase.Manager.SwitchAsync(RunMode.RealPlc);

        Assert.Equal(0, fixtureRealCase.Connect.StopCount);
        Assert.Equal(0, fixtureRealCase.Connect.StartCount);
        Assert.Equal(0, EventTimes);
    }

    [Fact]
    public async Task NoneSwitchPermissionWhen_ShouldRejectAndNotLeaveAnyStateChange()
    {
        await using var fixtureRealCase = BuildFixture(Permission: NotLoginPermission());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => fixtureRealCase.Manager.SwitchAsync(RunMode.SimulationPlc));

        Assert.Equal(RunMode.RealPlc, fixtureRealCase.Manager.CurrentMode);
        Assert.IsType<FluentModbusTCPClient>(fixtureRealCase.Proxy.CurrentRealCurrent);
        Assert.False(fixtureRealCase.Slave.IsRun);
        Assert.Equal(0, fixtureRealCase.Connect.StopCount);
        Assert.Equal(0, fixtureRealCase.Connect.StartCount);
    }

    [Fact]
    public async Task SlaveUnableToListenWhen_ShouldStopSwitchAndStateNotChange()
    {
        // 监听地址不是合法 IP → 从站在"停连接"之前就失败，属预期中的"什么都没发生"
        await using var fixtureRealCase = BuildFixture(ListenAddress: "不是IP");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixtureRealCase.Manager.SwitchAsync(RunMode.SimulationPlc));

        Assert.Equal(RunMode.RealPlc, fixtureRealCase.Manager.CurrentMode);
        Assert.IsType<FluentModbusTCPClient>(fixtureRealCase.Proxy.CurrentRealCurrent);
        Assert.False(fixtureRealCase.Slave.IsRun);
        Assert.Equal(0, fixtureRealCase.Connect.StopCount);
        Assert.Equal(0, fixtureRealCase.Connect.StartCount);
    }

    [Fact]
    public async Task OpcUaMode_UnsupportedRuntimeSwitch()
    {
        await using var fixtureRealCase = BuildFixture(Protocol: CommunicationProtocol.OPCUA);

        Assert.False(fixtureRealCase.Manager.CanAtRuntimeSwitch);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixtureRealCase.Manager.SwitchAsync(RunMode.SimulationPlc));

        Assert.Equal(0, fixtureRealCase.Connect.StopCount);
    }
}
