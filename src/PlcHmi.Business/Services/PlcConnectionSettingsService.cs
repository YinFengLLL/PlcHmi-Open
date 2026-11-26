using System.Text.Json;
using Microsoft.Extensions.Logging;
using PlcHmi.Business.Configuration;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// PLC 连接参数管理（系统设置页，仅管理员）。
///
/// <para><b>生效机制</b>：通信层客户端（<c>FluentModbusTcpClient</c>）每次连接时都从共享的
/// <see cref="PlcConnectionOptions"/> 单例现读 IP / 端口，连接管理器 <c>StopAsync</c> 会断开
/// 底层 TCP。因此"保存"= 更新共享参数 → 原子落盘 → 重启连接管理器，新地址立即生效，无需重启程序。</para>
///
/// <para><b>为什么 Series / UseSimulator 不开放编辑</b>：前者决定地址解析体系（换机型属 L3 高风险），
/// 后者由 DI 装配定型（运行期改了无效）——把"改了不起作用"的入口挡在模型之外。</para>
/// </summary>
public sealed class PlcConnectionSettingsService : IPlcConnectionSettingsService
{
    /// <summary>Modbus TCP 标准端口。</summary>
    private const int portMin = 1;

    private const int portMax = 65535;

    /// <summary>Modbus 从站号合法范围（0 是广播，TCP 主站常规寻址不用）。</summary>
    private const int unitIdMin = 1;

    private const int unitIdMax = 247;

    /// <summary>低于 100ms 的超时在任何真实网络上都不现实，只会在 PLC 忙时误判断线。</summary>
    private const int timeoutMinMilliseconds = 100;

    /// <summary>心跳过密会平白占用 PLC 连接处理能力（H5U 默认最多 8 客户端）。</summary>
    private const int heartbeatMinMilliseconds = 500;

    /// <summary>退避小于 100ms 断线风暴时会打挂 PLC。</summary>
    private const int reconnectMinMilliseconds = 100;

    /// <summary>汇川 PLC 单次读寄存器上限 123（本程序文档建议保守取 100）。</summary>
    private const int maxRegisterLowerLimit = 1;

    private const int maxRegisterUpperLimit = 123;

    /// <summary>OPC UA 命名空间索引合法范围。ns=0 是标准命名空间保留段，现场从 1 起用。</summary>
    private const int namespaceMin = 1;

    private const int namespaceMax = 65535;

    private readonly PlcConnectionOptions _options;
    private readonly IConnectManager _connect;
    private readonly ILogger<PlcConnectionSettingsService> _log;

    /// <summary>改连接参数会断开重连，属管理员专有。</summary>
    private readonly IPermissionGuard _permission;

    /// <inheritdoc />
    public event EventHandler? saved;

    public PlcConnectionSettingsService(
        PlcConnectionOptions Options,
        IConnectManager Connect,
        IPermissionGuard Permission,
        ILogger<PlcConnectionSettingsService> Log)
    {
        _options = Options;
        _connect = Connect;
        _permission = Permission;
        _log = Log;
    }

    /// <inheritdoc />
    public PlcConnectionEditModel CreateEditModel() => new()
    {
        Host = _options.Host,
        Port = _options.Port,
        UnitId = _options.UnitId,
        ConnectTimeoutMilliseconds = _options.ConnectTimeoutMilliseconds,
        ReadTimeoutMilliseconds = _options.ReadTimeoutMilliseconds,
        HeartbeatIntervalMilliseconds = _options.HeartbeatIntervalMilliseconds,
        ReconnectInitialDelayMilliseconds = _options.ReconnectInitialDelayMilliseconds,
        ReconnectMaxDelayMilliseconds = _options.ReconnectMaxDelayMilliseconds,
        PerRequestMaxRegisterNumber = _options.PerRequestMaxRegisterNumber,
        AutoReconnect = _options.AutoReconnect,
        CommunicationProtocol = _options.CommunicationProtocol,
        OpcUaPort = _options.OpcUaPort,
        OpcUaNamespaceIndex = _options.OpcUaNamespaceIndex,
        SimulationSlavePort = _options.SimulationSlavePort,
        SimulationSlaveListenAddress = _options.SimulationSlaveListenAddress,
    };

    /// <inheritdoc />
    public IReadOnlyList<string> Validate(PlcConnectionEditModel candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var errorList = new List<string>();
        var Host = candidate.Host.Trim();

        if (Host.Length == 0)
        {
            errorList.Add("PLC 地址不能为空。");
        }
        else if (Uri.CheckHostName(Host) == UriHostNameType.Unknown)
        {
            errorList.Add($"PLC 地址「{Host}」不是合法的 IP 地址或主机名。");
        }

        if (candidate.Port is < portMin or > portMax)
        {
            errorList.Add($"端口必须在 {portMin}–{portMax} 之间（标准 Modbus TCP 为 502），当前 {candidate.Port}。");
        }

        if (candidate.UnitId is < unitIdMin or > unitIdMax)
        {
            errorList.Add($"从站号必须在 {unitIdMin}–{unitIdMax} 之间，当前 {candidate.UnitId}。");
        }

        if (candidate.ConnectTimeoutMilliseconds < timeoutMinMilliseconds)
        {
            errorList.Add($"连接超时不能小于 {timeoutMinMilliseconds}ms，当前 {candidate.ConnectTimeoutMilliseconds}ms。");
        }

        if (candidate.ReadTimeoutMilliseconds < timeoutMinMilliseconds)
        {
            errorList.Add($"读写超时不能小于 {timeoutMinMilliseconds}ms，当前 {candidate.ReadTimeoutMilliseconds}ms。");
        }

        if (candidate.HeartbeatIntervalMilliseconds < heartbeatMinMilliseconds)
        {
            errorList.Add($"心跳间隔不能小于 {heartbeatMinMilliseconds}ms（过密会占用 PLC 连接处理能力），当前 {candidate.HeartbeatIntervalMilliseconds}ms。");
        }

        if (candidate.ReconnectInitialDelayMilliseconds < reconnectMinMilliseconds)
        {
            errorList.Add($"重连初始退避不能小于 {reconnectMinMilliseconds}ms，当前 {candidate.ReconnectInitialDelayMilliseconds}ms。");
        }

        if (candidate.ReconnectMaxDelayMilliseconds < candidate.ReconnectInitialDelayMilliseconds)
        {
            errorList.Add($"重连退避上限（{candidate.ReconnectMaxDelayMilliseconds}ms）不能小于初始退避（{candidate.ReconnectInitialDelayMilliseconds}ms）。");
        }

        if (candidate.PerRequestMaxRegisterNumber is < maxRegisterLowerLimit or > maxRegisterUpperLimit)
        {
            errorList.Add($"单次最大寄存器数必须在 {maxRegisterLowerLimit}–{maxRegisterUpperLimit} 之间（汇川读上限 123），当前 {candidate.PerRequestMaxRegisterNumber}。");
        }

        if (candidate.OpcUaPort is < portMin or > portMax)
        {
            errorList.Add($"OPC UA 端口必须在 {portMin}–{portMax} 之间（汇川内置服务端默认 4840），当前 {candidate.OpcUaPort}。");
        }

        if (candidate.OpcUaNamespaceIndex is < namespaceMin or > namespaceMax)
        {
            errorList.Add($"OPC UA 命名空间索引必须在 {namespaceMin}–{namespaceMax} 之间（默认 2），当前 {candidate.OpcUaNamespaceIndex}。");
        }

        // 模拟从站参数只在 Modbus 模式的模拟运行期生效；仍然校验格式，
        // 避免管理员写下一个切到模拟模式时才暴露的坏端口 / 坏地址。
        if (candidate.SimulationSlavePort is < portMin or > portMax)
        {
            errorList.Add($"模拟从站端口必须在 {portMin}–{portMax} 之间（默认 5020，避开真实 PLC 的 502），当前 {candidate.SimulationSlavePort}。");
        }

        var simulationListenAddress = candidate.SimulationSlaveListenAddress.Trim();

        if (simulationListenAddress.Length > 0 && !System.Net.IPAddress.TryParse(simulationListenAddress, out _))
        {
            errorList.Add($"模拟从站监听地址「{simulationListenAddress}」不是合法的 IP 地址（本机调试填 127.0.0.1，跨机调试填 0.0.0.0 或本机网卡地址）。");
        }

        return errorList;
    }

    /// <inheritdoc />
    public async Task SaveAsync(PlcConnectionEditModel candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        _permission.WantRequire(Permission.EditPlcConnect);

        var errorList = Validate(candidate);
        if (errorList.Count > 0)
        {
            throw new InvalidOperationException(string.Join("\n", errorList));
        }

        var Host = candidate.Host.Trim();

        // 先落盘、后改内存：写入是原子的（AtomicFile），任一步失败内存与文件都保持旧值一致。
        // Series / 通讯协议 / 使用模拟器 以内存运行态为准写回，避免"文件缺失回退默认值"覆盖掉真实运行模式。
        // OPC UA 专属参数（端口/命名空间/节点格式/账号）同为 DI 定型参数，原样带过以防落盘丢失。
        //
        // 使用模拟器的落盘值走 运行模式策略：Modbus 模式下恒写 false —— 运行模式是运行期状态，
        // 不该被"保存连接参数"这个动作顺手记进配置文件（否则下次启动就记住了上次的模拟选择）。
        var Snapshot = new PlcConnectionOptions
        {
            Host = Host,
            Port = candidate.Port,
            // 运行参数里 UnitId 是 byte（Modbus 从站号 1–247 恰在其范围内），校验已保证可安全收窄
            UnitId = (byte)candidate.UnitId,
            Series = _options.Series,
            UseSimulator = RunModeStrategy.PersistSimulationSwitch(candidate.CommunicationProtocol, _options.UseSimulator),
            CommunicationProtocol = candidate.CommunicationProtocol,
            OpcUaPort = candidate.OpcUaPort,
            OpcUaNamespaceIndex = candidate.OpcUaNamespaceIndex,
            OpcUaSectionPointFormat = _options.OpcUaSectionPointFormat,
            OpcUaUserName = _options.OpcUaUserName,
            OpcUaPassword = _options.OpcUaPassword,
            SimulationSlavePort = candidate.SimulationSlavePort,
            SimulationSlaveListenAddress = candidate.SimulationSlaveListenAddress.Trim(),
            ConnectTimeoutMilliseconds = candidate.ConnectTimeoutMilliseconds,
            ReadTimeoutMilliseconds = candidate.ReadTimeoutMilliseconds,
            HeartbeatIntervalMilliseconds = candidate.HeartbeatIntervalMilliseconds,
            ReconnectInitialDelayMilliseconds = candidate.ReconnectInitialDelayMilliseconds,
            ReconnectMaxDelayMilliseconds = candidate.ReconnectMaxDelayMilliseconds,
            PerRequestMaxRegisterNumber = candidate.PerRequestMaxRegisterNumber,
            AutoReconnect = candidate.AutoReconnect,
        };

        await PersistAsync(Snapshot, cancellationToken).ConfigureAwait(false);

        ApplyRuntime(Snapshot);

        // 重启连接循环：StopAsync 末尾会断开底层 TCP，StartAsync 重新起循环，
        // 循环内 ConnectAsync 会用刚更新的 Host/Port 建立新连接。轮询引擎无需重启——
        // 它未连接时本就空转等待，重连成功后自动恢复采集。
        await _connect.StopAsync(cancellationToken).ConfigureAwait(false);
        await _connect.StartAsync(cancellationToken).ConfigureAwait(false);

        _log.LogWarning(
            "PLC 连接参数已变更并生效：{Host}:{Port}（从站号 {UnitId}，自动重连 {Auto}）",
            Host, candidate.Port, candidate.UnitId, candidate.AutoReconnect ? "开" : "关");

        saved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>把参数整体写入 config/appsettings.json（原子写；其余节原样保留）。</summary>
    private async Task PersistAsync(PlcConnectionOptions Snapshot, CancellationToken cancellationToken)
    {
        // 2026-11-19 安全加固：这里是"加载整份 → 改 Plc 节 → 整体写回"。
        // 若主文件与 .bak 都坏了，加载器只能给出出厂默认 —— 直接写回就等于
        // 用默认值抹掉现场的扫码枪 / 轮询 / 轴定义配置。因此这种情况必须拒绝落盘。
        var result = AppSettingsLoader.LoadWithResult();

        var Document = result.loadResult switch
        {
            LoadResultSplitCategory.Loaded => result.Value!,

            LoadResultSplitCategory.Missing => new AppSettingsDocument(),   // 首次部署：按默认文档创建

            LoadResultSplitCategory.RestoredFromBackup => result.Value!,

            _ => throw new InvalidOperationException(
                $"应用配置读取失败（主文件与备份均不可用），为避免覆盖现场配置已中止保存：{result.ErrorInfo}"),
        };

        if (result.loadResult is LoadResultSplitCategory.RestoredFromBackup)
        {
            // 允许继续，但必须留痕：现场需要知道"最近一次改动可能已丢失"
            _log.LogWarning(
                "保存 PLC 连接参数前发现 appsettings.json 损坏，已从 .bak 恢复后继续：{Path}", result.path);
        }

        Document.PlcConnection = Snapshot;

        // appsettings.json 现为 PascalCase 风格（与 tags.json 等人工手写的 camelCase 文件不同口径），
        // 用 Write 保持风格不漂移
        var jsonText = JsonSerializer.Serialize(Document, JsonConfig.Write);

        try
        {
            await AtomicFile.WriteAllTextAsync(ConfigPath.AppSettings, jsonText, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "保存 PLC 连接参数失败：{Path}", ConfigPath.AppSettings);
            throw;
        }
    }

    /// <summary>把已落盘的参数同步进共享单例。通信层每次连接现读这些值，改完即对下次连接生效。</summary>
    private void ApplyRuntime(PlcConnectionOptions Snapshot)
    {
        _options.Host = Snapshot.Host;
        _options.Port = Snapshot.Port;
        _options.UnitId = Snapshot.UnitId;
        _options.ConnectTimeoutMilliseconds = Snapshot.ConnectTimeoutMilliseconds;
        _options.ReadTimeoutMilliseconds = Snapshot.ReadTimeoutMilliseconds;
        _options.HeartbeatIntervalMilliseconds = Snapshot.HeartbeatIntervalMilliseconds;
        _options.ReconnectInitialDelayMilliseconds = Snapshot.ReconnectInitialDelayMilliseconds;
        _options.ReconnectMaxDelayMilliseconds = Snapshot.ReconnectMaxDelayMilliseconds;
        _options.PerRequestMaxRegisterNumber = Snapshot.PerRequestMaxRegisterNumber;
        _options.AutoReconnect = Snapshot.AutoReconnect;
        _options.CommunicationProtocol = Snapshot.CommunicationProtocol;
        _options.OpcUaPort = Snapshot.OpcUaPort;
        _options.OpcUaNamespaceIndex = Snapshot.OpcUaNamespaceIndex;
        _options.SimulationSlavePort = Snapshot.SimulationSlavePort;
        _options.SimulationSlaveListenAddress = Snapshot.SimulationSlaveListenAddress;
    }
}
