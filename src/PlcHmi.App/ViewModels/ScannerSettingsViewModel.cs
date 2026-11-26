using System.Globalization;
using System.IO;
using System.IO.Ports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PlcHmi.App.Services;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.App.ViewModels;

/// <summary>
/// 系统设置页「扫码枪设置」板块（2026-11-12 新增）。
///
/// <para><b>两块内容：</b>① 通讯（串口 / TCP/IP）与扫码枪参数；
/// ② 与 PLC 的握手信号——扫码成功信号寄存器（默认 D4050）与扫码触发寄存器（默认 D4010），
/// 以及工作模式（手动型 / 触发型）。</para>
///
/// <para><b>保存即生效：</b>走服务层校验 + 原子落盘 + 重连，不需要重启程序；
/// 只有管理员（编辑 PLC 连接权限）能改。</para>
/// </summary>
public sealed partial class ScannerSettingsViewModel : ObservableObject
{
    private readonly IScannerService _scanner;
    private readonly IUserService _userItem;
    private readonly IDialogService _dialog;
    private readonly ILogger<ScannerSettingsViewModel> _log;

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private int _communicationModeIndex;
    [ObservableProperty] private string _serialPortName = "COM1";
    [ObservableProperty] private string _baudRate = "9600";
    [ObservableProperty] private int _dataBitIndex = 3;      // 5/6/7/8 → 默认 8
    [ObservableProperty] private int _stopBitsIndex;          // 1 / 1.5 / 2 → 默认 1
    [ObservableProperty] private int _validateIndex;            // 无 / 奇 / 偶 → 默认 无
    [ObservableProperty] private string _ethernetAddress = "192.168.1.11";
    [ObservableProperty] private string _ethernetPort = "2000";
    [ObservableProperty] private string _scanSuccessRegister = "D4050";
    [ObservableProperty] private string _scanTriggerRegister = "D4010";
    [ObservableProperty] private int _workModeIndex;
    [ObservableProperty] private string _triggerCommand = "TRIGGER";
    [ObservableProperty] private string _releaseCommand = "RELEASE";
    [ObservableProperty] private string _pollingIntervalMilliseconds = "500";
    [ObservableProperty] private string _timeoutMilliseconds = "5000";
    [ObservableProperty] private bool _editableScanner;
    [ObservableProperty] private string _connectionStateText = "未启用";
    [ObservableProperty] private string _connectionStateColorKey = "MutedBrush";
    [ObservableProperty] private string _workOrderText = "（尚未扫码）";

    public ScannerSettingsViewModel(
        IScannerService Scanner,
        IUserService User,
        IDialogService Dialog,
        ILogger<ScannerSettingsViewModel> Log)
    {
        _scanner = Scanner;
        _userItem = User;
        _dialog = Dialog;
        _log = Log;

        _scanner.connectChanged += (_, Connected) => UiDispatch.Post(() => ApplyConnectionState(Connected));
        _scanner.workOrderChanged += (_, WorkOrder) => UiDispatch.Post(() => WorkOrderText = WorkOrder);
        _userItem.userChanged += (_, _) => UiDispatch.Post(RefreshPermission);

        FromConfigLoad();
        RefreshPermission();
        ApplyConnectionState(_scanner.Connected);

        if (!string.IsNullOrEmpty(_scanner.WorkOrder))
        {
            WorkOrderText = _scanner.WorkOrder;
        }
    }

    /// <summary>通讯方式下拉。</summary>
    public string[] CommunicationModeOptions { get; } = ["串口通讯", "TCP/IP 通讯"];

    /// <summary>工作模式下拉。</summary>
    public string[] WorkModeOptions { get; } = ["手动型", "触发型"];

    /// <summary>校验下拉。</summary>
    public string[] ValidateOptions { get; } = ["无", "奇", "偶"];

    /// <summary>停止位下拉。</summary>
    public string[] StopBitsOptions { get; } = ["1", "1.5", "2"];

    /// <summary>数据位下拉（索引 + 5）。</summary>
    public string[] DataBitOptions { get; } = ["5", "6", "7", "8"];

    /// <summary>
    /// 本机可用串口下拉项。
    ///
    /// <para><b>当前配置的串口必须在列表里</b>：扫码枪没插（或换了台机器）时
    /// <see cref="SerialPort.GetPortNames"/> 里没有 COMx，下拉就成了"配置写着 COM3、
    /// 选择框却空着"——现场会误以为配置丢了。所以枚举结果与当前串口号取并集。</para>
    /// </summary>
    [ObservableProperty] private string[] _availableSerialPort = [];

    /// <summary>刷新串口列表（插拔 USB 转串口后点一下）。</summary>
    [RelayCommand]
    private void RefreshSerialPort() => RefreshAvailableSerialPort();

    /// <summary>串口参数区是否可见（TCP 模式隐藏）。</summary>
    public bool IsSerialPortMode => CommunicationModeIndex == 0;

    /// <summary>网口参数区是否可见。</summary>
    public bool IsEthernetMode => CommunicationModeIndex == 1;

    partial void OnCommunicationModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsSerialPortMode));
        OnPropertyChanged(nameof(IsEthernetMode));
    }

    /// <summary>保存并立即应用（管理员）。</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!EditableScanner)
        {
            _dialog.Hint("权限不足", "仅管理员可修改扫码枪设置。");
            return;
        }

        if (!TryAssemble(out var candidate, out var error))
        {
            _dialog.ErrorInfo("扫码枪设置无效", error);
            return;
        }

        try
        {
            await _scanner.SaveSetAsync(candidate).ConfigureAwait(true);
            FromConfigLoad();
            _dialog.Hint("已保存", "扫码枪设置已保存并立即生效。");
        }
        catch (UnauthorizedAccessException exception)
        {
            _dialog.ErrorInfo("保存失败", exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            _dialog.ErrorInfo("保存失败", exception.Message);
        }
        catch (IOException exception)
        {
            _log.LogError(exception, "保存扫码枪设置失败");
            _dialog.ErrorInfo("保存失败", $"写入 appsettings.json 失败：{exception.Message}");
        }
    }

    /// <summary>按当前配置重连一次（串口拔插 / 扫码枪重启后用）。</summary>
    [RelayCommand]
    private async Task ReconnectAsync()
    {
        SafeInvokeAsync.FireAndForget(() => _scanner.RestartAsync(), _log, "重连扫码枪");

        await Task.Delay(300).ConfigureAwait(true);
        ApplyConnectionState(_scanner.Connected);
    }

    /// <summary>把运行配置搬到界面字段。</summary>
    private void FromConfigLoad()
    {
        var Config = _scanner.Config;

        Enabled = Config.Enabled;
        CommunicationModeIndex = (int)Config.CommunicationMode;
        SerialPortName = Config.SerialPortName;
        BaudRate = Config.BaudRate.ToString(CultureInfo.InvariantCulture);
        DataBitIndex = Math.Clamp(Config.DataBit - 5, 0, 3);
        StopBitsIndex = Config.StopBits?.Trim() switch { "2" => 2, "1.5" => 1, _ => 0 };
        ValidateIndex = Config.Validate?.Trim() switch { "奇" => 1, "偶" => 2, _ => 0 };
        EthernetAddress = Config.EthernetAddress;
        EthernetPort = Config.EthernetPort.ToString(CultureInfo.InvariantCulture);
        ScanSuccessRegister = Config.ScanSuccessRegister;
        ScanTriggerRegister = Config.ScanTriggerRegister;
        WorkModeIndex = (int)Config.WorkMode;
        TriggerCommand = Config.TriggerCommand;
        ReleaseCommand = Config.ReleaseCommand;
        PollingIntervalMilliseconds = Config.PollingIntervalMilliseconds.ToString(CultureInfo.InvariantCulture);
        TimeoutMilliseconds = Config.TimeoutMilliseconds.ToString(CultureInfo.InvariantCulture);

        RefreshAvailableSerialPort();
        OnPropertyChanged(nameof(IsSerialPortMode));
        OnPropertyChanged(nameof(IsEthernetMode));
    }

    private void RefreshPermission() => EditableScanner = _userItem.Has(Permission.EditPlcConnect);

    private void ApplyConnectionState(bool Connected)
    {
        if (!Enabled)
        {
            ConnectionStateText = "未启用";
            ConnectionStateColorKey = "MutedBrush";
            return;
        }

        ConnectionStateText = Connected ? "已连接" : "未连接";
        ConnectionStateColorKey = Connected ? "SuccessBrush" : "WarningBrush";
    }

    private bool TryAssemble(out ScannerConfig candidate, out string error)
    {
        candidate = new ScannerConfig();
        error = string.Empty;

        if (!int.TryParse(BaudRate.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var baudRateValue))
        {
            error = "波特率必须是整数（如 9600）。";
            return false;
        }

        if (!int.TryParse(EthernetPort.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var portValue))
        {
            error = "网口端口必须是整数（如 2000）。";
            return false;
        }

        if (!int.TryParse(PollingIntervalMilliseconds.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var intervalValue))
        {
            error = "轮询间隔必须是整数（毫秒）。";
            return false;
        }

        if (!int.TryParse(TimeoutMilliseconds.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeoutValue))
        {
            error = "扫码超时必须是整数（毫秒，如 5000）。";
            return false;
        }

        candidate.Enabled = Enabled;
        candidate.CommunicationMode = CommunicationModeIndex == 1 ? ScanCommunicationMode.TcpIp : ScanCommunicationMode.SerialPort;
        candidate.SerialPortName = SerialPortName.Trim();
        candidate.BaudRate = baudRateValue;
        candidate.DataBit = DataBitIndex + 5;
        candidate.StopBits = StopBitsOptions[Math.Clamp(StopBitsIndex, 0, 2)];
        candidate.Validate = ValidateOptions[Math.Clamp(ValidateIndex, 0, 2)];
        candidate.EthernetAddress = EthernetAddress.Trim();
        candidate.EthernetPort = portValue;
        candidate.ScanSuccessRegister = ScanSuccessRegister.Trim();
        candidate.ScanTriggerRegister = ScanTriggerRegister.Trim();
        candidate.WorkMode = WorkModeIndex == 1 ? ScanWorkMode.TriggerKind : ScanWorkMode.ManualKind;
        candidate.TriggerCommand = TriggerCommand;
        candidate.ReleaseCommand = ReleaseCommand;
        candidate.PollingIntervalMilliseconds = intervalValue;
        candidate.TimeoutMilliseconds = timeoutValue;

        return true;
    }

    /// <summary>重新枚举串口，并把当前配置的串口并进列表（保证选中项一定在下拉里）。</summary>
    private void RefreshAvailableSerialPort()
    {
        var Port = EnumSerialPort();
        var Current = SerialPortName?.Trim() ?? string.Empty;

        if (Current.Length > 0 && !Port.Contains(Current, StringComparer.OrdinalIgnoreCase))
        {
            Port = [.. Port, Current];
            Array.Sort(Port, StringComparer.OrdinalIgnoreCase);
        }

        AvailableSerialPort = Port;
    }

    private static string[] EnumSerialPort()
    {
        string[] Port;

        try
        {
            Port = SerialPort.GetPortNames();
        }
        catch (IOException)
        {
            return [];
        }
        catch (InvalidOperationException)
        {
            return [];
        }

        Array.Sort(Port, StringComparer.OrdinalIgnoreCase);
        return Port;
    }
}
