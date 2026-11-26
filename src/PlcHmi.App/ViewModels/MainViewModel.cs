using System.Reflection;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PlcHmi.App.Services;
using PlcHmi.Business.Services;
using PlcHmi.Business.Configuration;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.App.ViewModels;

/// <summary>导航项。</summary>
public sealed class NavigationEntry
{
    public NavigationEntry(string titleText, string iconKeyValue, ViewModelBase pageItem)
    {
        Title = titleText;
        IconKey = iconKeyValue;
        Page = pageItem;
    }

    /// <summary>导航标题。</summary>
    public string Title { get; }

    /// <summary>导航图标资源键（Images/PlcHmi.Icons.xaml 中的 Geometry）。</summary>
    public string IconKey { get; }

    /// <summary>目标页面。</summary>
    public ViewModelBase Page { get; }
}

/// <summary>
/// 主窗口 ViewModel。负责导航、连接状态、时间与底部报警条。
/// </summary>
public sealed partial class MasterViewModel : ObservableObject
{
    /// <summary>
    /// 导航项 → 访问所需权限。
    /// <para>操作员只有运行监控 + IO/报警（只读）；工程师与管理员可见全部。</para>
    /// </summary>
    private static readonly Dictionary<string, Permission> navigationPermission = new()
    {
        ["运行监控"] = Permission.ViewRunPage,
        ["参数设置"] = Permission.ViewAllPage,
        ["锁附示教"] = Permission.ViewAllPage,
        ["点胶示教"] = Permission.ViewAllPage,
        ["标定 · 其它点"] = Permission.ViewAllPage,
        ["机种管理"] = Permission.ViewAllPage,
        ["手动调试"] = Permission.ViewAllPage,
        ["IO 点位"] = Permission.ViewMonitorControlPage,
        ["报警记录"] = Permission.ViewMonitorControlPage,
        ["历史记录"] = Permission.ViewMonitorControlPage,
        ["系统设置"] = Permission.ViewAllPage,
    };

    /// <summary>
    /// 页面号映射（写给 PLC 的 D10）：<b>主监控 = 1</b>，按侧栏固定顺序递增。
    ///
    /// <para><b>为什么用固定表而不是集合下标：</b>导航集合会按身份过滤（操作员只看得到 4 项），
    /// 用下标会让"参数设置"在管理员眼里是 2、在操作员眼里是 1 —— 同一页面两个号码，
    /// PLC 侧的程序分支必然错乱。所以页码只认标题。</para>
    /// </summary>
    private static readonly Dictionary<string, int> pageNumberMap = new()
    {
        ["运行监控"] = 1,
        ["参数设置"] = 2,
        ["锁附示教"] = 3,
        ["点胶示教"] = 4,
        ["标定 · 其它点"] = 5,
        ["机种管理"] = 6,
        ["手动调试"] = 7,
        ["IO 点位"] = 8,
        ["报警记录"] = 9,
        ["历史记录"] = 10,
        ["系统设置"] = 11,
    };

    private readonly IServiceProvider _service;
    private readonly IConnectManager _connect;
    private readonly IAlarmService _alarm;
    private readonly IUserService _userItem;
    private readonly IConfigHealth _configHealth;
    private readonly IRuntimeErrorReceiver _runtimeErrorList;
    private readonly IDialogService _dialog;
    private readonly PlcConnectionOptions _plcOptions;
    private readonly IRunModeManager? _runMode;
    private readonly IPageContextService _pageContext;
    private readonly ILogger<MasterViewModel> _log;
    private readonly DispatcherTimer _clock;

    private ConnectionState _lastConnectionState = ConnectionState.Disconnected;  // 只在「已连接 → 非已连接」时触发失效全部()

    /// <summary>弹窗宿主（M 触发线圈 → 主窗居中弹窗，定义见 config/popups.json）。</summary>
    public PopupHostViewModel PopupHost { get; }

    [ObservableProperty] private NavigationEntry? _selectedNavigationEntry;
    [ObservableProperty] private ViewModelBase? _currentPage;
    [ObservableProperty] private string _connectionStateText = "未连接";
    [ObservableProperty] private string _connectionStateColorKey = "MutedBrush";
    [ObservableProperty] private string _currentTimeText = string.Empty;
    [ObservableProperty] private string _alarmRowText = "当前无报警";
    [ObservableProperty] private string _alarmRowColorKey = "SuccessBrush";
    [ObservableProperty] private bool _hasActiveAlarm;

    /// <summary>顶栏副标题（连接目标 + 运行模式），随运行模式切换刷新。</summary>
    [ObservableProperty] private string _stationSubtitle = string.Empty;

    /// <summary>是否处于模拟 PLC 模式（顶栏常驻横幅依据）。</summary>
    [ObservableProperty] private bool _isSimulationMode;

    /// <summary>模拟模式横幅文案。</summary>
    [ObservableProperty] private string _simulationModeText = string.Empty;

    [ObservableProperty] private string _userShowText = "操作员（未登录）";
    [ObservableProperty] private string _userRoleText = "操作员";

    /// <summary>是否存在配置降级（横幅是否显示）。</summary>
    [ObservableProperty] private bool _hasConfigIssue;

    /// <summary>配置降级横幅文案。</summary>
    [ObservableProperty] private string _configIssueText = string.Empty;

    /// <summary>横幅配色资源键：数据不可用=红，已恢复=橙。</summary>
    [ObservableProperty] private string _configIssueColorKey = "DangerBrush";

    /// <summary>是否存在运行期异常（第二条横幅）。</summary>
    [ObservableProperty] private bool _hasRuntimeError;

    /// <summary>运行期异常横幅文案。</summary>
    [ObservableProperty] private string _runtimeErrorText = string.Empty;

    /// <summary>运行期异常横幅配色资源键：真缺陷=红，PLC 未连接=橙（现场常态，不该喊红）。</summary>
    [ObservableProperty] private string _runtimeErrorColorKey = "DangerBrush";

    /// <summary>运行期异常横幅底色资源键。</summary>
    [ObservableProperty] private string _runtimeErrorBottomColorKey = "DangerSoftBrush";

    public MasterViewModel(
        IServiceProvider serviceCollection,
        IConnectManager Connect,
        IAlarmService alarmList,
        IUserService User,
        IConfigHealth ConfigHealth,
        IRuntimeErrorReceiver RuntimeErrorList,
        IDialogService dialogCollection,
        UiOptions UiOptions,
        PlcConnectionOptions PlcOptions,
        ILogger<MasterViewModel> Log,
        IRunModeManager? RunMode = null)
    {
        _service = serviceCollection;
        _connect = Connect;
        _alarm = alarmList;
        _userItem = User;
        _configHealth = ConfigHealth;
        _runtimeErrorList = RuntimeErrorList;
        _dialog = dialogCollection;
        _plcOptions = PlcOptions;
        _runMode = RunMode;
        _log = Log;

        StationName = string.IsNullOrWhiteSpace(UiOptions.StationName) ? "工控上位机" : UiOptions.StationName;

        BuildNavigation();
        RefreshUserShow();
        RefreshRunMode();

        _connect.StateChanged += (_, StateValue) => UiDispatch.Post(() => ApplyConnectionState(StateValue));

        // 运行模式切换（真实 ↔ 模拟）由系统设置页发起：顶栏副标题与模拟横幅随之刷新
        if (_runMode is not null)
        {
            _runMode.modeChanged += (_, _) => UiDispatch.Post(RefreshRunMode);
        }

        _alarm.alarmTriggered += (_, _) => UiDispatch.Post(RefreshAlarmRow);
        _alarm.alarmCleared += (_, _) => UiDispatch.Post(RefreshAlarmRow);

        // 身份变化 → 重算可见导航并刷新顶栏
        _userItem.userChanged += (_, _) => UiDispatch.Post(() =>
        {
            BuildNavigation();
            RefreshUserShow();
        });

        // 配置健康变化 → 刷新降级横幅（加载器可能在后台线程登记）
        _configHealth.changed += (_, _) => UiDispatch.Post(RefreshConfigHealth);

        // 运行期异常 → 刷新错误横幅（异常可能来自任意线程）
        _runtimeErrorList.changed += (_, _) => UiDispatch.Post(RefreshRuntimeError);

        // 连接状态变化 → 设备类提示（"PLC 未连接，数据未刷新"）该出现/该消失都由它定
        _connect.StateChanged += (_, _) => UiDispatch.Post(RefreshRuntimeError);

        // 可配置弹窗（M 触发线圈 → 主窗居中弹窗，定义见 config/popups.json）
        PopupHost = _service.GetRequiredService<PopupHostViewModel>();

        // 页面号/点码同步（D10 / D651·D652）：PLC 侧据此做程序分支
        _pageContext = _service.GetRequiredService<IPageContextService>();

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) =>
        {
            CurrentTimeText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            // 顺带把当前页需要周期回读的 PLC 状态跟上（动作按钮 / 回原状态灯，见 MainViewModel.Buttons.cs）
            ReadBackCurrentPageStateAsync();
        };
        _clock.Start();

        CurrentTimeText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        ApplyConnectionState(_connect.State);
        RefreshAlarmRow();
        RefreshConfigHealth();
        RefreshRuntimeError();

        if (NavigationEntryCollection.Count > 0)
        {
            SelectedNavigationEntry = NavigationEntryCollection[0];
        }
    }

    /// <summary>站点/项目名称，显示在顶栏。</summary>
    public string StationName { get; }

    /// <summary>侧栏版本号（取程序集版本，避免与 csproj 里写死的版本号各说各话）。</summary>
    public string VersionNumber { get; } = FormatVersionNumber(Assembly.GetExecutingAssembly().GetName().Version);

    private static string FormatVersionNumber(Version? version) =>
        $"v{version?.Major ?? 0}.{version?.Minor ?? 0}.{version?.Build ?? 0} · 点胶锁付机";

    /// <summary>导航项集合。</summary>
    public BatchObservableCollection<NavigationEntry> NavigationEntryCollection { get; } = new();

    partial void OnSelectedNavigationEntryChanged(NavigationEntry? value)
    {
        if (value is null)
        {
            return;
        }

        CurrentPage = value.Page;

        // 页面号同步给 PLC（D0）：主监控 = 1，按侧栏固定顺序递增。
        // 与页面初始化并行发出——页码只是上下文提示，不该等页面加载完才写。
        if (pageNumberMap.TryGetValue(value.Title, out var pageNumber))
        {
            SafeInvokeAsync.FireAndForget(
                () => _pageContext.WritePageNumberAsync(pageNumber),
                _log,
                $"写入页面号（{value.Title} = {pageNumber}）");
        }

        SafeInvokeAsync.FireAndForget(
            () => value.Page.InitializeAsync(),
            _log,
            $"初始化页面「{value.Title}」");
    }

    /// <summary>
    /// 按当前身份重建导航。
    ///
    /// <para>页面 ViewModel 仍然全部构造（都是单例，构造一次即可），只是不显示在导航栏上——
    /// 这样切换身份时不需要重新解析 DI，也不会丢状态。</para>
    /// </summary>
    private void BuildNavigation()
    {
        var previousTitle = SelectedNavigationEntry?.Title;

        NavigationEntry[] All =
        [
            new("运行监控", "Icon.Nav.Run", _service.GetRequiredService<RunViewModel>()),
            new("参数设置", "Icon.Nav.Param", _service.GetRequiredService<ParameterViewModel>()),
            new("锁附示教", "Icon.Nav.Teach", _service.GetRequiredService<ScrewTeachViewModel>()),
            new("点胶示教", "Icon.Nav.Recipe", _service.GetRequiredService<DispenseTeachViewModel>()),
            new("标定 · 其它点", "Icon.Nav.Io", _service.GetRequiredService<CalibrationViewModel>()),
            new("机种管理", "Icon.Nav.Recipe", _service.GetRequiredService<RecipeViewModel>()),
            new("手动调试", "Icon.Nav.Manual", _service.GetRequiredService<ManualViewModel>()),
            new("IO 点位", "Icon.Nav.Io", _service.GetRequiredService<IoViewModel>()),
            new("报警记录", "Icon.Nav.Alarm", _service.GetRequiredService<AlarmViewModel>()),
            new("历史记录", "Icon.Nav.History", _service.GetRequiredService<HistoryRecordViewModel>()),
            new("系统设置", "Icon.Nav.Settings", _service.GetRequiredService<SettingsViewModel>()),
        ];

        NavigationEntryCollection.ReplaceAll(All.Where(Entry =>
            !navigationPermission.TryGetValue(Entry.Title, out var required) || _userItem.Has(required)));

        // 尽量停留在原来的页面；该页已不可见时回到第一页
        SelectedNavigationEntry = NavigationEntryCollection.FirstOrDefault(navigationItem => navigationItem.Title == previousTitle)
                          ?? NavigationEntryCollection.FirstOrDefault();
    }

    // ── 身份与权限 ──────────────────────────────────────────────────────

    /// <summary>弹出登录框切换身份。</summary>
    [RelayCommand]
    private void SwitchUser()
    {
        if (!_dialog.ShowLogin())
        {
            return;
        }

        _log.LogInformation("身份已切换：{User}", _userItem.CurrentDisplayName);

        if (_userItem.IsUseDefaultPassword)
        {
            // 不在提示里复述初始密码本身——密码值不应出现在任何界面上
            _dialog.Hint("安全提示",
                "当前账号仍在使用初始密码。\n\n"
                + "请在「系统设置」页尽快修改，避免无关人员以管理员身份操作系统。");
        }
    }

    /// <summary>
    /// 打开报警记录页。底栏报警条可点击直达——报警出现后操作员的下一跳应该是一键可达的。
    /// </summary>
    [RelayCommand]
    private void OpenAlarm()
    {
        var alarmList = NavigationEntryCollection.FirstOrDefault(navigationItem => navigationItem.Title == "报警记录");

        if (alarmList is not null)
        {
            SelectedNavigationEntry = alarmList;
        }
    }

    /// <summary>退出登录，回落到操作员身份。</summary>
    [RelayCommand]
    private void ExitLoginUser()
    {
        if (!_userItem.IsAlreadyLogin)
        {
            return;
        }

        _userItem.ExitLogin();
        _log.LogInformation("已退出登录，回落到操作员身份");
    }

    private void RefreshUserShow()
    {
        UserRoleText = RolePermission.DisplayName(_userItem.CurrentRole);

        UserShowText = _userItem.IsAlreadyLogin
            ? $"{_userItem.CurrentDisplayName}（{UserRoleText}）"
            : "操作员（未登录）";
    }

    /// <summary>
    /// 刷新顶栏副标题与模拟模式横幅。
    /// <para>模拟模式必须在界面上"一眼可见"——否则操作员会以为设备在跑，
    /// 实际却在跟一台虚拟 PLC 说话。</para>
    /// </summary>
    private void RefreshRunMode()
    {
        var mode = _runMode?.CurrentMode
                   ?? (_plcOptions.UseSimulator ? RunMode.SimulationPlc : RunMode.RealPlc);

        StationSubtitle = $"{_plcOptions.Host}:{_plcOptions.Port} · " +
                     (mode == RunMode.SimulationPlc ? "模拟模式（虚拟 PLC）" : "真实 PLC");

        IsSimulationMode = mode == RunMode.SimulationPlc;

        SimulationModeText = IsSimulationMode
            ? "当前为模拟 PLC 模式：未连接真实设备，仅用于离线调试；下方数据均来自内置虚拟 PLC。"
              + (_runMode is not null && _runMode.SimulationSlaveIsRun
                  ? $"　Modbus 调试从站：{_runMode.SimulationSlaveEndpointText}。"
                  : string.Empty)
            : string.Empty;
    }

    private void ApplyConnectionState(ConnectionState StateValue)
    {
        // 断线后界面不该继续显示旧值；本方法已在 UI 线程（界面派发.投递），事件订阅者也在 UI 线程。
        if (_lastConnectionState is ConnectionState.Connected && StateValue is not ConnectionState.Connected)
        {
            _service.GetRequiredService<TagValueService>().FailEffectAll();
        }

        _lastConnectionState = StateValue;

        (ConnectionStateText, ConnectionStateColorKey) = StateValue switch
        {
            ConnectionState.Connected => ("已连接", "SuccessBrush"),
            ConnectionState.Connecting => ("连接中", "WarningBrush"),
            ConnectionState.Reconnecting => ("重连中", "WarningBrush"),
            ConnectionState.Fault => ("连接故障", "DangerBrush"),
            _ => ("未连接", "MutedBrush"),
        };
    }

    // 三条横幅（配置降级 / 运行期异常 / 活动报警）的刷新见 MainViewModel.Banners.cs
}
