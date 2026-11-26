using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PlcHmi.App.Services;
using PlcHmi.App.Views;
using PlcHmi.Business.Services;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.App.ViewModels;

/// <summary>主运行页 ViewModel。设备状态、节拍、产量与启停控制。</summary>
public sealed partial class RunViewModel : ViewModelBase
{
    private readonly IDeviceService _device;
    private readonly IAlarmService _alarm;
    private readonly IDialogService _dialog;
    private readonly IRunMonitorAddressService _runAddress;
    private readonly IUserService _userItem;
    private readonly IRecipeService _recipe;
    private readonly ILogger<RunViewModel> _log;

    private bool _chartPendingRefresh;  // 4 个事件源同批只算一次
    private readonly DispatcherTimer _chartBatch = new() { Interval = TimeSpan.FromMilliseconds(150) };

    [ObservableProperty] private string _deviceStateText = "未知";
    [ObservableProperty] private string _deviceStateColorKey = "MutedBrush";
    [ObservableProperty] private string _weekPeriodTimeText = "--.---";
    [ObservableProperty] private string _outputCountText = "0";
    [ObservableProperty] private string _targetCountText = "0";
    [ObservableProperty] private string _goodCountText = "0";
    [ObservableProperty] private string _defectiveCountText = "0";
    [ObservableProperty] private string _reachAsText = "0.0%";
    [ObservableProperty] private bool _editableAddress;

    /// <summary>
    /// 是否显示"数据清除"按钮（工程师与管理员）。
    ///
    /// <para>清零累计数据是调试 / 交接班动作，操作员没有理由碰它；
    /// 沿用"改参数值"这一权限点：恰好工程师与管理员具备、操作员不具备，
    /// 不必为单个按钮新增权限位。</para>
    /// </summary>
    [ObservableProperty] private bool _canClearRemoveData;

    // ── 数据分析组合视图（产量进度卡 + 四张图表翻页） ──────────────────
    private readonly IOutputStatistics _statistics;

    [ObservableProperty] private int _currentChartIndex;

    [ObservableProperty] private string _chartTitle = chartTitleList[0];
    [ObservableProperty] private string _pageNumberText = "1 / 4";
    [ObservableProperty] private bool _showRatioChart = true;
    [ObservableProperty] private bool _showHourChart;
    [ObservableProperty] private bool _showWeekChart;
    [ObservableProperty] private bool _showPairCompareChart;

    [ObservableProperty] private double _ratioGoodValue;
    [ObservableProperty] private double _ratioDefectiveValue;
    [ObservableProperty] private int _currentHourIndex;

    [ObservableProperty] private IReadOnlyList<double> _todayHourCollection = AllZeroSequence(24);
    [ObservableProperty] private IReadOnlyList<double> _weeklyOutputCollection = AllZeroSequence(7);
    [ObservableProperty] private IReadOnlyList<double> _pairCompareTodayCollection = AllZeroSequence(24);
    [ObservableProperty] private IReadOnlyList<double> _pairCompareYesterdayDayCollection = AllZeroSequence(24);
    [ObservableProperty] private IReadOnlyList<string> _weekTagCollection = EmptyTag(7);
    [ObservableProperty] private IReadOnlyList<LineSeries> _weeklyOutputSeries = [];
    [ObservableProperty] private IReadOnlyList<LineSeries> _pairCompareSeries = [];

    /// <summary>横轴小时标签（0–23），三张图共用。</summary>
    public IReadOnlyList<string> HourTagCollection { get; } =
        Enumerable.Range(0, 24).Select(hour => $"{hour}").ToList();

    private static readonly string[] chartTitleList =
    [
        "良品 / 不良品占比",
        "今日每小时产量",
        "近 7 天每日产量",
        "今日与昨日同小时对比",
    ];

    /// <summary>达成率（0–100），仅供进度条使用；精确读数看 <see cref="ReachAsText"/>。</summary>
    [ObservableProperty] private double _achievementRate;

    /// <summary>
    /// 当前机种（显示为「机种 03 · 锁附 A」）。
    /// <para>现场最怕"跑错机种"——机种号显示在运行页第一眼能看到的位置。</para>
    /// </summary>
    [ObservableProperty] private string _currentMachineModelText = "机种 --";

    public RunViewModel(
        IDeviceService Device,
        IAlarmService alarmList,
        IDialogService dialogCollection,
        IRunMonitorAddressService RunAddress,
        IUserService UserItem,
        IOutputStatistics outputStatistics,
        IRecipeService recipeItem,
        IScannerService Scanner,
        ILogger<RunViewModel> Log)
    {
        _device = Device;
        _alarm = alarmList;
        _dialog = dialogCollection;
        _runAddress = RunAddress;
        _userItem = UserItem;
        _statistics = outputStatistics;
        _recipe = recipeItem;
        _scanner = Scanner;
        _log = Log;

        _chartBatch.Tick += (_, _) =>
        {
            _chartBatch.Stop();
            _chartPendingRefresh = false;
            RefreshChart();
        };

        Title = "运行监控";
        Subtitle = "设备实时状态与生产节拍";

        _device.StateChanged += (_, StateValue) => AtUiThreadExecute(() => ApplyState(StateValue));
        _device.weekPeriodTimeChanged += (_, seconds) => AtUiThreadExecute(() => ApplyWeekPeriodTime(seconds));
        _device.outputCountChanged += (_, countValue) => AtUiThreadExecute(() =>
        {
            ApplyOutputCount(countValue);
            MarkChartPendingRefresh();
        });
        _device.targetCountChanged += (_, countValue) => AtUiThreadExecute(() => ApplyOutputCount(_device.OutputCount));
        _device.goodCountChanged += (_, countValue) => AtUiThreadExecute(() =>
        {
            ApplyGoodCount(countValue);
            MarkChartPendingRefresh();
        });
        _device.defectiveCountChanged += (_, countValue) => AtUiThreadExecute(() =>
        {
            ApplyDefectiveCount(countValue);
            MarkChartPendingRefresh();
        });
        _alarm.alarmTriggered += (_, AlarmItem) => AtUiThreadExecute(RefreshAlarmSummary);
        _alarm.alarmCleared += (_, _) => AtUiThreadExecute(RefreshAlarmSummary);
        _userItem.userChanged += (_, _) => AtUiThreadExecute(RefreshPermission);
        _statistics.statisticsChanged += (_, _) => AtUiThreadExecute(MarkChartPendingRefresh);

        // 工位状态条（锁附/点胶/加工完成 + 已锁附颗数）见 RunViewModel.Process.cs
        SubscribeProcessState();

        // 当前工单号（扫码结果）见 RunViewModel.Scanner.cs
        SubscribeScanWorkOrder();
        _recipe.recipeChanged += (_, _) => AtUiThreadExecute(RefreshMachineModelText);

        TargetCountText = _device.TargetCount.ToString("N0");
        ApplyState(_device.State);
        RefreshAlarmSummary();
        RefreshPermission();
        RefreshMachineModelText();
        RefreshChart();
    }

    /// <inheritdoc />
    public override Task InitializeAsync()
    {
        TargetCountText = _device.TargetCount.ToString("N0");
        ApplyOutputCount(_device.OutputCount);
        ApplyGoodCount(_device.GoodCount);
        ApplyDefectiveCount(_device.DefectiveCount);

        // 主动拉一次节拍快照。<b>不能只依赖事件</b>：设备服务仅在值发生变化时
        // 才触发 周期时间已变更，若启动时机器正以恒定节拍生产，事件永远不来，
        // 「当前节拍」会一直停在 --.---（审查标准 R2：订阅事件流的服务必须在初始拉一次快照）。
        ApplyWeekPeriodTime(_device.WeekPeriodTimeSeconds);
        ApplyProcessInitialSnapshot();
        ApplyWorkOrder(_scanner.WorkOrder);
        RefreshChart();
        return Task.CompletedTask;
    }

    // ── 数据分析组合视图 ────────────────────────────────────────────────

    /// <summary>上一张图（循环翻页）。</summary>
    [RelayCommand]
    private void OnOneChart() => CurrentChartIndex = (CurrentChartIndex + 3) % 4;

    /// <summary>下一张图（循环翻页）。</summary>
    [RelayCommand]
    private void NextChart() => CurrentChartIndex = (CurrentChartIndex + 1) % 4;

    partial void OnCurrentChartIndexChanged(int value)
    {
        PageNumberText = $"{value + 1} / 4";
        ChartTitle = chartTitleList[value];
        ShowRatioChart = value == 0;
        ShowHourChart = value == 1;
        ShowWeekChart = value == 2;
        ShowPairCompareChart = value == 3;
    }

    /// <summary>4 个事件源合批：脏标记已在则不重复排队，计时器到期只重建一次图表。</summary>
    private void MarkChartPendingRefresh()
    {
        if (_chartPendingRefresh)
        {
            return;
        }

        _chartPendingRefresh = true;
        _chartBatch.Stop();
        _chartBatch.Start();
    }

    /// <summary>
    /// 从统计服务与设备服务拉取最新快照刷新全部图表。
    ///
    /// <para>每次都替换集合实例——图表控件的依赖属性靠引用变化触发重绘。</para>
    /// </summary>
    private void RefreshChart()
    {
        RatioGoodValue = _device.GoodCount;
        RatioDefectiveValue = _device.DefectiveCount;

        var Today = _statistics.TodayHourlyOutput;
        TodayHourCollection = Today;
        PairCompareTodayCollection = Today;   // 与昨日共用同一份今日数据
        PairCompareYesterdayDayCollection = _statistics.YesterdayDayHourlyOutput;
        CurrentHourIndex = DateTime.Now.Hour;

        WeeklyOutputCollection = _statistics.WeekPerDayOutput;
        WeekTagCollection = _statistics.WeekDateTag;

        WeeklyOutputSeries =
        [
            new LineSeries { Name = "每日产量", ColorKey = "PrimaryBrush", NumericValue = WeeklyOutputCollection },
        ];

        PairCompareSeries =
        [
            new LineSeries { Name = "今日", ColorKey = "PrimaryBrush", NumericValue = PairCompareTodayCollection },
            new LineSeries { Name = "昨日", ColorKey = "WarningBrush", NumericValue = PairCompareYesterdayDayCollection },
        ];
    }

    private static double[] AllZeroSequence(int Count) => new double[Count];

    private static string[] EmptyTag(int Count) =>
        Enumerable.Range(0, Count).Select(_ => "--").ToArray();

    // ── 命令 ───────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task StartAsync()
    {
        if (!_dialog.Confirm("启动确认", "确认启动设备？请确认人员已撤离危险区域。"))
        {
            return;
        }

        await ExecuteCommandAsync("启动", () => _device.StartAsync());
    }

    [RelayCommand]
    private async Task StopAsync() => await ExecuteCommandAsync("停止", () => _device.StopAsync());

    [RelayCommand]
    private async Task ResetAsync() => await ExecuteCommandAsync("复位", () => _device.ResetAsync());

    /// <summary>
    /// 报警清除。
    ///
    /// <para>上位机<b>不提供急停按钮</b>——急停必须由物理按钮实现：软件卡死或断线时
    /// 按钮就失效了，不能作为安全回路的一环。现场按下急停后 PLC 会把状态字置为
    /// <c>4</c>，界面上的设备状态会显示为「报警中」。</para>
    /// </summary>
    [RelayCommand]
    private async Task ClearEmptyAlarmAsync()
    {
        var clearRemoveAddress = _runAddress.FindAddress("CmdAlarmClear");

        if (!_dialog.Confirm("报警清除",
                $"确认向 PLC 下发报警清除命令（{clearRemoveAddress}）？",
                Views.ConfirmLevel.Warning, "下发清除",
                "清除后报警锁存位被复位，请先排除报警原因再清除。"))
        {
            return;
        }

        await ExecuteCommandAsync("报警清除", () => _device.ClearEmptyAlarmAsync());
    }

    /// <summary>数据清除（复归型脉冲命令，由 PLC 清零产量等累计数据）。</summary>
    [RelayCommand]
    private async Task ClearRemoveDataAsync()
    {
        var clearRemoveAddress = _runAddress.FindAddress("CmdDataClear");

        if (!_dialog.Confirm("数据清除",
                $"确认向 PLC 下发数据清除命令（{clearRemoveAddress}）？",
                Views.ConfirmLevel.Warning, "下发清除",
                "产量、良品 / 不良品等累计数据将被 PLC 清零，该操作不可撤销。"))
        {
            return;
        }

        await ExecuteCommandAsync("数据清除", () => _device.ClearRemoveDataAsync());
    }

    /// <summary>编辑运行监控页元件地址（仅管理员）：对话框就地修改候选副本，服务层校验并保存。</summary>
    [RelayCommand]
    private async Task EditAddressAsync()
    {
        // 编辑走克隆副本，确认前不影响页面运行；取消则直接丢弃
        var candidateList = _runAddress.ComponentCollection.Select(EntryItem => EntryItem.Clone()).ToList();

        if (!_dialog.EditRunMonitorAddress(candidateList))
        {
            return;
        }

        var Success = await UiCommand.ExecuteAsync(_log, _dialog, "保存运行监控页地址",
            () => _runAddress.SaveAsync(candidateList)).ConfigureAwait(true);

        if (Success)
        {
            _dialog.Hint("保存成功", "运行监控页元件地址已保存并立即生效。");
        }
    }

    private void RefreshPermission()
    {
        EditableAddress = _userItem.Has(Permission.EditBindAddress);
        CanClearRemoveData = _userItem.Has(Permission.EditParameterValueList);
    }

    /// <summary>刷新机种显示文本（切换机种、重命名后由 配方已变更 事件触发）。</summary>
    private void RefreshMachineModelText()
    {
        var Number = _recipe.CurrentRecipeNumber;
        var Name = _recipe.CurrentRecipeName;

        CurrentMachineModelText = string.IsNullOrWhiteSpace(Name)
            ? $"机种 {Number:00}"
            : $"机种 {Number:00} · {Name}";
    }

    private async Task ExecuteCommandAsync(string action, Func<Task> Command)
    {
        IsBusy = true;

        try
        {
            if (await UiCommand.ExecuteAsync(_log, _dialog, action, Command).ConfigureAwait(true))
            {
                _log.LogInformation("已下发{Action}命令", action);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── 状态映射 ───────────────────────────────────────────────────────

    /// <summary>
    /// 把设备状态应用到主监控页 Hero 卡的显示文本与语义色键。
    /// 状态字约定见 <see cref="DeviceService.MapStatusWord"/>：0=空闲中 1=运行中 2=待机中 3=复位中 4=报警中；
    /// 文案与色键的对应关系见 <see cref="DeviceService.StateShow"/>（回归测试钉住）。
    /// </summary>
    private void ApplyState(DeviceState StateValue)
    {
        (DeviceStateText, DeviceStateColorKey) = DeviceService.StateShow(StateValue);
    }

    private void ApplyGoodCount(long countValue) => GoodCountText = countValue.ToString("N0");

    private void ApplyDefectiveCount(long countValue) => DefectiveCountText = countValue.ToString("N0");

    private void ApplyWeekPeriodTime(double seconds)
    {
        WeekPeriodTimeText = seconds <= 0 ? "--.---" : seconds.ToString("0.000");
    }

    private void ApplyOutputCount(long countValue)
    {
        OutputCountText = countValue.ToString("N0");
        TargetCountText = _device.TargetCount.ToString("N0");

        var speedRate = _device.TargetCount > 0 ? (double)countValue / _device.TargetCount * 100 : 0;
        ReachAsText = $"{speedRate:0.0}%";

        // 进度条只认 0–100：产量被人工改到超过目标时，条不该冲出卡片。
        // 文字照实显示真实百分比 —— 数字要诚实，条只负责"进度感"。
        AchievementRate = Math.Clamp(speedRate, 0, 100);
    }

    // 报警展示（横幅 / 胶囊 / 自动消失）见 RunViewModel.Alarm.cs（partial）。
}
