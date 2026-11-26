using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PlcHmi.App.Services;
using PlcHmi.Business.Services;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Models;

namespace PlcHmi.App.ViewModels;

/// <summary>参数值读取的轻量访问器（避免页面直接依赖业务内部类型）。</summary>
public sealed class TagValueServiceAccessor
{
    private readonly ITagValueService _valueList;

    public TagValueServiceAccessor(ITagValueService ValueList) => _valueList = ValueList;

    public bool TryGet(string nameValue, out double Value) => _valueList.TryGetDouble(nameValue, out Value);
}

/// <summary>延时表 / 按钮表的一行。</summary>
public sealed partial class ParameterEntryRowViewModel : ObservableObject
{
    private readonly IParameterTableService _dataGrid;
    private readonly ParameterTableCategory _category;
    private readonly bool _variableAddressing;
    private bool _loadValue;

    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private string _valueText = "0.000";
    [ObservableProperty] private string _buttonStateText = "—";

    public ParameterEntryRowViewModel(
        ParameterEntryDefinition DefinitionItem, ParameterTableCategory Category, IParameterTableService DataGrid, bool VariableAddressing = false)
    {
        Definition = DefinitionItem;
        _category = Category;
        _dataGrid = DataGrid;
        _variableAddressing = VariableAddressing;

        _isEnabled = DefinitionItem.Enabled;
        _valueText = DefinitionItem.Value.ToString("0.000");
    }

    /// <summary>底层定义（编辑用）。</summary>
    public ParameterEntryDefinition Definition { get; }

    public string Id => Definition.Id;
    public string Name => Definition.Name;

    /// <summary>地址列显示：OPC UA 模式且已绑定变量时显示变量名（物理地址无关）。</summary>
    public string Address => BindingDisplay.AddressText(_variableAddressing, Definition.Variable, Definition.Address);

    /// <summary>取值显示（延时按秒，格式 XX.XXX S；PLC 的 D 区存毫秒整数，见模型注释）。</summary>
    public string NewValueShow => _category == ParameterTableCategory.Delay
        ? $"{Definition.Value:0.000} S"
        : "—";

    /// <summary>是否显示“当前值”列（按钮表不显示）。</summary>
    public bool ShowNewValue => _category == ParameterTableCategory.Delay;

    /// <summary>是否为按钮行（显示置位 / 复位操作与状态）。</summary>
    public bool IsButtonRow => _category == ParameterTableCategory.Button;

    /// <summary>停用的行置灰。</summary>
    public double RowOpacity => Definition.Enabled ? 1.0 : 0.45;

    partial void OnIsEnabledChanged(bool value)
    {
        Definition.Enabled = value;
        OnPropertyChanged(nameof(RowOpacity));

        // 就地更新内存副本（不落盘，由「保存到本地」统一写）
        _dataGrid.Update(_category, Definition);
    }

    /// <summary>
    /// 延时值提交（失焦时触发，对应界面绑定 <c>UpdateSourceTrigger=LostFocus</c>）。
    /// <para>需求方确认：参数页元件直接绑定 D/M 区、<b>实时生效</b>，不再走「下发到 PLC」。
    /// 用 LostFocus 而不是 PropertyChanged，是为了避免每敲一个字符就写一次 PLC（红线 R3）。</para>
    /// </summary>
    public event EventHandler<ParameterEntryRowViewModel>? newValueCommitted;

    partial void OnValueTextChanged(string value)
    {
        if (_loadValue)
        {
            return;
        }

        if (CoordinateAux.TryParse(value, out var resolved))
        {
            Definition.Value = resolved;
            OnPropertyChanged(nameof(NewValueShow));
            newValueCommitted?.Invoke(this, this);
        }
    }

    /// <summary>重载行显示（不触发写回），供表格重建后刷新用。</summary>
    public void ReloadNewValue()
    {
        _loadValue = true;
        ValueText = Definition.Value.ToString("0.000");
        _loadValue = false;
    }

    /// <summary>
    /// 记录按钮的本地操作结果。
    /// <para>2026-10-17 起按钮状态会由 <see cref="ParameterTableService.ReadBackAsync"/> 周期回读，
    /// 这里的"开启 / 关闭"是<b>PLC 侧实时状态</b>（本地操作只是立刻刷新一次，不必等下一轮回读）。</para>
    /// </summary>
    public void MarkButtonState(bool on) => ButtonStateText = on ? "开启" : "关闭";

    /// <summary>
    /// 应用回读到的延时值（秒）。
    /// <para><b>用 <see cref="_loadValue"/> 挡住写回</b>：回读绝不能反过来写 PLC
    /// （那会形成"读→写→读"的无谓循环，还可能把别人刚改的值覆盖回去）。</para>
    /// </summary>
    public void ApplyReadBackDelay(double seconds)
    {
        if (Math.Abs(Definition.Value - seconds) < 1e-9)
        {
            return;   // 与本地一致：不必每 2s 发一次无谓的属性通知
        }

        _loadValue = true;
        Definition.Value = seconds;
        ValueText = seconds.ToString("0.000");
        _loadValue = false;

        OnPropertyChanged(nameof(NewValueShow));
    }
}

/// <summary>
/// 参数设置页 ViewModel。
///
/// <para><b>四张表：</b>轴速度（每轴只保留定位速度，含可编辑的寄存器地址）、延时表、按钮表，
/// 以及仅管理员可见的「轴地址」表（位置 / 正反转点动 / 点动速度档位）。
/// 点动测试已移到示教页与手动调试页。</para>
///
/// <para><b>写入时机：</b>轴速度数值改内存，只有按「下发到 PLC」或切换机种才写设备；
/// 延时数值实时下发；<b>地址改动一律走「保存轴地址」</b>，由服务层统一校验后落盘并重建采集计划。</para>
///
/// <para><b>权限：</b>延时表 / 按钮表 / 轴地址的增删改仅管理员可操作（服务层同步校验）。</para>
/// </summary>
/// <summary>
/// 参数设置页 ViewModel。
///
/// <para><b>四张表：</b>轴速度（每轴只保留定位速度，含可编辑的寄存器地址）、延时表、按钮表，
/// 以及仅管理员可见的「轴地址」表（位置 / 正反转点动 / 点动速度档位）。
/// 点动测试已移到示教页与手动调试页。</para>
///
/// <para><b>写入时机：</b>轴速度数值改内存，只有按「下发到 PLC」或切换机种才写设备；
/// 延时数值实时下发；<b>地址改动走「保存」或「保存轴地址」</b>，由服务层统一校验后落盘并重建采集计划。</para>
///
/// <para><b>权限：</b>延时表 / 按钮表 / 轴地址的增删改仅管理员可操作（服务层同步校验）。</para>
///
/// <para>轴地址部分见 <c>ParamViewModel.AxisAddress.cs</c>（同一 partial 类型）。</para>
/// </summary>
public sealed partial class ParameterViewModel : ViewModelBase, IParameterReadBackPage
{
    private readonly IRecipeService _recipe;
    private readonly IParameterTableService _dataGrid;
    private readonly IAxisAddressService _axisAddress;
    private readonly IUserService _userItem;
    private readonly IDialogService _dialog;
    private readonly PlcConnectionOptions? _plcOptions;
    private readonly ILogger<ParameterViewModel> _log;

    [ObservableProperty] private string _recipeBadgeMark = "机种 --";
    [ObservableProperty] private string _stateText = "改数值不会立即写 PLC，需按「下发到 PLC」";
    [ObservableProperty] private bool _editableTable;
    [ObservableProperty] private bool _operableValueList;

    /// <summary>OPC UA 模式：地址列显示变量名（物理地址无关），轴速度行隐藏物理地址输入框。</summary>
    public bool VariableAddressingMode => BindingDisplay.IsVariableAddressing(_plcOptions);

    public ParameterViewModel(
        IRecipeService recipeItem,
        IParameterTableService DataGrid,
        IAxisAddressService axisAddressList,
        IUserService User,
        IDialogService dialogCollection,
        IReadOnlyList<AxisDefinition> axisCollection,
        ILogger<ParameterViewModel> Log,
        PlcConnectionOptions? PlcOptions = null)
    {
        _recipe = recipeItem;
        _dataGrid = DataGrid;
        _axisAddress = axisAddressList;
        _userItem = User;
        _dialog = dialogCollection;
        _plcOptions = PlcOptions;
        _log = Log;

        Title = "参数设置";
        Subtitle = "轴速度 / 延时 / 按钮 · 按钮可置位复位（即写 M5000–M5099），增删改地址仅管理员";

        var VariableAddressing = BindingDisplay.IsVariableAddressing(PlcOptions);

        foreach (var axisNumber in axisCollection)
        {
            AxisSpeed.Add(new AxisSpeedRowViewModel(axisNumber, recipeItem, VariableAddressing));
        }

        _recipe.recipeChanged += (_, _) => AtUiThreadExecute(RefreshAll);
        _dataGrid.entryChanged += (_, _) => AtUiThreadExecute(RebuildTable);
        _axisAddress.AddressChanged += (_, _) => AtUiThreadExecute(RefreshAll);
        _userItem.userChanged += (_, _) => AtUiThreadExecute(RefreshPermission);

        RefreshAll();
        RebuildTable();
        RefreshPermission();
    }

    /// <summary>轴速度表。</summary>
    public ObservableCollection<AxisSpeedRowViewModel> AxisSpeed { get; } = new();

    /// <summary>「轴地址」表（位置 / 正反转点动 / 点动速度档位）。仅管理员可见可改。</summary>
    public ObservableCollection<AxisAddressRowViewModel> AxisAddressCollection { get; } = new();

    /// <summary>延时表。</summary>
    public ObservableCollection<ParameterEntryRowViewModel> DelayCollection { get; } = new();

    /// <summary>按钮表。</summary>
    public ObservableCollection<ParameterEntryRowViewModel> ButtonCollection { get; } = new();

    /// <inheritdoc />
    public override Task InitializeAsync()
    {
        if (AddressHasChangeMove())
        {
            // 地址栏是独立提交的草稿：换页返回时同样不能把没提交的改动刷掉
            // （现场把"读取/重进页面后地址回默认值"当成"改了保存不了"）。
            StateText = "地址栏有未保存的修改：按「保存」提交，或按「读取」放弃";

            foreach (var rowItem in AxisSpeed)
            {
                rowItem.Refresh();
            }
        }
        else
        {
            RefreshAll();
        }

        RebuildTable();
        RefreshPermission();
        return Task.CompletedTask;
    }

    // ── 下发 / 保存 ─────────────────────────────────────────────────────

    /// <summary>
    /// 读取（刷新）：「轴速度」数值与地址、延时/按钮表都从配置文件重读。
    ///
    /// <para>地址栏是<b>独立提交</b>的一套编辑（唯一出口是「保存轴地址」），所以刷新会把没提交的
    /// 地址改动丢掉——这在现场被理解成"改了地址保存不了"（2026-10-14 反馈）。
    /// 因此丢弃前先问一句，绝不静默回退。</para>
    /// </summary>
    [RelayCommand]
    private void Read()
    {
        if (AddressHasChangeMove() && !_dialog.Confirm("读取",
                "地址栏还有未保存的修改。\n\n读取会把这些修改恢复为配置文件里的值"
                + "（要保存修改请按「保存」或「保存轴地址」）。是否继续读取？"))
        {
            return;
        }

        RefreshAll();
    }

    [RelayCommand]
    private async Task WriteAsync()
    {
        if (!_dialog.Confirm("下发到 PLC",
                $"确认把当前界面的全部参数写入 PLC（机种 {_recipe.CurrentRecipeNumber:00}）？",
                Views.ConfirmLevel.Warning, "下发",
                "PLC 参数区将被界面当前值整体覆盖，设备行为立即改变。"))
        {
            return;
        }

        try
        {
            IsBusy = true;
            await _recipe.WriteParameterAsync().ConfigureAwait(false);

            var countValue = _recipe.ParameterSpec.Count;
            var Number = _recipe.CurrentRecipeNumber;

            StateText = $"已下发 {countValue} 项参数，并已保存到本机种 {Number:00}";
            _dialog.Hint("下发完成", $"参数已下发到 PLC，并已保存到本机种（机种 {Number:00}）。");
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "参数下发失败");
            _dialog.ErrorInfo("下发失败", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            IsBusy = true;

            // 地址栏也是本页的编辑：只保存数值与表格，会让"改了地址再按保存"看起来没生效
            // （现场正是这么用的）。管理员且地址确有改动时，一并走轴地址保存流程。
            if (EditableTable && AddressHasChangeMove())
            {
                await SaveAxisAddressAsync().ConfigureAwait(false);
            }

            await _recipe.SaveThisGroundAsync().ConfigureAwait(false);
            await _dataGrid.SaveAsync().ConfigureAwait(false);
            _dialog.Hint("保存成功", "机种参数与延时/按钮表已保存到本地配置文件。");
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "保存失败");
            _dialog.ErrorInfo("保存失败", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void RestoreDefaultValue()
    {
        if (!_dialog.Confirm("恢复默认",
                "确认将本机种的参数恢复为出厂默认值？",
                Views.ConfirmLevel.Danger, "恢复默认",
                "界面上已调整的参数值会被覆盖；这里只恢复到界面，需再按「下发到 PLC」才会写入设备。"))
        {
            return;
        }

        _recipe.RestoreDefaultValue();
        RefreshAll();

        StateText = "已恢复为默认值（尚未写入 PLC）";
    }

    // ── 内部 ────────────────────────────────────────────────────────────

    private async Task AddEntryAsync(ParameterTableCategory Category, string TagName)
    {
        if (!EnsureEditable())
        {
            return;
        }

        var Entry = new ParameterEntryDefinition
        {
            Id = _dataGrid.NewId(Category),
            Name = $"新{TagName}",
            Address = Category == ParameterTableCategory.Delay
                ? $"D{ParameterTableService.delayAddressMin}"
                : $"M{ParameterTableService.buttonAddressMin}",
            Unit = "s",
            Min = 0,
            Max = 2147483.647,   // DINT 毫秒上限（2026-10-16 起延时按 32 位存）
            Enabled = true,
            Order = _dataGrid.Get(Category).Count + 1,
        };

        if (!_dialog.EditParameterEntry(Category, Entry))
        {
            return;
        }

        var validateItem = _dataGrid.Validate(Category, Entry);

        if (!validateItem.IsValid)
        {
            _dialog.ErrorInfo("校验失败", validateItem.Summary);
            return;
        }

        _dataGrid.Add(Category, Entry);
        await SaveTableAsync($"{TagName}「{Entry.Name}」已添加。").ConfigureAwait(false);
    }

    private async Task EditEntryAsync(ParameterTableCategory Category, ParameterEntryRowViewModel? rowItem, string TagName)
    {
        if (rowItem is null || !EnsureEditable())
        {
            return;
        }

        var alreadyEdit = rowItem.Definition.Clone();

        if (!_dialog.EditParameterEntry(Category, alreadyEdit))
        {
            return;
        }

        // isUpdate: true —— 编辑的是克隆副本，同 Id 的条目就是自身，不能报「已被占用」。
        var validateItem = _dataGrid.Validate(Category, alreadyEdit, isUpdate: true);

        if (!validateItem.IsValid)
        {
            _dialog.ErrorInfo("校验失败", validateItem.Summary);
            return;
        }

        _dataGrid.Update(Category, alreadyEdit);
        await SaveTableAsync($"{TagName}「{alreadyEdit.Name}」已更新。").ConfigureAwait(false);
    }

    private async Task RemoveEntryAsync(ParameterTableCategory Category, ParameterEntryRowViewModel? rowItem, string TagName)
    {
        if (rowItem is null || !EnsureEditable())
        {
            return;
        }

        if (!_dialog.Confirm($"删除{TagName}",
                $"确认删除{TagName}「{rowItem.Name}」？",
                Views.ConfirmLevel.Danger, "删除",
                "该条目将从配置文件中移除，删除后需重新录入。"))
        {
            return;
        }

        _dataGrid.Remove(Category, rowItem.Id);
        await SaveTableAsync(null).ConfigureAwait(false);
    }

    private bool EnsureEditable()
    {
        if (EditableTable)
        {
            return true;
        }

        _dialog.Hint("权限不足", "只有管理员可以修改延时表与按钮表。");
        return false;
    }

    private async Task SaveTableAsync(string? Message)
    {
        try
        {
            await _dataGrid.SaveAsync().ConfigureAwait(false);

            if (Message is not null)
            {
                _dialog.Hint("已保存", Message);
            }
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "保存参数表失败");
            _dialog.ErrorInfo("保存失败", exception.Message);
        }
    }

    private void RefreshAll()
    {
        RecipeBadgeMark = string.IsNullOrWhiteSpace(_recipe.CurrentRecipeName)
            ? $"机种 {_recipe.CurrentRecipeNumber:00}"
            : $"机种 {_recipe.CurrentRecipeNumber:00} · {_recipe.CurrentRecipeName}";

        foreach (var rowItem in AxisSpeed)
        {
            rowItem.Refresh();
            rowItem.RefreshAddress();
        }

        RebuildAxisAddress();
    }

    /// <summary>按服务的当前快照重建「轴地址」表。</summary>
    private void RebuildAxisAddress()
    {
        AxisAddressCollection.Clear();

        foreach (var EntryItem in _axisAddress.DataGrid.AxisList)
        {
            var rowItem = new AxisAddressRowViewModel(EntryItem);
            rowItem.Refresh();
            AxisAddressCollection.Add(rowItem);
        }
    }

    private void RebuildTable()
    {
        DelayCollection.Clear();

        var VariableAddressing = VariableAddressingMode;

        foreach (var Entry in _dataGrid.DelayCollection)
        {
            var rowItem = new ParameterEntryRowViewModel(Entry, ParameterTableCategory.Delay, _dataGrid, VariableAddressing);
            rowItem.newValueCommitted += RowNewValueCommitted;
            DelayCollection.Add(rowItem);
        }

        ButtonCollection.Clear();

        foreach (var Entry in _dataGrid.ButtonCollection)
        {
            ButtonCollection.Add(new ParameterEntryRowViewModel(Entry, ParameterTableCategory.Button, _dataGrid, VariableAddressing));
        }
    }

    private void RowNewValueCommitted(object? sender, ParameterEntryRowViewModel rowItem) =>
        SafeInvokeAsync.FireAndForget(
            () => SubmitDelayNewValueAsync(rowItem),
            _log,
            $"延时「{rowItem.Name}」实时下发");

    /// <summary>延时值失焦提交：立即写 D 区并静默落盘（实时生效，需求方 2026-09-11 确认）。</summary>
    private async Task SubmitDelayNewValueAsync(ParameterEntryRowViewModel rowItem)
    {
        if (!OperableValueList)
        {
            return;   // 输入框对无权限者已禁用，这里是服务层之外的双保险
        }

        try
        {
            await _dataGrid.WriteDelayAsync(rowItem.Definition).ConfigureAwait(false);

            var milliseconds = (int)Math.Round(rowItem.Definition.Value * 1000, MidpointRounding.AwayFromZero);
            StateText = $"「{rowItem.Name}」已实时生效：{rowItem.Definition.Value:0.###} S（PLC 写入 {milliseconds} ms）";
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "延时「{Name}」实时下发失败", rowItem.Name);
            AtUiThreadExecute(() => _dialog.ErrorInfo("下发失败", exception.Message));
        }
    }

    private void RefreshPermission()
    {
        // 增删条目与改绑定地址 → 管理员；改数值与操作按钮（置位/复位）→ 工程师
        EditableTable = _userItem.Has(Permission.EditBindAddress);
        OperableValueList = _userItem.Has(Permission.EditParameterValueList);
    }

    // ── 轴地址（仅管理员）：见 ParamViewModel.AxisAddress.cs（partial）────────

    // ── 参数回读（2026-10-17 现场要求）────────────────────────────────────

    /// <summary>
    /// 回读延时表与按钮表的 PLC 当前值（由主界面 1s 时钟按 2s 间隔调用）。
    ///
    /// <para><b>为什么必须回读：</b>延时（D1000–D1098）与按钮（M5000–M5099）不在 <c>tags.json</c> 里，
    /// 不走轮询引擎；但 PLC 侧同样可能改写它们（切换机种、HMI 旁的操作面板、断电前的保持状态）。
    /// 界面只显示本地配置值会与设备实际不符——现场反馈"改了的延时没生效"正是这种现象。</para>
    /// </summary>
    public async Task RefreshParameterReadBackAsync()
    {
        await _dataGrid.ReadBackAsync().ConfigureAwait(true);

        AtUiThreadExecute(() =>
        {
            foreach (var rowItem in DelayCollection)
            {
                if (_dataGrid.ReadBackDelayValue(rowItem.Id) is { } seconds)
                {
                    rowItem.ApplyReadBackDelay(seconds);
                }
            }

            foreach (var rowItem in ButtonCollection)
            {
                if (_dataGrid.ReadBackButtonState(rowItem.Id) is { } State)
                {
                    rowItem.MarkButtonState(State);
                }
            }
        });
    }
}
