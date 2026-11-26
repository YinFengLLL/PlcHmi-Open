using Microsoft.Extensions.Logging.Abstractions;
using PlcHmi.Business.Configuration;
using PlcHmi.Business.Services;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;
using PlcHmi.Tests.Fake;

namespace PlcHmi.Tests;

/// <summary>
/// PLC 连接参数管理的服务层测试。
///
/// <para><b>守的是什么</b>：这是系统设置页唯一的"改完立刻改变通信目标"的入口——
/// IP 填错程序就连不上 PLC，端口 / 从站号填错则读写打不到正确的从站；
/// 而保存动作会重启连接，如果校验被绕过，一次误操作就能把产线通信打断。
/// 所以范围校验必须落在服务层（界面只是隐藏入口），且"保存 = 落盘 + 更新运行参数 + 重启连接"
/// 这条链路必须有测试锁定。</para>
///
/// <para>保存会写测试输出目录里的 <c>config/appsettings.json</c>，归入 config-files
/// 集合禁用并行，并在用例内备份 / 还原原文件。</para>
/// </summary>
[Collection("config-files")]
public class PlcConnectionSettingsServiceTest
{
    private static PlcConnectionOptions NewOptions() => new()
    {
        Host = "192.168.1.10",
        Port = 502,
        UnitId = 1,
        Series = PlcSeries.SmallPlc,
        UseSimulator = true,
        ConnectTimeoutMilliseconds = 3000,
        ReadTimeoutMilliseconds = 1000,
        HeartbeatIntervalMilliseconds = 5000,
        ReconnectInitialDelayMilliseconds = 1000,
        ReconnectMaxDelayMilliseconds = 30000,
        PerRequestMaxRegisterNumber = 100,
        AutoReconnect = true,
    };

    private static (PlcConnectionSettingsService Service, PlcConnectionOptions Options, FakeConnectManager Connection) NewService()
    {
        var Options = NewOptions();
        var Connect = new FakeConnectManager();
        var serviceInstance = new PlcConnectionSettingsService(
            Options, Connect, new AllowAllPermissionGuard(),
            NullLogger<PlcConnectionSettingsService>.Instance);
        return (serviceInstance, Options, Connect);
    }

    [Fact]
    public async Task ValidParameterSaveAfterShouldUpdateRunParameterAndPersistAndRestartConnect()
    {
        var (serviceInstance, Options, Connect) = NewService();
        var Original = await File.ReadAllTextAsync(ConfigPath.AppSettings);

        try
        {
            var edit = serviceInstance.CreateEditModel();
            edit.Host = "192.168.2.77";
            edit.Port = 1502;
            edit.UnitId = 3;

            await serviceInstance.SaveAsync(edit);

            // 1) 共享运行参数被更新 —— 通信客户端下次连接就用它
            Assert.Equal("192.168.2.77", Options.Host);
            Assert.Equal(1502, Options.Port);
            Assert.Equal(3, Options.UnitId);

            // 2) 连接被重启（Stop 断开旧连接 → Start 用新参数重连）
            Assert.Equal(1, Connect.StopCount);
            Assert.Equal(1, Connect.StartCount);

            // 3) 落盘：重新读文件确认新值，且不可编辑字段以运行态保留
            var reloaded = AppSettingsLoader.Load(out var error);
            Assert.Null(error);
            Assert.Equal("192.168.2.77", reloaded.PlcConnection.Host);
            Assert.Equal(1502, reloaded.PlcConnection.Port);
            Assert.Equal(3, reloaded.PlcConnection.UnitId);
            // 运行模式（模拟开关）是运行期状态，Modbus 模式下恒以 false 落盘：
            // 每次启动一律真实 PLC，避免生产机台重启后"记住"上次调试的模拟选择
            Assert.False(reloaded.PlcConnection.UseSimulator,
                "Modbus 运行模式不跨重启记忆：保存连接参数时模拟开关必须写回 false");
            Assert.Equal(PlcSeries.SmallPlc, reloaded.PlcConnection.Series);
        }
        finally
        {
            // 还原配置文件：本用例改的是测试输出目录里的副本，不还原会污染后续读配置的用例
            await File.WriteAllTextAsync(ConfigPath.AppSettings, Original);
        }
    }

    [Theory]
    [InlineData("", 502, 1)]            // 空地址
    [InlineData("abc!", 502, 1)]        // 非法主机名
    [InlineData("192.168.1.10", 0, 1)]  // 端口下界
    [InlineData("192.168.1.10", 65536, 1)]  // 端口上界
    [InlineData("192.168.1.10", 502, 0)]    // 从站号下界
    [InlineData("192.168.1.10", 502, 248)]  // 从站号上界
    public void IllegalAddressPortSlaveNumberShouldBeRejected(string Host, int portNumber, int unitIdValue)
    {
        var (serviceInstance, _, _) = NewService();
        var edit = serviceInstance.CreateEditModel();
        edit.Host = Host;
        edit.Port = portNumber;
        edit.UnitId = unitIdValue;

        Assert.NotEmpty(serviceInstance.Validate(edit));
    }

    [Theory]
    [InlineData(99, 1000, 1000, 100, 1000, 100)]   // 连接超时 < 100
    [InlineData(3000, 99, 1000, 100, 1000, 100)]   // 读写超时 < 100
    [InlineData(3000, 1000, 499, 100, 1000, 100)]  // 心跳 < 500（过密打挂 PLC）
    [InlineData(3000, 1000, 1000, 99, 1000, 100)]  // 重连初始 < 100
    [InlineData(3000, 1000, 1000, 100, 50, 100)]   // 退避上限 < 初始
    [InlineData(3000, 1000, 1000, 100, 1000, 0)]   // 寄存器数下界
    [InlineData(3000, 1000, 1000, 100, 1000, 124)] // 寄存器数超汇川读上限
    public void ExceedOutRangeCommunicationParameterShouldBeRejected(
        int connectTimeout, int readTimeout, int heartbeat,
        int reconnectInitial, int reconnectMax, int maxRegister)
    {
        var (serviceInstance, _, _) = NewService();
        var edit = serviceInstance.CreateEditModel();
        edit.ConnectTimeoutMilliseconds = connectTimeout;
        edit.ReadTimeoutMilliseconds = readTimeout;
        edit.HeartbeatIntervalMilliseconds = heartbeat;
        edit.ReconnectInitialDelayMilliseconds = reconnectInitial;
        edit.ReconnectMaxDelayMilliseconds = reconnectMax;
        edit.PerRequestMaxRegisterNumber = maxRegister;

        Assert.NotEmpty(serviceInstance.Validate(edit));
    }

    [Fact]
    public void DefaultParameterShouldPassValidate()
    {
        var (serviceInstance, _, _) = NewService();
        var edit = serviceInstance.CreateEditModel();

        Assert.Empty(serviceInstance.Validate(edit));
    }

    [Theory]
    [InlineData(0, 2)]        // OPC UA 端口下界
    [InlineData(65536, 2)]    // OPC UA 端口上界
    [InlineData(4840, 0)]     // 命名空间索引下界（ns=0 是标准命名空间保留段）
    public void IllegalOpcUaParameterShouldBeRejected(int opcuaPort, int ns)
    {
        var (serviceInstance, _, _) = NewService();
        var edit = serviceInstance.CreateEditModel();
        edit.OpcUaPort = opcuaPort;
        edit.OpcUaNamespaceIndex = ns;

        Assert.NotEmpty(serviceInstance.Validate(edit));
    }

    [Fact]
    public async Task SwitchCommunicationProtocolShouldPersistAndAtNextStartGenerateEffect()
    {
        var (serviceInstance, Options, Connect) = NewService();
        var Original = await File.ReadAllTextAsync(ConfigPath.AppSettings);

        try
        {
            var edit = serviceInstance.CreateEditModel();
            Assert.Equal(CommunicationProtocol.ModbusTCP, edit.CommunicationProtocol);

            edit.CommunicationProtocol = CommunicationProtocol.OPCUA;
            edit.OpcUaPort = 4840;
            edit.OpcUaNamespaceIndex = 3;

            await serviceInstance.SaveAsync(edit);

            // 1) 落盘：重启后 DI 按新协议装配
            var reloaded = AppSettingsLoader.Load(out var error);
            Assert.Null(error);
            Assert.Equal(CommunicationProtocol.OPCUA, reloaded.PlcConnection.CommunicationProtocol);
            Assert.Equal(4840, reloaded.PlcConnection.OpcUaPort);
            Assert.Equal(3, reloaded.PlcConnection.OpcUaNamespaceIndex);

            // 2) 共享运行参数同步（下次连接按新端口；协议本身仍需重启才切换客户端实现）
            Assert.Equal(CommunicationProtocol.OPCUA, Options.CommunicationProtocol);
            Assert.Equal(3, Options.OpcUaNamespaceIndex);

            // 3) 连接被重启
            Assert.Equal(1, Connect.StopCount);
            Assert.Equal(1, Connect.StartCount);
        }
        finally
        {
            await File.WriteAllTextAsync(ConfigPath.AppSettings, Original);
        }
    }

    [Fact]
    public async Task ValidateInvalidWhenSaveShouldThrowAndNotChangeMoveAnyState()
    {
        var (serviceInstance, Options, Connect) = NewService();
        var Original = await File.ReadAllTextAsync(ConfigPath.AppSettings);

        // 初始值从配置读出（2026-10-12 真机 IP 改为 192.168.1.10，不能在这里硬编码）
        var initialHost = Options.Host;
        var initialPort = Options.Port;

        try
        {
            var edit = serviceInstance.CreateEditModel();
            edit.Host = "10.0.0.99";
            edit.Port = 0;   // 非法

            await Assert.ThrowsAsync<InvalidOperationException>(() => serviceInstance.SaveAsync(edit));

            // 内存、文件、连接都不应被触碰
            Assert.Equal(initialHost, Options.Host);
            Assert.Equal(initialPort, Options.Port);
            Assert.Equal(0, Connect.StopCount);
            Assert.Equal(0, Connect.StartCount);

            // 文件未被触碰：与用例开始时的原始内容逐字一致（不比对具体 IP，避免与现场配置耦合）
            Assert.Equal(Original,
                await File.ReadAllTextAsync(ConfigPath.AppSettings));
        }
        finally
        {
            await File.WriteAllTextAsync(ConfigPath.AppSettings, Original);
        }
    }

    [Fact]
    public void ConnectParameterEditPermissionShouldOnlyAdminOwnHas()
    {
        Assert.True(RolePermission.Has(UserRole.Admin, Permission.EditPlcConnect));
        Assert.False(RolePermission.Has(UserRole.Engineer, Permission.EditPlcConnect));
        Assert.False(RolePermission.Has(UserRole.Operator, Permission.EditPlcConnect));
    }
}
