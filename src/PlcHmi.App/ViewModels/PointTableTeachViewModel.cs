using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PlcHmi.App.Services;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Models;

namespace PlcHmi.App.ViewModels;

/// <summary>
/// 点表示教页 ViewModel 基类。锁附示教与点胶示教共用同一套读写、
/// 点动、回零与坐标示教逻辑，仅点表配置不同（列不同）。
///
/// <para><b>写入时机：</b>「坐标示教」只写当前点，「参数写入」把机种区段 D2500–D3999 整段写 PLC 并存机种；
/// 输入框改动只改内存。</para>
/// </summary>
public abstract partial class PointTableTeachViewModel : ViewModelBase, IActionButtonPage
{
    private readonly IManualWidgetService _manual;
    private readonly IUserService _userItem;
    private readonly ITagValueService _valueList;
    private readonly IDialogService _dialog;
    private readonly IPageContextService _pageContext;
    private readonly ILogger _log;

    [ObservableProperty] private int _selectedIndex;
    [ObservableProperty] private TeachAxisViewModel? _selectedAxis;
    [ObservableProperty] private bool _isManualEnabled;
    [ObservableProperty] private bool _editableAddress;
    [ObservableProperty] private string _indexSummary = string.Empty;

    /// <summary>
    /// 辅助地址区顶部的"当前点"提示（结构体数组模式）：如
    /// <c>当前为第 3 点 · 地址 D3516–D3523（基址 D3500 + 点序号 × 8）</c>。
    /// <para>固定地址页面（如锁附页的拍照点）为空，界面上不占位。</para>
    /// </summary>
    [ObservableProperty] private string _auxAddressSummary = string.Empty;
    [ObservableProperty] private string _currentPositionText = "--.--";

    protected PointTableTeachViewModel(
        string tableKeyValue,
        int pointCodeRegisterValue,
        IPointTableService pointTableServiceItem,
        IPageAddressService pageAddressServiceItem,
        RecipeConfig configPairObject,
        IManualWidgetService Manual,
        IUserService User,
        ITagValueService ValueList,
        IDialogService Dialog,
        IPageContextService PageContext,
        ILogger Log)
    {
        TableKey = tableKeyValue;
        PointCodeRegister = pointCodeRegisterValue;
        PointTableService = pointTableServiceItem;
        PageAddressService = pageAddressServiceItem;
        _manual = Manual;
        _userItem = User;
        _valueList = ValueList;
        _dialog = Dialog;
        _pageContext = PageContext;
        _log = Log;

        // 允许写入的判定读的是本页的"手动使能"（与点动 / 回零同一道门）
        _auxAddressWrite = new AuxAddressRealtimeWriteHandle(Log, Dialog, () => IsManualEnabled);

        TableDefinition = PointTableService.Table(TableKey);
        Group = TableDefinition.JogGroup.Length == 0 ? TableDefinition.Key : TableDefinition.JogGroup;

        for (var Sequence = 0; Sequence < TableDefinition.PointCount; Sequence++)
        {
            RowCollection.Add(new PointValueRowViewModel(TableDefinition, Sequence, PointTableService, SelectedPointAsync));
        }

        // 只显示本页分组的轴：锁附页 = 锁附 X/Y/Z/ZZ；点胶页 = 点胶 X/Y/Z（Y 与锁附 Y 共轴）
        foreach (var Item in configPairObject.Jog.Where(Item => PageGroup.Match(Item.Group, Group)))
        {
            AxisList.Add(new TeachAxisViewModel(Item));
        }

        foreach (var Item in PageAddressService.ActionButton(Group))
        {
            ActionButtonCollection.Add(new ActionButtonViewModel(Item));
        }

        foreach (var Item in PageAddressService.AuxAddress(Group))
        {
            AuxAddressCollection.Add(CreateAuxAddressRow(Item));
        }

        if (AxisList.Count > 0)
        {
            _selectedAxis = AxisList[0];
            _selectedAxis.IsSelected = true;
        }

        PointTableService.pointTableChanged += (_, Key) =>
        {
            if (string.Equals(Key, tableKeyValue, StringComparison.OrdinalIgnoreCase))
            {
                AtUiThreadExecute(RefreshRow);
            }
        };

        _valueList.NewValueChanged += (_, Value) => AtUiThreadExecute(() => TagChanged(Value));
        _manual.enabledChanged += (_, Enabled) => AtUiThreadExecute(() => IsManualEnabled = Enabled);
        _userItem.userChanged += (_, _) => AtUiThreadExecute(RefreshPermission);

        IsManualEnabled = _manual.IsEnabled;
        RefreshPermission();
        RefreshIndexSummary();
        RefreshRow();
    }

    /// <summary>本页对应的点表键。</summary>
    protected string TableKey { get; }

    /// <summary>本页的元件分组（锁附 / 点胶）。</summary>
    protected string Group { get; }

    /// <summary>点表服务。</summary>
    protected IPointTableService PointTableService { get; }

    /// <summary>页面地址服务（地址编辑与辅助地址读写）。</summary>
    protected IPageAddressService PageAddressService { get; }

    /// <summary>点表定义。</summary>
    public PointTableDefinition TableDefinition { get; }

    /// <summary>点表行集合。</summary>
    public ObservableCollection<PointValueRowViewModel> RowCollection { get; } = new();

    /// <summary>点动轴列表（配置驱动，按本页分组过滤）。</summary>
    public ObservableCollection<TeachAxisViewModel> AxisList { get; } = new();

    /// <summary>动作按钮（按本页分组过滤）。</summary>
    public BatchObservableCollection<ActionButtonViewModel> ActionButtonCollection { get; } = new();

    /// <summary>辅助地址（按本页分组过滤，管理员可增删改）。</summary>
    public ObservableCollection<AuxAddressRowViewModel> AuxAddressCollection { get; } = new();

    // ── 初始化 ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    public override async Task InitializeAsync()
    {
        await PointTableService.LoadPointTableAsync(TableKey).ConfigureAwait(false);
        await ReadAuxAddressAsync().ConfigureAwait(false);
        await RefreshThisPageStateAsync().ConfigureAwait(false);   // 状态灯 + 动作按钮（见 *.Lamp.cs）
        await SyncCurrentPointNumberAsync().ConfigureAwait(false);

        AtUiThreadExecute(() =>
        {
            RefreshPermission();
            RefreshRow();
            RefreshAxisPositionSnapshot();
        });
    }

    // ── 序号与选中 ──────────────────────────────────────────────────────

    partial void OnSelectedIndexChanged(int value) => RefreshIndexSummary();

    /// <summary>上一个点。</summary>
    [RelayCommand]
    private void PreviousIndex() => MoveIndex(-1);

    /// <summary>下一个点。</summary>
    [RelayCommand]
    private void NextIndex() => MoveIndex(1);

    private void MoveIndex(int increment)
    {
        var totalNumber = TableDefinition.PointCount;
        var next = (SelectedIndex + increment + totalNumber) % totalNumber;
        SelectedIndex = next;
        RefreshRow();
    }

    /// <summary>点击点表行：把该行设为当前点。</summary>
    private Task SelectedPointAsync(int Sequence)
    {
        SelectedIndex = Sequence;
        RefreshRow();
        return Task.CompletedTask;
    }

    // ── 点动轴 ──────────────────────────────────────────────────────────

    /// <summary>选中要点动的轴。</summary>
    [RelayCommand]
    private void SelectAxis(TeachAxisViewModel? axisItem)
    {
        if (axisItem is not null)
        {
            SelectedAxis = axisItem;
        }
    }

    partial void OnSelectedAxisChanged(TeachAxisViewModel? oldValue, TeachAxisViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
            CurrentPositionText = newValue.PositionText;
        }
    }

    /// <summary>
    /// 手动使能开关（与手动调试页共用同一个服务）。
    /// </summary>
    partial void OnIsManualEnabledChanged(bool value)
    {
        if (_manual.IsEnabled == value)
        {
            return;
        }

        _manual.SetEnabled(value);
        SafeInvokeAsync.FireAndForget(() => _manual.SyncEnabledAsync(), _log, "同步手动使能状态到 PLC");

        // 辅助地址的当前值也归手动使能管：未使能只能看，开了才能改（2026-11-12）
        RefreshAuxAddressEditable();
    }

    // ── JOG ─────────────────────────────────────────────────────────────

    /// <summary>按住方向按钮：启动点动。</summary>
    public void StartJog(int direction)
    {
        if (SelectedAxis is null)
        {
            return;
        }

        SafeInvokeAsync.FireAndForget(
            () => StartJogCoreAsync(SelectedAxis.AxisKey, direction),
            _log,
            $"{SelectedAxis.Name}启动点动");
    }

    /// <summary>松开方向按钮：停止点动（无条件生效）。</summary>
    public void EndJog()
    {
        if (SelectedAxis is null)
        {
            return;
        }

        SafeInvokeAsync.FireAndForget(
            () => _manual.StopJogAxisAsync(SelectedAxis.AxisKey),
            _log,
            $"{SelectedAxis.Name}停止点动");
    }

    /// <summary>按住指定轴的方向按钮：启动点动（统一方向键盘，Tag="轴键,±1"）。</summary>
    public void StartJog(string AxisKey, int direction)
    {
        SafeInvokeAsync.FireAndForget(
            () => StartJogCoreAsync(AxisKey, direction),
            _log,
            $"{AxisKey} 启动点动");
    }

    /// <summary>松开指定轴的方向按钮：停止点动（无条件生效）。</summary>
    public void EndJog(string AxisKey)
    {
        SafeInvokeAsync.FireAndForget(
            () => _manual.StopJogAxisAsync(AxisKey),
            _log,
            $"{AxisKey} 停止点动");
    }

    private async Task StartJogCoreAsync(string AxisKey, int direction)
    {
        if (!IsManualEnabled)
        {
            _dialog.Hint("操作被拒绝", "请先开启手动使能，再执行点动。");
            return;
        }

        await UiCommand.ExecuteAsync(_log, _dialog, $"{AxisKey} 点动", () => _manual.JogAxisAsync(AxisKey, direction));
    }

    /// <summary>回零（二次确认后只置位 M 区线圈）。</summary>
    [RelayCommand]
    private async Task HomeAsync()
    {
        if (SelectedAxis is null)
        {
            return;
        }

        if (!IsManualEnabled)
        {
            _dialog.Hint("操作被拒绝", "请先开启手动使能，再执行回零。");
            return;
        }

        if (!_dialog.Confirm("轴回零",
                $"确认让「{SelectedAxis.Name}」执行回零？\n\n回零过程中该轴将移动，确认现场无人员处于动作范围。",
                Views.ConfirmLevel.Warning, "回零"))
        {
            return;
        }

        IsBusy = true;

        try
        {
            if (await UiCommand
                    .ExecuteAsync(_log, _dialog, "回零", () => _manual.HomeAsync(SelectedAxis.AxisKey))
                    .ConfigureAwait(true))
            {
                _dialog.Hint("已下发", $"「{SelectedAxis.Name}」回零命令已下发，请观察轴动作。");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── 辅助地址：见 PointTableTeachViewModel.Aux.cs（结构体数组按点寻址）──

    /// <summary>
    /// 按当前配置重建动作按钮（改地址后刷新用）。
    /// <para><b>必须重建</b>：管理员可在「编辑地址」里新增 / 删除动作按钮，
    /// 只重建辅助地址会漏掉按钮区的增删，表现为"保存了但按钮没出来"。</para>
    /// </summary>
    protected void RebuildActionButton()
    {
        ActionButtonCollection.ReplaceAll(PageAddressService.ActionButton(Group)
            .Select(Item => new ActionButtonViewModel(Item)));
    }

    // ── 刷新 ────────────────────────────────────────────────────────────

    /// <summary>刷新管理员权限可见性。</summary>
    protected void RefreshPermission() => EditableAddress = _userItem.Has(Permission.EditBindAddress);

    private void RefreshIndexSummary()
    {
        IndexSummary = $"第 {SelectedIndex + 1} 点 ／ 共 {TableDefinition.PointCount} 点";

        // 序号一变就同步点码给 PLC（本页自己的寄存器）：点选 / 上一下一点 / 点击行都在这里汇合
        SafeInvokeAsync.FireAndForget(() => _pageContext.WritePointNumberAsync(PointCodeRegister, SelectedIndex), _log, "写入点码");

        // 结构体数组（点胶单点工艺参数）随点切换：地址换一份、值重新读一遍
        RefreshAuxAddressPointNumber();

        // 指令码下拉跟当前点走（见 *.InstructionCode.cs）
        SyncInstructionCodeDownPull();
    }


    private void RefreshRow()
    {
        var PointList = PointTableService.CurrentPointTable(TableKey);

        for (var Entry = 0; Entry < RowCollection.Count; Entry++)
        {
            var rowItem = RowCollection[Entry];
            var PointEntry = Entry < PointList.Count ? PointList[Entry] : new PointValueRow { Sequence = Entry };

            rowItem.Load(PointEntry);
            rowItem.IsCurrent = rowItem.Sequence == SelectedIndex;
        }

        // 点表数据变了（清空 / 下发 / 载入机种）：下拉框跟着刷新，不停在旧值
        SyncInstructionCodeDownPull();
    }

    private void TagChanged(TagNewValue Value)
    {
        if (!Value.IsValid)
        {
            return;
        }

        var Text = CoordinateAux.Format(Value.scaleAfterNewValue);

        foreach (var axisItem in AxisList.Where(item => item.PositionTag == Value.Name))
        {
            axisItem.PositionText = Text;
        }

        if (SelectedAxis is not null && SelectedAxis.PositionTag == Value.Name)
        {
            CurrentPositionText = Text;
        }
    }
}
