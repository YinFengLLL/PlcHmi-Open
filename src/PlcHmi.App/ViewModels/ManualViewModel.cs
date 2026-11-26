using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PlcHmi.App.Services;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Models;

namespace PlcHmi.App.ViewModels;

/// <summary>
/// 手动调试页 ViewModel。
///
/// <para><b>元件配置驱动：</b>气缸 / 按钮 / 指示灯来自 <c>manual-widgets.json</c>，
/// 管理员可在界面上增删改。工程师及以下只能操作不能改配置。</para>
/// </summary>
public sealed partial class ManualViewModel : ViewModelBase
{
    private readonly IManualWidgetService _manual;
    private readonly IManualComponentService _component;
    private readonly IUserService _userItem;
    private readonly ITagValueService _valueList;
    private readonly IDialogService _dialog;
    private readonly PlcConnectionOptions? _plcOptions;
    private readonly ILogger<ManualViewModel> _log;

    [ObservableProperty] private bool _isManualEnabled;
    [ObservableProperty] private AxisViewModel? _selectedAxis;
    [ObservableProperty] private string _currentPositionText = "--.--";
    [ObservableProperty] private bool _editableComponent;
    [ObservableProperty] private int _jogSpeedLevel = JogSpeedLevelAddress.MinLevel;

    /// <summary>
    /// 各轴最近一次写入的点动速度档位。
    /// <para><b>这是上位机的记忆，不是 PLC 实时回读</b>——与气缸、参数页按钮表同一约定。
    /// 若有人在 PLC 侧改过档位，界面会与设备不一致，按一次即可纠正。</para>
    /// </summary>
    private readonly Dictionary<string, int> _jogLevel = new(StringComparer.OrdinalIgnoreCase);

    public ManualViewModel(
        IManualWidgetService Manual,
        IManualComponentService ComponentList,
        IUserService User,
        ITagValueService ValueList,
        IDialogService dialogCollection,
        RecipeConfig configPairObject,
        ILogger<ManualViewModel> Log,
        PlcConnectionOptions? PlcOptions = null)
    {
        _manual = Manual;
        _component = ComponentList;
        _userItem = User;
        _valueList = ValueList;
        _dialog = dialogCollection;
        _plcOptions = PlcOptions;
        _log = Log;

        Title = "手动调试";
        Subtitle = "元件手动与六轴点动 · 需先开启手动使能";

        // 六轴（锁附 X/Y/Z/ZZ + 点胶 X/Z）由配置驱动：点动线圈与位置回读点位都在
        // recipe-config.json 的 jogButtons 里，地址改了页面跟着变，不在代码里写死。
        foreach (var axisItem in configPairObject.Jog
                     .GroupBy(Item => Item.Axis, StringComparer.OrdinalIgnoreCase)
                     .Select(groupValue => groupValue.First()))
        {
            AxisList.Add(new AxisViewModel(axisItem));
        }

        if (AxisList.Count == 0)
        {
            // 配置缺失时不至于空页面崩溃；由界面提示"未配置点动轴"
            AxisList.Add(new AxisViewModel(new JogButtonItem { Axis = "X", Name = "X 轴" }));
        }

        _selectedAxis = AxisList[0];
        _selectedAxis.IsSelected = true;

        _valueList.NewValueChanged += (_, Value) => AtUiThreadExecute(() =>
        {
            foreach (var axisNumber in AxisList.Where(item => item.PositionTag == Value.Name))
            {
                if (Value.IsValid)
                {
                    axisNumber.PositionText = Value.scaleAfterNewValue.ToString("0.00");
                }
            }

            if (SelectedAxis is not null && SelectedAxis.PositionTag == Value.Name && Value.IsValid)
            {
                CurrentPositionText = SelectedAxis.PositionText;
            }
        });

        _component.componentChanged += (_, _) => AtUiThreadExecute(RebuildComponent);
        _userItem.userChanged += (_, _) => AtUiThreadExecute(RefreshPermission);

        // 使能开关与示教页共用同一个服务，靠事件保持两页显示一致
        _manual.enabledChanged += (_, Enabled) => AtUiThreadExecute(() => IsManualEnabled = Enabled);

        // 只推变化的事件订阅方必须在构造时拉一次快照，否则会漏掉初始状态
        IsManualEnabled = _manual.IsEnabled;

        RebuildComponent();
        RefreshPermission();
    }

    /// <summary>手动页元件列表（配置驱动）。</summary>
    public ObservableCollection<ManualComponentViewModel> ComponentCollection { get; } = new();

    /// <summary>轴列表。</summary>
    public ObservableCollection<AxisViewModel> AxisList { get; } = new();

    /// <summary>当前是否有元件（空时界面显示提示）。</summary>
    public bool HasComponent => ComponentCollection.Count > 0;

    /// <inheritdoc />
    public override Task InitializeAsync()
    {
        RebuildComponent();
        RefreshPermission();
        return Task.CompletedTask;
    }

    // ── 手动使能 ────────────────────────────────────────────────────────

    partial void OnIsManualEnabledChanged(bool value)
    {
        // 与服务状态一致时直接返回：这通常是“由事件回写”的场景，
        // 继续往下会绕圈（服务 → 事件 → 属性 → 服务）。
        if (_manual.IsEnabled == value)
        {
            return;
        }

        _manual.SetEnabled(value);

        // 原写法把 `_ = SyncEnableAsync()` 包在 try/catch 里——捕不到：
        // TagWriter 的写入失败发生在 await 之后，落在 Task 上。交给 AsyncSafe 记录。
        SafeInvokeAsync.FireAndForget(
            () => _manual.SyncEnabledAsync(),
            _log,
            "同步手动使能状态到 PLC");
    }

    // ── 元件操作 ────────────────────────────────────────────────────────

    /// <summary>选中轴。</summary>
    [RelayCommand]
    private void SelectAxis(AxisViewModel? axisNumber)
    {
        if (axisNumber is not null)
        {
            SelectedAxis = axisNumber;
        }
    }

    /// <summary>
    /// 切换选中轴时同步选中标记与位置显示。
    ///
    /// <para>放在属性回调里而不是命令里：这样任何路径改 <see cref="SelectedAxis"/>
    /// 都能保持标记一致，不会出现"两个轴同时高亮"或"点不动"。</para>
    /// </summary>
    partial void OnSelectedAxisChanged(AxisViewModel? oldValue, AxisViewModel? newValue)
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

        // 档位随轴切换：显示该轴最近一次写入的值
        RefreshJogSpeedLevel();
    }

    /// <summary>元件动作：伸出 / 缩回（按当前状态自动反向）。</summary>
    [RelayCommand]
    private async Task SwitchComponentAsync(ManualComponentViewModel? ComponentItem)
    {
        if (ComponentItem is null)
        {
            return;
        }

        var action = ComponentItem.IsIndicator
            ? null
            : ComponentItem.IsActive ? "缩回" : "伸出";

        if (action is null)
        {
            return;
        }

        // 不再弹确认框（现场口径 2026-10-13）：这里本来就是"按一下伸出、再按一下缩回"的切换，
        // 每次弹窗既拖慢节奏，也让人对确认框麻木——真出问题时反而看不见。
        // 元件当前状态在按钮上直接可见（活动/停止），比弹窗更直观。
        await RunManualAsync($"{ComponentItem.Name} {action}", async () =>
        {
            if (ComponentItem.IsActive)
            {
                await ComponentItem.StopUseAsync().ConfigureAwait(false);
            }
            else
            {
                await ComponentItem.ActiveAsync().ConfigureAwait(false);
            }
        });
    }

    // ── 元件配置管理（仅管理员） ────────────────────────────────────────

    /// <summary>新增元件。</summary>
    [RelayCommand]
    private async Task AddComponentAsync()
    {
        if (!EditableComponent)
        {
            _dialog.Hint("权限不足", "只有管理员可以增删手动页元件。");
            return;
        }

        var ComponentItem = new ManualComponentDefinition
        {
            Id = _component.NewId(ManualComponentType.Cylinder),
            Kind = ManualComponentType.Cylinder,
            Name = "新气缸",
            Order = ComponentCollection.Count + 1,
            Address = "M",
        };

        if (!_dialog.EditManualComponent(ComponentItem))
        {
            return;
        }

        var validateItem = _component.Validate(ComponentItem);

        if (!validateItem.IsValid)
        {
            _dialog.ErrorInfo("校验失败", validateItem.Summary);
            return;
        }

        _component.Add(ComponentItem);

        try
        {
            await _component.SaveAsync().ConfigureAwait(false);
            _dialog.Hint("已添加", $"元件「{ComponentItem.Name}」已添加并保存。");
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "保存元件失败");
            _dialog.ErrorInfo("保存失败", exception.Message);
        }
    }

    /// <summary>编辑元件（改名称、类型、绑定地址）。</summary>
    [RelayCommand]
    private async Task EditComponentAsync(ManualComponentViewModel? ComponentItem)
    {
        if (ComponentItem is null)
        {
            return;
        }

        if (!EditableComponent)
        {
            _dialog.Hint("权限不足", "只有管理员可以修改元件的绑定地址。");
            return;
        }

        var alreadyEdit = ComponentItem.Definition.Clone();
        alreadyEdit.Id = ComponentItem.Id;

        if (!_dialog.EditManualComponent(alreadyEdit))
        {
            return;
        }

        var validateItem = _component.Validate(alreadyEdit);

        if (!validateItem.IsValid)
        {
            _dialog.ErrorInfo("校验失败", validateItem.Summary);
            return;
        }

        _component.Update(alreadyEdit);

        try
        {
            await _component.SaveAsync().ConfigureAwait(false);
            _dialog.Hint("已保存", $"元件「{alreadyEdit.Name}」已更新。");
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "保存元件失败");
            _dialog.ErrorInfo("保存失败", exception.Message);
        }
    }

    /// <summary>删除元件。</summary>
    [RelayCommand]
    private async Task RemoveComponentAsync(ManualComponentViewModel? ComponentItem)
    {
        if (ComponentItem is null)
        {
            return;
        }

        if (!EditableComponent)
        {
            _dialog.Hint("权限不足", "只有管理员可以删除手动页元件。");
            return;
        }

        if (!_dialog.Confirm("删除元件", $"确认删除元件「{ComponentItem.Name}」？\n\n此操作只影响界面配置，不会改动 PLC。"))
        {
            return;
        }

        _component.Remove(ComponentItem.Id);

        try
        {
            await _component.SaveAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "保存元件失败");
            _dialog.ErrorInfo("保存失败", exception.Message);
        }
    }

    // ── JOG（按住才动、松开即停） ───────────────────────────────────────

    /// <summary>
    /// 按住方向按钮：启动点动。
    /// <para>刻意不做成 Command——"按住 / 松开"需要 MouseDown / MouseUp，
    /// 由 View 的 <c>JogGesture</c> 手势助手调用本方法。</para>
    /// </summary>
    public void StartJog(int direction) =>
        SafeInvokeAsync.FireAndForget(
            () => StartJogCoreAsync(direction),
            _log,
            $"{SelectedAxis?.Name ?? "当前轴"}启动点动");

    /// <summary>
    /// 松开方向按钮：停止点动。
    /// <para><b>无条件生效</b>：刻意不走 <c>RunManualAsync</c>（那会先检查手动使能）。
    /// 若使能意外断开就拒绝停止，轴会一直动下去——那是安全事故。</para>
    /// </summary>
    public void EndJog() =>
        SafeInvokeAsync.FireAndForget(
            StopJogCoreAsync,
            _log,
            $"{SelectedAxis?.Name ?? "当前轴"}停止点动");

    private async Task StartJogCoreAsync(int direction)
    {
        if (SelectedAxis is null)
        {
            return;
        }

        var axisNumber = SelectedAxis.AxisKey;
        var TagName = direction > 0 ? "正向" : "负向";

        await RunManualAsync($"{axisNumber} 轴{TagName}点动", () =>
            _manual.JogAxisAsync(axisNumber, direction)).ConfigureAwait(false);
    }

    private async Task StopJogCoreAsync()
    {
        if (SelectedAxis is null)
        {
            return;
        }

        try
        {
            await _manual.StopJogAxisAsync(SelectedAxis.AxisKey).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _log.LogWarning(exception, "停止点动失败");
        }
    }

    // ── 点动速度档位（随轴切换） ────────────────────────────────────────

    /// <summary>低速档是否选中。</summary>
    public bool IsJogLow => JogSpeedLevel == JogSpeedLevelAddress.MinLevel;

    /// <summary>中速档是否选中。</summary>
    public bool IsJogMedium => JogSpeedLevel == JogSpeedLevelAddress.MinLevel + 1;

    /// <summary>高速档是否选中。</summary>
    public bool IsJogHeight => JogSpeedLevel == JogSpeedLevelAddress.maxLevel;

    partial void OnJogSpeedLevelChanged(int value)
    {
        OnPropertyChanged(nameof(IsJogLow));
        OnPropertyChanged(nameof(IsJogMedium));
        OnPropertyChanged(nameof(IsJogHeight));
    }

    /// <summary>把档位显示同步成当前轴记忆的值（没有记忆时回到低速）。</summary>
    private void RefreshJogSpeedLevel()
    {
        var axisNumber = SelectedAxis?.AxisKey ?? string.Empty;

        JogSpeedLevel = _jogLevel.TryGetValue(axisNumber, out var Level)
            ? Level
            : JogSpeedLevelAddress.MinLevel;
    }

    /// <summary>
    /// 写入点动速度档位：1 低速 / 2 中速 / 3 高速。
    /// <para>档位是参数设定而非动作机构，所以不要求手动使能（与参数页延时/按钮表一致）。</para>
    /// </summary>
    /// <param name="level">档位值文本，由按钮的 <c>CommandParameter</c> 传入。</param>
    [RelayCommand]
    private async Task SetJogSpeedAsync(string Level)
    {
        if (SelectedAxis is null)
        {
            return;
        }

        if (!int.TryParse(Level, NumberStyles.Integer, CultureInfo.InvariantCulture, out var resolved)
            || !JogSpeedLevelAddress.IsValidLevel(resolved))
        {
            return;
        }

        var axisNumber = SelectedAxis.AxisKey;

        try
        {
            IsBusy = true;

            await _manual.SetJogSpeedLevelAsync(axisNumber, resolved).ConfigureAwait(false);

            _jogLevel[axisNumber] = resolved;

            AtUiThreadExecute(() => JogSpeedLevel = resolved);
            _log.LogInformation("{Axis} 轴点动速度档位：{Level}", axisNumber, resolved);
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "{Axis} 轴点动速度档位写入失败", axisNumber);
            AtUiThreadExecute(() => _dialog.ErrorInfo("档位写入失败", exception.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── 内部 ────────────────────────────────────────────────────────────

    /// <summary>按配置重建元件列表。</summary>
    private void RebuildComponent()
    {
        ComponentCollection.Clear();

        var VariableAddressing = BindingDisplay.IsVariableAddressing(_plcOptions);

        foreach (var DefinitionItem in _component.ComponentCollection)
        {
            ComponentCollection.Add(new ManualComponentViewModel(DefinitionItem, _manual, VariableAddressing));
        }

        OnPropertyChanged(nameof(HasComponent));
    }

    private void RefreshPermission() => EditableComponent = _userItem.Has(Permission.EditManualComponent);

    private async Task RunManualAsync(string action, Func<Task> Command)
    {
        if (!IsManualEnabled)
        {
            _dialog.Hint("操作被拒绝", "请先开启手动使能，再执行手动动作。");
            return;
        }

        try
        {
            IsBusy = true;
            await Command().ConfigureAwait(false);
            _log.LogInformation("已执行：{Action}", action);
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "{Action} 执行失败", action);
            _dialog.ErrorInfo("操作失败", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
