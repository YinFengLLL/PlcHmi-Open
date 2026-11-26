using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PlcHmi.App.Services;
using PlcHmi.App.Views;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Models;

namespace PlcHmi.App.ViewModels;

// 单点 / 分组的行视图模型见 IoRowViewModels.cs。

/// <summary>
/// IO 点位监控页 ViewModel。
///
/// <para><b>页面职责：</b>按八进制分组展示 X 输入与 Y 输出的实时状态；
/// 允许工程师直接编辑每个点的含义并保存回 <c>io-points.json</c>。</para>
///
/// <para>程序本身不需要知道 “X0 是什么”——这是本模板区别于普通上位机的关键设计，
/// 换设备零改代码。</para>
/// </summary>
public sealed partial class IoViewModel : ViewModelBase
{
    /// <summary>每组的点位数（八进制自然分组）。</summary>
    private const int GroupSize = 8;

    private readonly IIoPointService _ioPointTable;
    private readonly IUserService _userItem;
    private readonly IPollingEngine _polling;
    private readonly IDialogService _dialog;
    private readonly IIoDiagnosticService _diagnostic;
    private readonly ILogger<IoViewModel> _log;

    private readonly Dictionary<string, IoPointViewModel> _sequence = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>摘要刷新脏标记：合并同一批状态事件（见 <see cref="RequestRefreshSummary"/>）。</summary>
    private bool _summaryPendingRefresh;

    /// <summary>筛选文本防抖：输入过程中只重启计时器，停手 200ms 才重建分组。</summary>
    private readonly DispatcherTimer _filterDebounce = new() { Interval = TimeSpan.FromMilliseconds(200) };

    [ObservableProperty] private bool _isEditMode;
    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private string _inputSummary = string.Empty;
    [ObservableProperty] private string _outputSummary = string.Empty;
    [ObservableProperty] private bool _showDefined;

    /// <summary>是否允许新增点位区（管理员专属：这会改变采集范围）。</summary>
    [ObservableProperty] private bool _canAddPoint;

    /// <summary>是否显示「IO 通信诊断」入口（与改 IO 配置同权限：诊断结论要用来改配置）。</summary>
    [ObservableProperty] private bool _diagnosable;

    // 文本变化只重启防抖计时器，停手后才重建分组（逐字符全量重建太重）。
    // _序号 始终保持全量点位（实时状态刷新不能丢）；复选框等条件仍即时重建。
    partial void OnFilterTextChanged(string value) => RestartFilterDebounce();

    partial void OnShowDefinedChanged(bool value) => BuildGroup();

    public IoViewModel(
        IIoPointService IoPointTable,
        IUserService UserItem,
        IPollingEngine Polling,
        IDialogService dialogCollection,
        IIoDiagnosticService Diagnostic,
        ILogger<IoViewModel> Log)
    {
        _ioPointTable = IoPointTable;
        _userItem = UserItem;
        _polling = Polling;
        _dialog = dialogCollection;
        _diagnostic = Diagnostic;
        _log = Log;

        _filterDebounce.Tick += (_, _) =>
        {
            _filterDebounce.Stop();
            BuildGroup();
        };

        Title = "IO 点位监控";
        Subtitle = $"机型 {IoPointTable.DataGrid.Element.PlcModel} · X/Y 按八进制编号";

        BuildGroup();

        _ioPointTable.StateChanged += StateChanged;
        _userItem.userChanged += (_, _) => AtUiThreadExecute(RefreshPermission);

        RefreshPermission();
    }

    /// <summary>X 输入分组。</summary>
    public BatchObservableCollection<IoGroupViewModel> InputGroup { get; } = new();

    /// <summary>Y 输出分组。</summary>
    public BatchObservableCollection<IoGroupViewModel> OutputGroup { get; } = new();

    /// <inheritdoc />
    public override Task InitializeAsync()
    {
        RefreshSummary();
        return Task.CompletedTask;
    }

    /// <summary>进入/退出编辑模式时，同步所有点位的编辑状态。</summary>
    partial void OnIsEditModeChanged(bool value)
    {
        foreach (var viewModel in _sequence.Values)
        {
            viewModel.IsEdit = value;
        }
    }

    // ── 命令 ───────────────────────────────────────────────────────────

    [RelayCommand]
    private void SwitchEditMode() => IsEditMode = !IsEditMode;

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            IsBusy = true;
            await _ioPointTable.SaveAsync().ConfigureAwait(false);

            AtUiThreadExecute(() =>
            {
                IsEditMode = false;
                RefreshSummary();
                _dialog.Hint("保存成功", "IO 点位含义已保存到 io-points.json。");
            });
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "保存 IO 点位表失败");
            _dialog.ErrorInfo("保存失败", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CancelEdit()
    {
        // 放弃编辑：从配置文件重新加载，完成后再重建分组。
        // 顺序是硬约束——曾经的"即发即忘重载 + 立即构建分组"在重载未完成时
        // 就用了旧表/旧状态，界面停留在重载前的数据（异步重载 完成时不推事件，
        // 直到下次筛选或进出页才会纠正）。构建分组动的是绑定集合，必须回界面线程。
        IsEditMode = false;

        SafeInvokeAsync.FireAndForget(async () =>
        {
            await _ioPointTable.ReloadAsync().ConfigureAwait(false);
            AtUiThreadExecute(BuildGroup);
        }, _log, "重新加载 IO 点位表");
    }

    [RelayCommand]
    private void ClearEmptyFilter() => FilterText = string.Empty;

    /// <summary>在 X 输入区末尾追加一栏（8 点，八进制自动递增）。</summary>
    [RelayCommand]
    private Task AddInputAreaAsync() => AddNewRowAsync("X");

    /// <summary>在 Y 输出区末尾追加一栏（8 点，八进制自动递增）。</summary>
    [RelayCommand]
    private Task AddOutputAreaAsync() => AddNewRowAsync("Y");

    /// <summary>
    /// 新增一栏 X / Y 点位。
    ///
    /// <para>顺序是硬约束：<b>先改表并落盘，再重建采集计划</b>。
    /// 反过来先重建，新点还没进表，重建等于白做；改了表却不重建，
    /// 新点会出现在界面上但永远读不到状态 —— 那比不显示更糟，
    /// 操作员会以为那个点只是没有信号。</para>
    ///
    /// <para>轮询引擎重建时会重新从 <see cref="IIoPointService.DataGrid"/> 取点位，
    /// 所以运行中新增的点不需要重启程序即可参与采集。</para>
    /// </summary>
    private async Task AddNewRowAsync(string prefix)
    {
        if (!CanAddPoint)
        {
            _dialog.Hint("权限不足", "只有管理员可以新增 IO 点位区。");
            return;
        }

        var areaName = string.Equals(prefix, "X", StringComparison.OrdinalIgnoreCase) ? "X 输入区" : "Y 输出区";
        var preview = _ioPointTable.PreviewNextRow(prefix);
        var perGroupPointCount = _ioPointTable.DataGrid.Element.GroupSize;

        if (!_dialog.Confirm($"新增 {areaName}",
                $"将在 {areaName}末尾追加一栏 {preview}（{perGroupPointCount} 点，编号按八进制自动递增），\n" +
                "写入 io-points.json 并立即重建采集计划。\n\n是否继续？"))
        {
            return;
        }

        try
        {
            IsBusy = true;

            var Success = await UiCommand.ExecuteAsync(_log, _dialog, "新增一栏", async () =>
            {
                await _ioPointTable.AddNewRowAsync(prefix).ConfigureAwait(false);
                await _polling.RebuildPlanListAsync().ConfigureAwait(false);
            }, areaName).ConfigureAwait(true);

            // 只有真的成功才报喜：失败时也弹"新增成功"，比不提示更危险
            if (Success)
            {
                AtUiThreadExecute(() =>
                {
                    BuildGroup();
                    _dialog.Hint("新增成功", $"{areaName} 已新增一栏 {preview}，采集计划已重建。");
                });
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>新增点位区会改变采集范围，与改 PLC 连接同级，只给管理员。</summary>
    private void RefreshPermission()
    {
        CanAddPoint = _userItem.Has(Permission.EditIoPoint);
        Diagnosable = _userItem.Has(Permission.EditIoPoint);
    }

    /// <summary>
    /// IO 通信诊断：X / Y 段 × FC01 / FC02 的原始读数并排，定位"显示与物理对不上"。
    /// <para>基址取 <c>io-points.json</c> 元里配置的值（与解析器同一套 X/Y 基址）。</para>
    /// </summary>
    [RelayCommand]
    private void IoDiagnosticAsync()
    {
        var Element = _ioPointTable.DataGrid.Element;

        IoDiagnosticDialog.Show(_diagnostic, Element.XBaseAddressValue, Element.YBaseAddressValue);
    }

    // ── 构建分组 ───────────────────────────────────────────────────────

    private void RestartFilterDebounce()
    {
        _filterDebounce.Stop();
        _filterDebounce.Start();
    }

    private void BuildGroup()
    {
        _sequence.Clear();

        // 先建全量索引（实时状态刷新与编辑模式同步都依赖它），再按筛选条件生成分组展示
        IndexSide(_ioPointTable.DataGrid.Input);
        IndexSide(_ioPointTable.DataGrid.Output);

        var Query = FilterText.Trim();
        BuildSide(_ioPointTable.DataGrid.Input, InputGroup, "X", Query);
        BuildSide(_ioPointTable.DataGrid.Output, OutputGroup, "Y", Query);

        RefreshSummary();
    }

    /// <summary>把一侧全部点位建入索引（不受筛选影响）。</summary>
    private void IndexSide(IReadOnlyList<IoPoint> PointItemList)
    {
        foreach (var PointItem in PointItemList)
        {
            var viewModel = new IoPointViewModel(PointItem);
            viewModel.IsOpenStart = _ioPointTable.GetState(PointItem.Code).IsOpenStart;
            _sequence[PointItem.Code] = viewModel;
        }
    }

    private void BuildSide(IReadOnlyList<IoPoint> PointItemList, BatchObservableCollection<IoGroupViewModel> target, string prefix, string Query)
    {
        var viewModelItem = PointItemList
            .Select(PointItem => _sequence[PointItem.Code])
            .Where(viewModel => MatchFilter(viewModel, Query))
            .ToList();

        // 按八进制高位分组：X0-X7、X10-X17、X20-X27……
        var groupList = viewModelItem.GroupBy(viewModel => GetGroupKey(viewModel.Code, prefix));

        target.ReplaceAll(groupList.Select(group =>
        {
            var hasIndex = group.ToList();
            var first = hasIndex.First().Code;
            var Last = hasIndex.Last().Code;
            return new IoGroupViewModel($"{first} — {Last}", hasIndex);
        }));
    }

    /// <summary>
    /// 筛选规则：「仅显示已定义」为硬条件；关键字按编号或含义模糊匹配（不区分大小写）。
    /// </summary>
    private bool MatchFilter(IoPointViewModel viewModel, string Query)
    {
        if (ShowDefined && !viewModel.IsDefined)
        {
            return false;
        }

        if (Query.Length == 0)
        {
            return true;
        }

        return viewModel.Code.Contains(Query, StringComparison.OrdinalIgnoreCase)
               || viewModel.Meaning.Contains(Query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 取八进制分组键：由低位数字之外的部分决定。
    /// <para>X0→"0"、X7→"0"、X10→"1"、X17→"1"、X20→"2"。</para>
    /// </summary>
    private static string GetGroupKey(string CodeRawValue, string prefix)
    {
        var numberList = CodeRawValue.Substring(prefix.Length);
        return numberList.Length <= 1 ? "0" : numberList[..^1];
    }

    // ── 状态更新 ───────────────────────────────────────────────────────

    private void StateChanged(object? sender, IoPointState StateValue)
    {
        AtUiThreadExecute(() =>
        {
            if (_sequence.TryGetValue(StateValue.Code, out var viewModel))
            {
                viewModel.IsOpenStart = StateValue.IsOpenStart;
            }

            RequestRefreshSummary();
        });
    }

    /// <summary>
    /// 摘要合并刷新：一个采集周期内可能几十个点同时翻转，
    /// 每点都全量重算摘要就是 O(事件数 × 点数)。
    /// 用脏标记把同一批事件合并成一次重算——入队顺序保证摘要回调
    /// 排在所有已排队的点位更新之后，界面最终状态与逐次刷新一致。
    /// </summary>
    private void RequestRefreshSummary()
    {
        if (_summaryPendingRefresh)
        {
            return;
        }

        _summaryPendingRefresh = true;

        AtUiThreadExecute(() =>
        {
            _summaryPendingRefresh = false;
            RefreshSummary();
        });
    }

    private void RefreshSummary()
    {
        var Input = _ioPointTable.DataGrid.Input;
        var Output = _ioPointTable.DataGrid.Output;

        var inputAsOpen = Input.Count(PointItem => _ioPointTable.GetState(PointItem.Code).IsOpenStart);
        var outputAsOpen = Output.Count(PointItem => _ioPointTable.GetState(PointItem.Code).IsOpenStart);

        InputSummary = $"共 {Input.Count} 点 · {Input.Count(PointItem => PointItem.Enabled)} 点启用 · {inputAsOpen} 点 ON";
        OutputSummary = $"共 {Output.Count} 点 · {Output.Count(PointItem => PointItem.Enabled)} 点启用 · {outputAsOpen} 点 ON";
    }
}
