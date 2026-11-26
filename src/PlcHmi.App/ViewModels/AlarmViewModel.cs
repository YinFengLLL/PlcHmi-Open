using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PlcHmi.App.Services;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.App.ViewModels;

/// <summary>报警行的视图模型。</summary>
public sealed partial class AlarmEntryViewModel : ObservableObject
{
    [ObservableProperty] private bool _isAlreadyConfirm;

    public AlarmEntryViewModel(AlarmEntry modelPairObject)
    {
        Model = modelPairObject;
        _isAlreadyConfirm = modelPairObject.IsAlreadyConfirm;
    }

    public AlarmEntry Model { get; }

    public string Code => Model.Code;
    public string Message => Model.Message;
    public string TimeText => Model.Triggered.ToString("HH:mm:ss");
    public string ClearedText => Model.Cleared?.ToString("HH:mm:ss") ?? "--";

    /// <summary>等级中文名。</summary>
    public string LevelText => Model.Level switch
    {
        AlarmLevel.Critical => "紧急",
        AlarmLevel.Warning => "警告",
        _ => "提示",
    };

    /// <summary>等级前景色资源键。</summary>
    public string LevelColorKey => Model.Level switch
    {
        AlarmLevel.Critical => "DangerBrush",
        AlarmLevel.Warning => "WarningBrush",
        _ => "PrimaryLightBrush",
    };

    /// <summary>等级背景色资源键。</summary>
    public string LevelBackgroundKey => Model.Level switch
    {
        AlarmLevel.Critical => "DangerSoftBrush",
        AlarmLevel.Warning => "WarningSoftBrush",
        _ => "PrimarySoftBrush",
    };

    partial void OnIsAlreadyConfirmChanged(bool value) => Model.IsAlreadyConfirm = value;
}

/// <summary>报警管理页 ViewModel。</summary>
public sealed partial class AlarmViewModel : ViewModelBase
{
    private readonly IAlarmService _alarm;
    private readonly IAlarmDefinitionService _definition;
    private readonly IUserService _userItem;
    private readonly IDialogService _dialog;
    private readonly ILogger<AlarmViewModel> _log;

    [ObservableProperty] private int _activeCount;
    [ObservableProperty] private bool _showHistory;
    [ObservableProperty] private bool _editableAlarm;

    public AlarmViewModel(
        IAlarmService alarmList,
        IAlarmDefinitionService definitionList,
        IUserService User,
        IDialogService dialogCollection,
        ILogger<AlarmViewModel> Log)
    {
        _alarm = alarmList;
        _definition = definitionList;
        _userItem = User;
        _dialog = dialogCollection;
        _log = Log;

        Title = "报警记录";
        Subtitle = "活动报警与历史记录 · 报警源为 PLC 的 D550–D554 位与 M500–M600";

        _alarm.alarmTriggered += (_, _) => AtUiThreadExecute(Refresh);
        _alarm.alarmCleared += (_, _) => AtUiThreadExecute(Refresh);
        _definition.definitionChanged += (_, _) => AtUiThreadExecute(RebuildDefinition);
        _userItem.userChanged += (_, _) => AtUiThreadExecute(RefreshPermission);

        Refresh();
        RebuildDefinition();
        RefreshPermission();
    }

    /// <summary>报警定义（PLC 地址 → 报警文本的映射）。</summary>
    public BatchObservableCollection<AlarmDefinition> DefinitionCollection { get; } = new();

    /// <summary>已启用的报警条数。</summary>
    public string DefinitionSummary =>
        $"共 {DefinitionCollection.Count} 条，已启用 {DefinitionCollection.Count(EntryItem => EntryItem.Enabled)} 条（上限 100 条）";

    /// <summary>活动报警。</summary>
    /// <remarks>
    /// 用 <see cref="批量可观察集合{T}"/>：报警事件会整表重建，
    /// 逐条 Add 在报警风暴时会放大成每秒数百次布局。
    /// </remarks>
    public BatchObservableCollection<AlarmEntryViewModel> ActiveAlarm { get; } = new();

    /// <summary>历史报警（报警风暴时同样整表重建，见 <see cref="Refresh"/>）。</summary>
    public BatchObservableCollection<AlarmEntryViewModel> History { get; } = new();

    /// <inheritdoc />
    public override Task InitializeAsync()
    {
        Refresh();
        RebuildDefinition();
        RefreshPermission();
        return Task.CompletedTask;
    }

    // ── 报警定义管理（仅管理员） ────────────────────────────────────────

    /// <summary>新增一条报警定义。</summary>
    [RelayCommand]
    private async Task AddAlarmAsync()
    {
        if (!EditableAlarm)
        {
            _dialog.Hint("权限不足", "只有管理员可以修改报警配置。");
            return;
        }

        var AlarmItem = new AlarmDefinition
        {
            Id = _definition.NewId(),
            Level = AlarmLevel.Warning,
            Message = "新报警",
            Address = "M500",
            Bit = -1,
            Enabled = true,
        };

        if (!_dialog.EditAlarmDefinition(AlarmItem, isNewBuild: true))
        {
            return;
        }

        var validateItem = _definition.Validate(AlarmItem);

        if (!validateItem.IsValid)
        {
            _dialog.ErrorInfo("校验失败", validateItem.Summary);
            return;
        }

        _definition.Add(AlarmItem);
        await SaveDefinitionAsync($"已新增报警「{AlarmItem.Id}」。").ConfigureAwait(false);
    }

    /// <summary>编辑一条报警定义（改文本、改地址、改等级；编号是主键，编辑时锁定）。</summary>
    [RelayCommand]
    private async Task EditAlarmAsync(AlarmDefinition? AlarmItem)
    {
        if (AlarmItem is null)
        {
            return;
        }

        if (!EditableAlarm)
        {
            _dialog.Hint("权限不足", "只有管理员可以修改报警配置。");
            return;
        }

        var alreadyEdit = AlarmItem.Clone();

        if (!_dialog.EditAlarmDefinition(alreadyEdit, isNewBuild: false))
        {
            return;
        }

        // isUpdate: true —— 编辑的是克隆副本，同 Id 的条目就是自身，不能报「已被占用」。
        var validateItem = _definition.Validate(alreadyEdit, isUpdate: true);

        if (!validateItem.IsValid)
        {
            _dialog.ErrorInfo("校验失败", validateItem.Summary);
            return;
        }

        _definition.Update(alreadyEdit);
        await SaveDefinitionAsync($"报警「{alreadyEdit.Id}」已更新。").ConfigureAwait(false);
    }

    /// <summary>删除一条报警定义。</summary>
    [RelayCommand]
    private async Task RemoveAlarmAsync(AlarmDefinition? AlarmItem)
    {
        if (AlarmItem is null)
        {
            return;
        }

        if (!EditableAlarm)
        {
            _dialog.Hint("权限不足", "只有管理员可以修改报警配置。");
            return;
        }

        if (!_dialog.Confirm("删除报警",
                $"确认删除报警「{AlarmItem.Id} {AlarmItem.Message}」？\n\n此操作只影响上位机的报警配置，不会改动 PLC。"))
        {
            return;
        }

        _definition.Remove(AlarmItem.Id);
        await SaveDefinitionAsync($"报警「{AlarmItem.Id}」已删除。").ConfigureAwait(false);
    }

    /// <summary>启用 / 停用一条报警。</summary>
    [RelayCommand]
    private async Task SwitchAlarmAsync(AlarmDefinition? AlarmItem)
    {
        if (AlarmItem is null || !EditableAlarm)
        {
            return;
        }

        var alreadyEdit = AlarmItem.Clone();
        alreadyEdit.Enabled = !alreadyEdit.Enabled;

        _definition.Update(alreadyEdit);
        await SaveDefinitionAsync(null).ConfigureAwait(false);
    }

    private async Task SaveDefinitionAsync(string? successMessage)
    {
        var Success = await UiCommand
            .ExecuteAsync(_log, _dialog, "保存", () => _definition.SaveAsync(), "报警配置")
            .ConfigureAwait(true);

        if (Success && successMessage is not null)
        {
            _dialog.Hint("已保存", successMessage);
        }
    }

    private void RebuildDefinition()
    {
        // 整表替换只发一次 Reset 通知（逐条 Add 会触发 N 次布局）
        DefinitionCollection.ReplaceAll(_definition.DefinitionCollection);

        OnPropertyChanged(nameof(DefinitionSummary));
    }

    private void RefreshPermission() => EditableAlarm = _userItem.Has(Permission.EditBindAddress);

    // ── 命令 ───────────────────────────────────────────────────────────

    [RelayCommand]
    private void ShowActive() => ShowHistory = false;

    [RelayCommand]
    private void ShowHistoryList() => ShowHistory = true;

    [RelayCommand]
    private void Confirm(AlarmEntryViewModel? Entry)
    {
        if (Entry is null)
        {
            return;
        }

        _alarm.Confirm(Entry.Code);
        Entry.IsAlreadyConfirm = true;
    }

    [RelayCommand]
    private void ConfirmAll()
    {
        // 批量操作也确认：误触会把未处理的报警一并标成已处理
        if (!_dialog.Confirm("全部确认",
                $"确认把 {ActiveAlarm.Count} 条活动报警全部标记为已确认？",
                Views.ConfirmLevel.Neutral, "全部确认"))
        {
            return;
        }

        _alarm.ConfirmAll();

        foreach (var Entry in ActiveAlarm)
        {
            Entry.IsAlreadyConfirm = true;
        }

        _dialog.Hint("确认完成", "已确认全部活动报警。");
    }

    [RelayCommand]
    private async Task ResetAsync()
    {
        if (!_dialog.Confirm("报警复位",
                "确认向 PLC 下发报警复位命令？",
                Views.ConfirmLevel.Warning, "下发复位",
                "复位会清除 PLC 内的报警锁存位，设备可能在报警原因未排除的情况下恢复动作。"))
        {
            return;
        }

        await UiCommand
            .ExecuteAsync(_log, _dialog, "复位", () => _alarm.ResetAsync(), "报警复位")
            .ConfigureAwait(true);
    }

    [RelayCommand]
    private void ClearEmptyHistory()
    {
        if (!_dialog.Confirm("清除历史",
                "确认清除全部历史报警记录？",
                Views.ConfirmLevel.Danger, "清除",
                "历史记录将永久删除，不可撤销；追溯故障时将无据可查。"))
        {
            return;
        }

        _alarm.ClearEmptyHistory();

        // 页签隐藏时 刷新() 不会重建历史表（按需装载），这里直接清空界面
        History.Clear();
        _historyLoaded = true;

        Refresh();
    }

    private void Refresh()
    {
        var Active = _alarm.ActiveAlarm;

        var activeRow = new List<AlarmEntryViewModel>(Active.Count);
        foreach (var Entry in Active)
        {
            activeRow.Add(new AlarmEntryViewModel(Entry));
        }

        ActiveAlarm.ReplaceAll(activeRow);
        ActiveCount = Active.Count;

        // 历史表：仅在用户正在看历史页签（或首次装载）时才重建。
        // 曾经每次触发/清除事件都为全部历史行重新分配行 VM——历史上千条时
        // = O(N) 分配/次，报警风暴时放大成可感知的卡顿。
        if (!ShowHistory && _historyLoaded)
        {
            return;
        }

        InstallLoadHistory();
    }

    /// <summary>历史表是否已按当前数据装载过（页签隐藏时跳过事件驱动的重建）。</summary>
    private bool _historyLoaded;

    /// <summary>重建历史表行。</summary>
    private void InstallLoadHistory()
    {
        var historyList = _alarm.History;
        var historyRow = new List<AlarmEntryViewModel>(historyList.Count);
        foreach (var Entry in historyList)
        {
            historyRow.Add(new AlarmEntryViewModel(Entry));
        }

        History.ReplaceAll(historyRow);
        _historyLoaded = true;
    }

    /// <summary>切到历史页签时补一次装载（隐藏期间的变更在这里一次性呈现）。</summary>
    partial void OnShowHistoryChanged(bool value)
    {
        if (value)
        {
            InstallLoadHistory();
        }
    }
}
