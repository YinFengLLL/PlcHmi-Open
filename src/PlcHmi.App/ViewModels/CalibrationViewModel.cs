using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PlcHmi.App.Services;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Models;

namespace PlcHmi.App.ViewModels;

/// <summary>其它点列表里的一行（一个辅助点位）。</summary>
public sealed partial class AuxPointRowViewModel : ObservableObject
{
    private readonly IAuxPointService _auxPoint;
    private readonly Func<int, Task>? _selectedBackAdjust;

    [ObservableProperty] private string _xText = "--";
    [ObservableProperty] private string _yText = "--";
    [ObservableProperty] private string _zText = "--";
    [ObservableProperty] private bool _isCurrent;

    public AuxPointRowViewModel(AuxPointDefinition DefinitionItem, int indexValue, IAuxPointService AuxPoint, Func<int, Task>? SelectedBackAdjust = null)
    {
        Definition = DefinitionItem;
        Sequence = indexValue;
        _auxPoint = AuxPoint;
        _selectedBackAdjust = SelectedBackAdjust;
    }

    /// <summary>
    /// 点码（<b>0 基</b>）：在其它点定义里的固定次序，选中后写 PLC 的 D653。
    /// <para>按名字在<b>全量列表</b>里定位得出，不随查询过滤而变——否则同一个点在不同筛选下
    /// 会报出两个点码，PLC 侧拿到的一会儿是这个点、一会儿是那个点。</para>
    /// </summary>
    public int Sequence { get; }

    /// <summary>界面显示的点号（1 基，两位）。</summary>
    public string Tag => (Sequence + 1).ToString("00");

    /// <summary>底层定义。</summary>
    public AuxPointDefinition Definition { get; }

    /// <summary>名称。</summary>
    public string Name => Definition.Name;

    /// <summary>地址文本（只读展示）。</summary>
    public string AddressText => $"D{Definition.XBaseAddress} / D{Definition.YBaseAddress} / D{Definition.ZBaseAddress}";

    /// <summary>从 PLC 读取坐标。</summary>
    public async Task ReadAsync()
    {
        var (X, Y, Z) = await _auxPoint.ReadCoordinateAsync(Definition).ConfigureAwait(false);
        XText = CoordinateAux.Format(X);
        YText = CoordinateAux.Format(Y);
        ZText = CoordinateAux.Format(Z);
    }

    /// <summary>把坐标写入 PLC。</summary>
    public async Task WriteAsync()
    {
        if (!CoordinateAux.TryParse(XText, out var X)
            || !CoordinateAux.TryParse(YText, out var Y)
            || !CoordinateAux.TryParse(ZText, out var Z))
        {
            throw new InvalidOperationException($"{Name} 的坐标不是有效数字。");
        }

        await _auxPoint.WriteCoordinateAsync(Definition, X, Y, Z).ConfigureAwait(false);
    }

    /// <summary>点击该行空白处：选中这个点（并把点码写进 D653）。</summary>
    [RelayCommand]
    private Task Selected() =>
        IsCurrent || _selectedBackAdjust is null ? Task.CompletedTask : _selectedBackAdjust(Sequence);
}

/// <summary>
/// 标定 / 其它点页 ViewModel（上下一页，2026-10-15 起不再分 Tab）。
///
/// <para><b>上半：标定示教</b>——标定中心坐标 X/Y/Z、每点偏移量、标定启动，
/// 锁附 X/Y/Z 轴实时位置，以及控制这三个轴的手动按钮（X±/Y±/Z±）。</para>
///
/// <para><b>下半：其它点坐标</b>——辅助点位（滑螺丝点、相机中心…）的增删改查与 CSV 导入导出，
/// 点行可选中并把点码写进 PLC 的 D653，见 <c>CalibrationViewModel.AuxPoints.cs</c>。</para>
/// </summary>
public sealed partial class CalibrationViewModel : ViewModelBase, IActionButtonPage
{
    private readonly ICalibrationService _calibration;
    private readonly IAuxPointService _auxPoint;
    private readonly IPageAddressService _pageAddress;
    private readonly IManualWidgetService _manual;
    private readonly IUserService _userItem;
    private readonly IDialogService _dialog;
    private readonly IFileDialogService _fileDialog;
    private readonly IPageContextService _pageContext;
    private readonly RecipeConfig _config;
    private readonly ITagValueService _valueList;
    private readonly ILogger<CalibrationViewModel> _log;

    /// <summary>辅助地址「实时读写」的统一实现（2026-11-12）：填完即写 + 周期回读 + 手动使能拦截。</summary>
    private readonly AuxAddressRealtimeWriteHandle _auxAddressWrite;

    // ── 上相机标定 ──────────────────────────────────────────────────────
    [ObservableProperty] private string _centerX = "0.00";
    [ObservableProperty] private string _centerY = "0.00";
    [ObservableProperty] private string _centerZ = "0.00";
    [ObservableProperty] private string _offsetValue = "0.00";

    // ── 下相机标定（2026-10-17 新增；**没有 Y**，只有 X / Z）─────────────
    [ObservableProperty] private string _bottomCameraX = "0.00";
    [ObservableProperty] private string _bottomCameraZ = "0.00";
    [ObservableProperty] private string _bottomCameraOffset = "0.00";
    [ObservableProperty] private string _queryKeyword = string.Empty;
    [ObservableProperty] private bool _editableAddress;
    [ObservableProperty] private bool _isManualEnabled;

    public CalibrationViewModel(
        ICalibrationService Calibration,
        IAuxPointService AuxPoint,
        IPageAddressService PageAddress,
        IManualWidgetService Manual,
        IUserService User,
        IDialogService Dialog,
        IFileDialogService FileDialog,
        IPageContextService PageContext,
        RecipeConfig configPairObject,
        ITagValueService ValueList,
        ILogger<CalibrationViewModel> Log)
    {
        _calibration = Calibration;
        _auxPoint = AuxPoint;
        _pageAddress = PageAddress;
        _manual = Manual;
        _userItem = User;
        _dialog = Dialog;
        _fileDialog = FileDialog;
        _pageContext = PageContext;
        _config = configPairObject;
        _valueList = ValueList;
        _log = Log;

        // 辅助地址实时读写（2026-11-12）：写入门槛 = 本页手动使能
        _auxAddressWrite = new AuxAddressRealtimeWriteHandle(Log, Dialog, () => IsManualEnabled);

        Title = "标定 / 其它点";
        Subtitle = "标定示教 + 其它点坐标（同页操作 · 选中其它点即写 PLC 的 D653 点码）";

        _auxPoint.changed += (_, _) => AtUiThreadExecute(RefreshAuxPoint);
        _manual.enabledChanged += (_, Enabled) => AtUiThreadExecute(() => IsManualEnabled = Enabled);
        _userItem.userChanged += (_, _) => AtUiThreadExecute(RefreshPermission);
        _valueList.NewValueChanged += (_, Value) => AtUiThreadExecute(() => TagChanged(Value));

        IsManualEnabled = _manual.IsEnabled;
        RefreshPermission();
        RebuildComponentArea();
        RefreshAuxPoint();
    }

    /// <summary>其它点行集合。</summary>
    public BatchObservableCollection<AuxPointRowViewModel> AuxPointCollection { get; } = new();

    // ── 初始化 ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    public override async Task InitializeAsync()
    {
        RefreshPermission();
        RebuildComponentArea();
        RebuildAxisList();
        await ReadAuxAddressAsync().ConfigureAwait(false);
        await SyncActionButtonStateAsync().ConfigureAwait(false);

        var (X, Y, Z) = await _calibration.ReadCenterAsync().ConfigureAwait(false);
        var Offset = await _calibration.ReadOffsetAsync().ConfigureAwait(false);
        var (bottomCameraXValue, bottomCameraZValue) = await _calibration.ReadBottomCameraCenterAsync().ConfigureAwait(false);
        var bottomCameraOffset = await _calibration.ReadBottomCameraOffsetAsync().ConfigureAwait(false);

        AtUiThreadExecute(() =>
        {
            CenterX = CoordinateAux.Format(X);
            CenterY = CoordinateAux.Format(Y);
            CenterZ = CoordinateAux.Format(Z);
            OffsetValue = CoordinateAux.Format(Offset);

            BottomCameraX = CoordinateAux.Format(bottomCameraXValue);
            BottomCameraZ = CoordinateAux.Format(bottomCameraZValue);
            BottomCameraOffset = CoordinateAux.Format(bottomCameraOffset);
        });

        await ReadAllAuxPointAsync().ConfigureAwait(false);
        await SyncOtherPointPointCodeAsync().ConfigureAwait(false);

        AtUiThreadExecute(RefreshAxisPositionSnapshot);
    }

    // ── 标定（上相机 / 下相机的写入与启动）：见 CalibrationViewModel.Camera.cs ──

    // ── 地址编辑（仅管理员） ────────────────────────────────────────────

    /// <summary>
    /// 编辑标定页所有与 PLC 交互的元件地址：中心点坐标、每点偏移量、标定启动，
    /// 以及锁附 X/Y/Z 的点动与回零线圈。
    /// </summary>
    [RelayCommand]
    private void EditAddressAsync()
    {
        if (!EditableAddress)
        {
            _dialog.Hint("权限不足", "仅管理员可编辑地址，请联系设备管理员。");
            return;
        }

        SafeInvokeAsync.FireAndForget(EditAddressCoreAsync, _log, "编辑标定页地址");
    }

    private async Task EditAddressCoreAsync()
    {
        var Request = new AddressEditRequest
        {
            Title = "标定 / 其它点",
            DefaultGroup = "标定",
            ItemCollection = _pageAddress.CollectSetCalibrationPage().ToList(),
        };

        if (!_dialog.EditPageAddress(Request))
        {
            return;
        }

        IsBusy = true;

        try
        {
            if (await UiCommand
                    .ExecuteAsync(_log, _dialog, "保存", () => _pageAddress.SaveCalibrationPageAsync(Request))
                    .ConfigureAwait(true))
            {
                AtUiThreadExecute(() =>
                {
                    RebuildComponentArea();
                    _dialog.Hint("已保存", "标定页元件地址已保存并立即生效。");
                });
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── 档位切换（统一方向键盘中央按钮） ────────────────────────────────

    /// <summary>当前点动档位文本（低/中/高）。</summary>
    [ObservableProperty] private string _gearText = "低";

    private int _currentGear = 0;

    /// <summary>
    /// 档位切换：低 → 中 → 高 → 低 循环，写入值 0/1/2。
    /// <para>六轴共用同一个速度档位地址（2026-10-13 现场要求），写哪个轴键都落到同一寄存器。</para>
    /// </summary>
    [RelayCommand]
    private async Task SwitchCalibrationGearAsync()
    {
        _currentGear = (_currentGear + 1) % 3;
        GearText = _currentGear switch { 1 => "中", 2 => "高", _ => "低" };

        await UiCommand.ExecuteAsync(_log, _dialog, "切换点动档位",
            () => _manual.SetJogSpeedLevelAsync("X", _currentGear));
    }

    // ── 标定页手动点动（X / Y / Z） ─────────────────────────────────────

    /// <summary>按住方向按钮：启动点动。方向键形如 <c>X+</c> / <c>Z-</c>。</summary>
    public void StartCalibrationJog(string directionKey)
    {
        if (directionKey.Length < 2)
        {
            return;
        }

        var AxisKey = directionKey[..1];
        var direction = directionKey[^1] == '-' ? -1 : 1;

        SafeInvokeAsync.FireAndForget(
            () => CalibrationJogCoreAsync(AxisKey, direction),
            _log,
            $"{AxisKey} 轴标定点动");
    }

    /// <summary>松开方向按钮：停止点动（无条件生效）。</summary>
    public void EndCalibrationJog()
    {
        SafeInvokeAsync.FireAndForget(
            async () =>
            {
                await _manual.StopJogAxisAsync("X").ConfigureAwait(false);
                await _manual.StopJogAxisAsync("Y").ConfigureAwait(false);
                await _manual.StopJogAxisAsync("Z").ConfigureAwait(false);
            },
            _log,
            "标定页停止点动");
    }

    private async Task CalibrationJogCoreAsync(string AxisKey, int direction)
    {
        if (!IsManualEnabled)
        {
            _dialog.Hint("操作被拒绝", "请先开启手动使能，再执行点动。");
            return;
        }

        await UiCommand
            .ExecuteAsync(_log, _dialog, $"{AxisKey} 点动", () => _manual.JogAxisAsync(AxisKey, direction))
            .ConfigureAwait(true);
    }

    // ── 刷新 ────────────────────────────────────────────────────────────

    private void RefreshPermission() => EditableAddress = _userItem.Has(Permission.EditBindAddress);

    private void ReloadAuxPoint(IReadOnlyList<AuxPointDefinition> list)
    {
        // 选中跟随"点名"而不是序号：列表重建（新增 / 删除 / 改名 / 换查询关键字）后序号会整体挪位。
        // 点名仍在全量列表里就保持选中（哪怕当前被查询过滤掉了），被删掉才回到未选中。
        var selectedName = AuxPointCollection.FirstOrDefault(rowItem => rowItem.IsCurrent)?.Name;

        // 点码按"全量列表里的次序"算，不随查询过滤变动——否则同一个点
        // 在不同筛选下会报出两个点码，PLC 侧拿到的一会儿这个点一会儿那个点
        AuxPointCollection.ReplaceAll(list.Select(Item =>
            new AuxPointRowViewModel(Item, IndexOf(Item.Name), _auxPoint, SelectedOtherPoint)));

        SelectedOtherPointIndex = selectedName is null ? -1 : IndexOf(selectedName);
        RefreshSelectedState();
    }
}
