using System.Windows.Threading;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PlcHmi.App.Services;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.App.ViewModels;

/// <summary>历史记录单元格：显示文本 + 该列颜色键（做成小对象，避免 XAML 逐格反查配置）。</summary>
public sealed class HistoryRecordCellViewModel
{
    public HistoryRecordCellViewModel(string textValue, string colorKeyValue)
    {
        Text = textValue;
        ColorKey = colorKeyValue;
    }

    /// <summary>单元格文本（已应用小数位或 ASCII 转换）。</summary>
    public string Text { get; }

    /// <summary>文字画刷资源键（主题语义色；"无"为正文色）。</summary>
    public string ColorKey { get; }
}

/// <summary>历史记录表的一行：列配置驱动，显示值装载时算一次（避免滚动时逐格重算）。</summary>
public sealed class HistoryRecordRowViewModel
{
    public HistoryRecordRowViewModel(
        HistoryRecordEntry Entry, IReadOnlyList<HistoryDataItem> DataItemCollection, int Sequence, WordOrder wordOrder)
    {
        ArgumentNullException.ThrowIfNull(Entry);
        ArgumentNullException.ThrowIfNull(DataItemCollection);

        Model = Entry;
        IndexText = Sequence.ToString(CultureInfo.InvariantCulture);

        var showValue = new List<HistoryRecordCellViewModel>(DataItemCollection.Count);

        for (var columnIndex = 0; columnIndex < DataItemCollection.Count; columnIndex++)
        {
            var dataItem = DataItemCollection[columnIndex];

            // 记录条数少于当前配置（列被增删过）时留占位符，不补 0——
            // 补 0 会让"没采到"看起来像"真的是 0"
            showValue.Add(columnIndex < Entry.NumericValueCollection.Count
                ? new HistoryRecordCellViewModel(dataItem.ShowText(Entry.NumericValueCollection[columnIndex], wordOrder), dataItem.ColorKey)
                : new HistoryRecordCellViewModel("--", "TextDisabledBrush"));
        }

        ShowValueCollection = showValue;
    }

    /// <summary>原始记录。</summary>
    public HistoryRecordEntry Model { get; }

    /// <summary>该日第几条（1 起，按时间顺序）。</summary>
    public string IndexText { get; }

    /// <summary>工单号：写下记录时的上次扫码结果（不落盘，重载后为空列）。</summary>
    public string WorkOrderText => Model.WorkOrder;

    /// <summary>触发时刻（只显示时间，日期由页面标题承载）。</summary>
    public string TimeText => Model.Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>与当前列一一对应的显示值（含该列颜色）。</summary>
    public IReadOnlyList<HistoryRecordCellViewModel> ShowValueCollection { get; }
}

/// <summary>历史记录页 ViewModel。</summary>
public sealed partial class HistoryRecordViewModel : ViewModelBase
{
    private static readonly string[] starPeriodName = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];

    /// <summary>单列宽度（与 XAML 中的列宽保持一致，用于计算横向滚动的最小宽度）。</summary>
    private const double columnWidth = 118;

    /// <summary>工单号列宽（与 XAML 保持一致）。</summary>
    private const double workOrderColumnWidth = 140;

    /// <summary>固定列（工单号 + 序号 + 时间）占用的宽度。</summary>
    private const double fixedColumnWidth = workOrderColumnWidth + 56 + 96;

    private readonly IHistoryRecordService _history;
    private readonly IUserService _userItem;
    private readonly IDialogService _dialog;
    private readonly IFileDialogService _fileDialog;
    private readonly ILogger<HistoryRecordViewModel> _log;

    private readonly Queue<HistoryRecordEntry> _pendingAppend = new();  // 200ms 合批，避免逐条 Insert
    private readonly DispatcherTimer _appendBatch = new() { Interval = TimeSpan.FromMilliseconds(200) };

    [ObservableProperty] private DateTime _currentDate = DateTime.Today;
    [ObservableProperty] private string _dateText = string.Empty;
    [ObservableProperty] private string _dataBodyName = string.Empty;
    [ObservableProperty] private string _samplingStateText = string.Empty;
    [ObservableProperty] private string _samplingStateColorKey = "MutedBrush";
    [ObservableProperty] private double _tableWidth = 720;
    [ObservableProperty] private int _recordCount;
    [ObservableProperty] private bool _editableHistory;

    public HistoryRecordViewModel(
        IHistoryRecordService historyRecord,
        IUserService User,
        IDialogService dialogCollection,
        IFileDialogService FileDialog,
        ILogger<HistoryRecordViewModel> Log)
    {
        _history = historyRecord;
        _userItem = User;
        _dialog = dialogCollection;
        _fileDialog = FileDialog;
        _log = Log;

        Title = "历史记录";
        Subtitle = "按触发位采集的数据体，可按天翻页查看、导出 CSV";

        _appendBatch.Tick += (_, _) => { _appendBatch.Stop(); FlushPendingAppend(); };

        _history.configChanged += (_, _) => AtUiThreadExecute(() =>
        {
            RebuildColumn();
            // 列变了，已显示的行必须按新列重建（否则表头与数据错位）
            SafeInvokeAsync.FireAndForget(() => LoadDayAsync(CurrentDate), _log, "刷新历史记录");
        });

        _history.recordAlreadyAdd += (_, Entry) => AtUiThreadExecute(() => AppendRow(Entry));

        _userItem.userChanged += (_, _) => AtUiThreadExecute(RefreshPermission);

        RebuildColumn();
        RefreshPermission();
    }

    /// <summary>表格列（配置里的数据项，按显示顺序）。</summary>
    public BatchObservableCollection<HistoryDataItem> ColumnCollection { get; } = new();

    /// <summary>当前日期的记录（最新的在最上面，新记录不必滚动就能看见）。</summary>
    /// <remarks>
    /// 用 <see cref="批量可观察集合{T}"/>：翻页装载是"整表替换"，
    /// 逐条 Add 会对每一行各发一次通知，几千行就是几千次布局。
    /// </remarks>
    public BatchObservableCollection<HistoryRecordRowViewModel> RecordCollection { get; } = new();

    /// <summary>当前日期的记录条数文本。</summary>
    public string RecordCountText => $"共 {RecordCount} 条";

    /// <inheritdoc />
    public override Task InitializeAsync()
    {
        RebuildColumn();
        RefreshPermission();
        return LoadDayAsync(CurrentDate);
    }

    partial void OnRecordCountChanged(int value) => OnPropertyChanged(nameof(RecordCountText));

    // ── 翻页 ────────────────────────────────────────────────────────────

    /// <summary>前一天（跳到最近有记录的前一天，停机几天后翻页不必连点十几下）。</summary>
    [RelayCommand]
    private Task BeforeOneDaysAsync() => FlipPageAsync(-1);

    /// <summary>后一天（同上，跳到最近有记录的后一天）。</summary>
    [RelayCommand]
    private Task AfterOneDaysAsync() => FlipPageAsync(1);

    /// <summary>回到今天。</summary>
    [RelayCommand]
    private Task BackToTodayAsync() => LoadDayAsync(DateTime.Today);

    private async Task FlipPageAsync(int direction)
    {
        var dateList = _history.HasRecordDateCollection();

        // 【2026-11-12 修复】这里原来写的是 Where(...).MaxBy/MinBy，
        // 而 MaxBy/MinBy 在**空序列 + 不可空值类型**（DateTime）上不返回 null，
        // 而是抛 InvalidOperationException("Sequence contains no elements")。
        // 于是"前一天没有数据"时友好提示永远走不到，异常直接冒到全局处理器，
        // 顶栏挂红条「程序异常（已记录日志，可继续操作）：InvalidOperationException…」。
        // 现在先筛成列表再判空，空列表就是"没有更多记录"。
        var candidateList = dateList
            .Where(dateValue => direction < 0 ? dateValue < CurrentDate : dateValue > CurrentDate)
            .ToList();

        if (candidateList.Count == 0)
        {
            _dialog.Hint("没有更多记录",
                direction < 0
                    ? $"没有早于 {CurrentDate:yyyy-MM-dd} 的历史记录。"
                    : $"没有晚于 {CurrentDate:yyyy-MM-dd} 的历史记录。");
            return;
        }

        var candidate = direction < 0 ? candidateList.Max() : candidateList.Min();

        await LoadDayAsync(candidate).ConfigureAwait(true);
    }

    // ── 导出 / 清除 / 配置 ──────────────────────────────────────────────

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        var defaultName = $"历史记录-{CurrentDate:yyyy-MM-dd}.csv";

        var filePath = _fileDialog.SelectSaveFile("导出历史记录", FileDialogService.csvFilter, defaultName);

        if (filePath is null)
        {
            return;
        }

        var Success = await UiCommand
            .ExecuteAsync(_log, _dialog, "导出", () => _history.ExportCsvToFileAsync(filePath, CurrentDate), defaultName)
            .ConfigureAwait(true);

        if (Success)
        {
            _dialog.Hint("导出完成", $"{CurrentDate:yyyy-MM-dd} 的 {RecordCount} 条记录已导出：\n{filePath}");
        }
    }

    [RelayCommand]
    private async Task ClearRemoveHistoryAsync()
    {
        if (!EditableHistory)
        {
            _dialog.Hint("权限不足", "只有管理员可以清除历史记录。");
            return;
        }

        var daysNumber = _history.HasRecordDateCollection().Count;

        if (!_dialog.Confirm("清除历史数据",
                $"确认清除全部历史记录？共 {daysNumber} 天的数据。",
                Views.ConfirmLevel.Danger, "清除",
                "历史记录将永久删除，不可撤销；追溯设备动作与工艺参数时将无据可查。"))
        {
            return;
        }

        var Success = await UiCommand
            .ExecuteAsync(_log, _dialog, "清除", () => _history.ClearRemoveAllAsync(), "全部历史记录")
            .ConfigureAwait(true);

        if (!Success)
        {
            return;
        }

        await LoadDayAsync(DateTime.Today).ConfigureAwait(true);
        _dialog.Hint("已清除", "历史记录已全部清除。");
    }

    [RelayCommand]
    private async Task EditConfigAsync()
    {
        if (!EditableHistory)
        {
            _dialog.Hint("权限不足", "只有管理员可以修改历史记录配置。");
            return;
        }

        // 编辑的是克隆副本：取消时运行中的配置必须原封不动
        var copy = _history.Config.Clone();

        if (!_dialog.EditHistoryRecordConfig(copy))
        {
            return;
        }

        var validateItem = _history.Validate(copy);

        if (!validateItem.IsValid)
        {
            _dialog.ErrorInfo("校验失败", validateItem.Summary);
            return;
        }

        var Success = await UiCommand
            .ExecuteAsync(_log, _dialog, "保存配置", () => _history.SaveConfigAsync(copy), "历史记录配置")
            .ConfigureAwait(true);

        if (!Success)
        {
            return;
        }

        RebuildColumn();
        await LoadDayAsync(CurrentDate).ConfigureAwait(true);

        _dialog.Hint("已保存",
            $"历史记录配置已保存：触发位 {copy.TriggerAddress}，{copy.DataItemCollection.Count} 个数据项。");
    }

    // ── 数据加载与刷新 ──────────────────────────────────────────────────

    private Task LoadDayAsync(DateTime Date) =>
        UiCommand.ExecuteAsync(_log, _dialog, "加载历史记录", async () =>
        {
            CurrentDate = Date.Date;

            var entryList = await _history.ReadDayAsync(CurrentDate).ConfigureAwait(true);

            _pendingAppend.Clear();
            _appendBatch.Stop();
            RebuildRow(entryList);
        }, Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    private void RebuildColumn()
    {
        var Config = _history.Config;

        ColumnCollection.ReplaceAll(Config.DataItemCollection);

        // 表格横向最小宽度：列多时出现横向滚动条，而不是把每列挤成看不清
        TableWidth = Math.Max(720, fixedColumnWidth + ColumnCollection.Count * columnWidth);

        DataBodyName = Config.Title;
        Subtitle = $"{Config.Title} · 按天翻页查看，可导出 CSV";

        RefreshSamplingState();
        RefreshDateText();
    }

    /// <summary>按当前列重建全部行（最新的在最上面）。</summary>
    private void RebuildRow(IReadOnlyList<HistoryRecordEntry> entryList)
    {
        var Config = _history.Config;
        var rowList = new List<HistoryRecordRowViewModel>(entryList.Count);

        for (var Sequence = entryList.Count - 1; Sequence >= 0; Sequence--)
        {
            rowList.Add(new HistoryRecordRowViewModel(
                entryList[Sequence], Config.DataItemCollection, Sequence + 1, Config.WordOrder));
        }

        // 整表替换只发一次 Reset 通知（逐条 Add 会触发 N 次布局）
        RecordCollection.ReplaceAll(rowList);

        RecordCount = entryList.Count;
        RefreshDateText();
    }

    /// <summary>采集线程记下一条：只入队，200ms 内多条合并成一次整表替换，最新的仍排在最上面。</summary>
    private void AppendRow(HistoryRecordEntry Entry)
    {
        if (Entry.Timestamp.Date != CurrentDate) return;   // 翻看别的日期：不打扰当前视图
        _pendingAppend.Enqueue(Entry);
        _appendBatch.Stop();
        _appendBatch.Start();
    }

    private void FlushPendingAppend()
    {
        var Config = _history.Config;
        var All = RecordCollection.ToList();   // 替换全部() 会清空自身，先快照旧行；Insert(0) 保持最新在最上
        while (_pendingAppend.Count > 0)
        {
            RecordCount++;
            All.Insert(0, new HistoryRecordRowViewModel(
                _pendingAppend.Dequeue(), Config.DataItemCollection, RecordCount, Config.WordOrder));
        }
        RecordCollection.ReplaceAll(All);
    }

    private void RefreshDateText()
    {
        var TodayDate = DateTime.Today;
        var suffix = CurrentDate == TodayDate
            ? "（今天）"
            : CurrentDate == TodayDate.AddDays(-1) ? "（昨天）" : string.Empty;

        DateText = $"{CurrentDate:yyyy-MM-dd} {starPeriodName[(int)CurrentDate.DayOfWeek]}{suffix}";
    }

    private void RefreshSamplingState()
    {
        var Config = _history.Config;

        var handshakeDescription = Config.ReadAfterResetTriggerBit ? "读取后自动复位触发位" : "触发位由 PLC 复位";

        SamplingStateText = Config.Enabled
            ? $"采集中：触发位 {Config.TriggerAddress}（上升沿记一条 · {handshakeDescription}）· {Config.DataItemCollection.Count} 个数据项"
            : $"已停用采集：触发位 {Config.TriggerAddress} · {Config.DataItemCollection.Count} 个数据项";

        SamplingStateColorKey = Config.Enabled ? "SuccessBrush" : "MutedBrush";
    }

    private void RefreshPermission() => EditableHistory = _userItem.Has(Permission.EditHistoryRecord);
}
