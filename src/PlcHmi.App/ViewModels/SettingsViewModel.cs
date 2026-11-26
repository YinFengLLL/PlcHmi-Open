using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PlcHmi.App.Services;
using PlcHmi.Business.Services;
using PlcHmi.Business.Configuration;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.App.ViewModels;

/// <summary>
/// 系统设置页 ViewModel。
///
/// <para>只读展示所有人可见；「连接参数编辑」区仅管理员（<see cref="Permission.EditPlcConnect"/>）可见。
/// 保存走服务层校验 + 原子落盘 + 断开重连，<b>立即生效</b>，不需要重启程序。</para>
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly PlcConnectionOptions _plcOptions;
    private readonly PollingOptions _pollingOptions;
    private readonly IConnectManager _connect;
    private readonly IPlcConnectionSettingsService _settings;
    private readonly IUserService _userItem;
    private readonly IDialogService _dialog;
    private readonly IVariableTableService? _variableTable;
    private readonly IFileDialogService _fileDialog;
    private readonly IRunModeManager? _runMode;
    private readonly UiOptions? _uiOptions;
    private readonly ILogger<SettingsViewModel> _log;

    [ObservableProperty] private string _connectionStateText = "未连接";
    [ObservableProperty] private string _connectionStateColorKey = "MutedBrush";
    [ObservableProperty] private bool _editableConnect;
    [ObservableProperty] private bool _importableVariableTable;

    // ── 连接参数编辑区（仅管理员）─────────────────────────────────────
    // 数值项用 string 承接输入框：解析失败由本层指出具体字段，范围校验由服务层兜底。
    [ObservableProperty] private string _editHost = string.Empty;
    [ObservableProperty] private string _editPort = string.Empty;
    [ObservableProperty] private string _editUnitId = string.Empty;
    [ObservableProperty] private string _editConnectTimeout = string.Empty;
    [ObservableProperty] private string _editReadTimeout = string.Empty;
    [ObservableProperty] private string _editHeartbeat = string.Empty;
    [ObservableProperty] private string _editReconnectInitial = string.Empty;
    [ObservableProperty] private string _editReconnectMax = string.Empty;
    [ObservableProperty] private string _editMaxRegister = string.Empty;
    [ObservableProperty] private bool _editAutoReconnect;

    // 通讯模式（协议下拉 0=Modbus TCP 1=OPC UA）与 OPC UA 参数
    [ObservableProperty] private int _editProtocolIndex;
    [ObservableProperty] private string _editOpcUaPort = string.Empty;
    [ObservableProperty] private string _editNamespace = string.Empty;

    /// <summary>协议下拉选项（索引与 通讯协议 枚举一一对应）。</summary>
    public string[] ProtocolOptions { get; } = ["Modbus TCP", "OPC UA"];

    /// <summary>可配置弹窗的监控服务（弹窗设置入口用；未注册时为 null，界面隐藏入口）。</summary>
    private readonly PopupMonitorService? _popupMonitor;

    /// <summary>M 区地址校验用解析器。</summary>
    private readonly IAddressParser? _parser;

    public SettingsViewModel(
        PlcConnectionOptions PlcOptions,
        PollingOptions PollingOptions,
        IConnectManager Connect,
        IPlcConnectionSettingsService Settings,
        IUserService User,
        IDialogService dialogCollection,
        IFileDialogService FileDialog,
        ILogger<SettingsViewModel> Log,
        IVariableTableService? VariableTable = null,
        IRunModeManager? RunMode = null,
        UiOptions? UiOptions = null,
        PopupMonitorService? PopupMonitor = null,
        IAddressParser? Parser = null,
        ScannerSettingsViewModel? ScannerSettings = null)
    {
        _plcOptions = PlcOptions;
        _pollingOptions = PollingOptions;
        _connect = Connect;
        _settings = Settings;
        _userItem = User;
        _dialog = dialogCollection;
        _fileDialog = FileDialog;
        _log = Log;
        _variableTable = VariableTable;
        _runMode = RunMode;
        _uiOptions = UiOptions;
        _popupMonitor = PopupMonitor;
        _parser = Parser;
        ScannerSettingsSection = ScannerSettings;

        Title = "系统设置";
        Subtitle = "通信与采集参数";

        _connect.StateChanged += (_, StateValue) => AtUiThreadExecute(() => ApplyState(StateValue));
        _userItem.userChanged += (_, _) => AtUiThreadExecute(RefreshPermission);
        _settings.saved += (_, _) => AtUiThreadExecute(() =>
        {
            RefreshConnectShow();
            LoadEditField();
        });

        if (_variableTable is not null)
        {
            _variableTable.alreadyReImport += (_, _) => AtUiThreadExecute(RefreshVariableTableShow);
        }

        if (_runMode is not null)
        {
            _runMode.modeChanged += (_, _) => AtUiThreadExecute(() =>
            {
                RefreshRunModeShow();
                RefreshConnectShow();
            });
        }

        ApplyState(_connect.State);
        RefreshPermission();
        LoadEditField();
        RefreshRunModeShow();
        RefreshVariableTableShow();
    }

    // ── 通信配置（只读展示）───────────────────────────────────────────

    public string Host => _plcOptions.Host;
    public int Port => _plcOptions.Port;
    public string UnitId => _plcOptions.UnitId.ToString();
    public string SeriesText => _plcOptions.Series switch
    {
        PlcSeries.SmallPlc => "小型 PLC（EASY320 / EASY52X / H5U / H3U）",
        PlcSeries.MediumPlc => "中型 PLC（AM600 / AC800）",
        _ => "未知",
    };

    public string ConnectTimeoutText => $"{_plcOptions.ConnectTimeoutMilliseconds} ms";
    public string ReadTimeoutText => $"{_plcOptions.ReadTimeoutMilliseconds} ms";
    public string HeartbeatText => $"{_plcOptions.HeartbeatIntervalMilliseconds} ms";
    public string MaxRegisterText => $"{_plcOptions.PerRequestMaxRegisterNumber} 个 / 次";
    public string AutoReconnectText => _plcOptions.AutoReconnect ? "已启用" : "已禁用";

    // ── 采集配置 ───────────────────────────────────────────────────────

    public string FastIntervalText => $"{_pollingOptions.FastIntervalMilliseconds} ms";
    public string SlowIntervalText => $"{_pollingOptions.SlowIntervalMilliseconds} ms";
    public string EventIntervalText => $"{_pollingOptions.EventIntervalMilliseconds} ms";

    /// <summary>配置建议提示。</summary>
    public string BackupNoteText =>
        "汇川 PLC 单次读寄存器上限 123、写上限 121，本程序按每块 100 个寄存器保守取值；" +
        "H5U 默认最多允许 8 个 Modbus TCP 客户端连接，故上位机只维持一个连接。";

    [RelayCommand]
    private async Task PopupSetAsync()
    {
        if (_popupMonitor is null || _parser is null)
        {
            _dialog.Hint("弹窗设置", "弹窗服务未就绪，请重启程序后重试。");
            return;
        }

        var result = Views.PopupSettingsDialog.Show(_popupMonitor, _parser);

        if (result is null)
        {
            return;
        }

        if (await UiCommand
                .ExecuteAsync(_log, _dialog, "保存弹窗设置", () => _popupMonitor.SaveCollectionAsync(result))
                .ConfigureAwait(true))
        {
            _dialog.Hint("弹窗设置", $"已保存 {result.Count} 个弹窗，立即生效。");
        }
    }

    [RelayCommand]
    private void TestConnect()
    {
        var StateValue = _connect.State;
        _dialog.Hint("连接状态测试",
            StateValue == ConnectionState.Connected
                ? $"连接正常。\n\n目标：{Host}:{Port}\n模式：{ModeText}"
                : $"当前未连接。\n\n目标：{Host}:{Port}\n状态：{ConnectionStateText}\n\n程序会按指数退避自动重连。");
    }

    /// <summary>
    /// 保存连接参数（仅管理员）。
    ///
    /// <para>流程：输入解析 → 服务层范围校验 → 二次确认（连接会中断几秒）→
    /// 服务保存（落盘 + 更新共享参数 + 重启连接）→ 刷新展示。
    /// 解析失败在本层指出具体字段；范围校验以服务层为准——界面校验只是提前反馈，不是防线。</para>
    /// </summary>
    [RelayCommand]
    private async Task SaveConnectAsync()
    {
        if (!EditableConnect)
        {
            _dialog.Hint("权限不足", "只有管理员可以修改 PLC 连接参数。");
            return;
        }

        if (!TryParseInteger(EditPort, "端口", out var portNumber) ||
            !TryParseInteger(EditUnitId, "从站号", out var unitIdValue) ||
            !TryParseInteger(EditConnectTimeout, "连接超时", out var connectTimeout) ||
            !TryParseInteger(EditReadTimeout, "读写超时", out var readTimeout) ||
            !TryParseInteger(EditHeartbeat, "心跳间隔", out var heartbeat) ||
            !TryParseInteger(EditReconnectInitial, "重连初始退避", out var reconnectInitial) ||
            !TryParseInteger(EditReconnectMax, "重连退避上限", out var reconnectMax) ||
            !TryParseInteger(EditMaxRegister, "单次最大寄存器", out var maxRegister) ||
            !TryParseInteger(EditOpcUaPort, "OPC UA 端口", out var opcuaPort) ||
            !TryParseInteger(EditNamespace, "OPC UA 命名空间索引", out var ns) ||
            !TryParseInteger(EditSimulationSlavePort, "模拟从站端口", out var SimulationSlavePort))
        {
            return;
        }

        if (EditProtocolIndex is < 0 or > 1)
        {
            _dialog.Hint("参数有误", "通讯协议选择无效。");
            return;
        }

        var originalProtocol = _plcOptions.CommunicationProtocol;

        var candidate = new PlcConnectionEditModel
        {
            Host = EditHost,
            Port = portNumber,
            UnitId = unitIdValue,
            ConnectTimeoutMilliseconds = connectTimeout,
            ReadTimeoutMilliseconds = readTimeout,
            HeartbeatIntervalMilliseconds = heartbeat,
            ReconnectInitialDelayMilliseconds = reconnectInitial,
            ReconnectMaxDelayMilliseconds = reconnectMax,
            PerRequestMaxRegisterNumber = maxRegister,
            AutoReconnect = EditAutoReconnect,
            CommunicationProtocol = (CommunicationProtocol)EditProtocolIndex,
            OpcUaPort = opcuaPort,
            OpcUaNamespaceIndex = ns,
            SimulationSlavePort = SimulationSlavePort,
            SimulationSlaveListenAddress = EditSimulationSlaveListenAddress,
        };

        var errorList = _settings.Validate(candidate);
        if (errorList.Count > 0)
        {
            _dialog.Hint("参数有误", string.Join("\n\n", errorList));
            return;
        }

        // 协议切换属重启级变更：保存前必须知情确认
        if (candidate.CommunicationProtocol != originalProtocol)
        {
            var targetName = candidate.CommunicationProtocol == CommunicationProtocol.OPCUA ? "OPC UA" : "Modbus TCP";

            if (!_dialog.Confirm("切换通讯协议",
                    $"确认把通讯模式从 {originalProtocol} 切换为 {targetName}？",
                    Views.ConfirmLevel.Warning, "保存并重启程序生效",
                    "协议由程序装配定型：保存写入配置文件，重启程序后按新协议连接；\n"
                    + "当前会话仍按旧协议运行。OPC UA 端口与命名空间也一并写入。"))
            {
                return;
            }
        }

        // 改 IP / 端口会断开当前连接：如果是生产中的连接，属于必须知情的中断
        if (!_dialog.Confirm("变更 PLC 连接",
                $"确认把连接目标改为 {candidate.Host.Trim()}:{candidate.Port}（从站号 {candidate.UnitId}）？",
                Views.ConfirmLevel.Warning, "保存并重连",
                "保存后会断开当前连接并用新参数重连，几秒内恢复；期间采集与写入暂停。\n"
                + "参数同时写入 config/appsettings.json，重启程序后仍生效。"))
        {
            return;
        }

        try
        {
            // 不加 ConfigureAwait(false)：后续弹窗与展示刷新需要回到 UI 线程
            await _settings.SaveAsync(candidate);
        }
        catch (InvalidOperationException exception)
        {
            _dialog.Hint("参数有误", exception.Message);
            return;
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "保存 PLC 连接参数失败");
            _dialog.Hint("保存失败",
                $"参数已校验通过，但写入配置文件失败：\n{exception.Message}\n\n请检查磁盘与权限后重试。");
            return;
        }

        if (candidate.CommunicationProtocol != originalProtocol)
        {
            _dialog.Hint("已保存，重启后切换协议",
                $"通讯模式已写入配置文件：{candidate.CommunicationProtocol}。\n\n"
                + "协议由程序装配定型，请重启程序以按新协议连接；\n"
                + "重启前当前会话仍按原协议运行。");
            return;
        }

        _dialog.Hint("已生效",
            $"PLC 连接参数已保存并生效：{candidate.Host.Trim()}:{candidate.Port}。\n\n连接正在用新参数重连。");
    }

    /// <summary>把当前生效参数灌入编辑框（初始化 / 保存成功后调用）。</summary>
    private void LoadEditField()
    {
        var modelPairObject = _settings.CreateEditModel();
        EditHost = modelPairObject.Host;
        EditPort = modelPairObject.Port.ToString();
        EditUnitId = modelPairObject.UnitId.ToString();
        EditConnectTimeout = modelPairObject.ConnectTimeoutMilliseconds.ToString();
        EditReadTimeout = modelPairObject.ReadTimeoutMilliseconds.ToString();
        EditHeartbeat = modelPairObject.HeartbeatIntervalMilliseconds.ToString();
        EditReconnectInitial = modelPairObject.ReconnectInitialDelayMilliseconds.ToString();
        EditReconnectMax = modelPairObject.ReconnectMaxDelayMilliseconds.ToString();
        EditMaxRegister = modelPairObject.PerRequestMaxRegisterNumber.ToString();
        EditAutoReconnect = modelPairObject.AutoReconnect;
        EditProtocolIndex = (int)modelPairObject.CommunicationProtocol;
        EditOpcUaPort = modelPairObject.OpcUaPort.ToString();
        EditNamespace = modelPairObject.OpcUaNamespaceIndex.ToString();
        EditSimulationSlavePort = modelPairObject.SimulationSlavePort.ToString();
        EditSimulationSlaveListenAddress = modelPairObject.SimulationSlaveListenAddress;
    }

    /// <summary>刷新只读展示区（这些属性直接读共享单例，保存后需要手动通知）。</summary>
    private void RefreshConnectShow()
    {
        OnPropertyChanged(nameof(Host));
        OnPropertyChanged(nameof(Port));
        OnPropertyChanged(nameof(UnitId));
        OnPropertyChanged(nameof(ConnectTimeoutText));
        OnPropertyChanged(nameof(ReadTimeoutText));
        OnPropertyChanged(nameof(HeartbeatText));
        OnPropertyChanged(nameof(MaxRegisterText));
        OnPropertyChanged(nameof(AutoReconnectText));
        OnPropertyChanged(nameof(ModeText));
    }

    private void RefreshPermission()
    {
        // 改 PLC 连接目标是高危操作：只给管理员
        EditableConnect = _userItem.Has(Permission.EditPlcConnect);
        ImportableVariableTable = _userItem.Has(Permission.ImportVariableTable);

        // 运行模式的"可切换"也含权限维度：身份变化后要重算
        RefreshRunModeShow();
    }

    /// <summary>解析整数输入框，失败时弹窗指出具体字段并返回 false。</summary>
    private bool TryParseInteger(string Text, string TagName, out int Value)
    {
        if (int.TryParse(Text?.Trim(), out Value))
        {
            return true;
        }

        Value = 0;
        _dialog.Hint("输入有误", $"「{TagName}」必须是整数，当前输入：{Text}");
        return false;
    }

    private void ApplyState(ConnectionState StateValue)
    {
        (ConnectionStateText, ConnectionStateColorKey) = StateValue switch
        {
            ConnectionState.Connected => ("已连接", "SuccessBrush"),
            ConnectionState.Connecting => ("连接中", "WarningBrush"),
            ConnectionState.Reconnecting => ("重连中", "WarningBrush"),
            ConnectionState.Fault => ("连接故障", "DangerBrush"),
            _ => ("未连接", "MutedBrush"),
        };
    }
}
