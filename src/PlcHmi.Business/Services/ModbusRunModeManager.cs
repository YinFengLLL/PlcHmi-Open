using Microsoft.Extensions.Logging;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;
using PlcHmi.Communication.Modbus;
using PlcHmi.Communication.Simulation;

namespace PlcHmi.Business.Services;

/// <summary>
/// Modbus 运行模式管理器。在运行期把上位机在「真实 PLC / 模拟 PLC」之间切换，
/// 并负责模拟模式下供外部工具离线调试的从站启停。
///
/// <para><b>切换的完整动作：</b>停连接（断开当前对端）→ 替换客户端实现（真实 ↔ 仿真）→
/// 起/停模拟从站 → 重启连接。全程不重建 DI、不重启程序。</para>
///
/// <para><b>状态一致性的关键：</b>连接管理器注入的是 <see cref="SwitchableModbusClient"/> 这一层代理，
/// 它始终指向"当前生效"的实现。因此切换只发生在服务层这一处，其它服务无感知。</para>
///
/// <para><b>不落盘：</b>运行模式仅存在于内存（<see cref="_options"/> 的模拟开关只在运行期改动，
/// 保存连接参数时由 <see cref="RunModeStrategy.PersistSimulationSwitch"/> 强制写 false）。下次启动照旧进入真实 PLC。</para>
/// </summary>
public sealed class ModbusRunModeManager : IRunModeManager, IAsyncDisposable
{
    private readonly SwitchableModbusClient _clientProxy;
    private readonly FluentModbusTCPClient _realClient;
    private readonly SimulationModbusClient _simulationClient;
    private readonly VirtualPlcSlave _slave;
    private readonly IConnectManager _connect;
    private readonly PlcConnectionOptions _options;
    private readonly IPermissionGuard _permission;
    private readonly ILogger<ModbusRunModeManager> _log;

    /// <summary>切换是"停—换—起"三段式，必须串行化，避免两次切换交叉执行。</summary>
    private readonly SemaphoreSlim _switchGate = new(1, 1);

    private volatile RunMode _currentMode;
    private volatile bool _isSwitching;

    public ModbusRunModeManager(
        SwitchableModbusClient ClientProxy,
        FluentModbusTCPClient RealClient,
        SimulationModbusClient SimulationClient,
        VirtualPlcSlave Slave,
        IConnectManager Connect,
        PlcConnectionOptions Options,
        IPermissionGuard Permission,
        ILogger<ModbusRunModeManager> Log)
    {
        _clientProxy = ClientProxy;
        _realClient = RealClient;
        _simulationClient = SimulationClient;
        _slave = Slave;
        _connect = Connect;
        _options = Options;
        _permission = Permission;
        _log = Log;

        // 初始模式跟随 DI 装配（由 使用模拟器 决定）：产品启动流程会把 Modbus 的 使用模拟器
        // 强制为 false（见 App 启动策略），因此现场每次启动都是真实 PLC；测试可注入 true。
        _currentMode = _options.UseSimulator ? RunMode.SimulationPlc : RunMode.RealPlc;
    }

    /// <inheritdoc />
    public RunMode CurrentMode => _currentMode;

    /// <inheritdoc />
    public bool CanAtRuntimeSwitch => RunModeStrategy.RuntimeSwitchable(_options.CommunicationProtocol);

    /// <inheritdoc />
    public bool IsSwitching => _isSwitching;

    /// <inheritdoc />
    public bool SimulationSlaveIsRun => _slave.IsRun;

    /// <inheritdoc />
    public string SimulationSlaveEndpointText => _slave.EndpointText;

    /// <inheritdoc />
    public string SimulationSlaveStateText => _slave.StateText;

    /// <inheritdoc />
    public event EventHandler? modeChanged;

    /// <inheritdoc />
    public async Task SwitchAsync(RunMode target, CancellationToken cancellationToken = default)
    {
        if (!CanAtRuntimeSwitch)
        {
            throw new InvalidOperationException(
                "当前通讯协议不支持运行期切换运行模式。OPC UA 模式请改用「通讯模式 + 重启程序」的方式切换。");
        }

        // 切到模拟模式会解除对真实设备的反馈约束：必须是管理员显式授权
        _permission.WantRequire(Permission.SwitchRunMode);

        await _switchGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (target == _currentMode)
            {
                return;
            }

            // 进入模拟模式前先拉起从站：监听失败就在"什么都没改变"的状态下明确报错，
            // 而不是切完一半留个外部工具连不上的僵局。
            if (target == RunMode.SimulationPlc)
            {
                await _slave.LaunchAsync(cancellationToken).ConfigureAwait(false);
            }

            _isSwitching = true;

            try
            {
                await _connect.StopAsync(cancellationToken).ConfigureAwait(false);

                if (target == RunMode.SimulationPlc)
                {
                    _clientProxy.SwitchRealCurrent(_simulationClient);
                    _options.UseSimulator = true;
                }
                else
                {
                    _clientProxy.SwitchRealCurrent(_realClient);
                    _options.UseSimulator = false;
                }

                await _connect.StartAsync(cancellationToken).ConfigureAwait(false);

                if (target == RunMode.RealPlc)
                {
                    await _slave.StopAsync().ConfigureAwait(false);
                }

                _currentMode = target;

                _log.LogWarning("运行模式已切换为 {Mode}：{Detail}", target,
                    target == RunMode.SimulationPlc
                        ? $"模拟从站监听 {_slave.EndpointText}，可用 Modbus 工具离线调试"
                        : "已恢复真实 PLC 连接");
            }
            finally
            {
                _isSwitching = false;
            }

            modeChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _switchGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _slave.StopAsync().ConfigureAwait(false);
        _switchGate.Dispose();
    }
}
