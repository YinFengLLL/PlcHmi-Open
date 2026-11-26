using CommunityToolkit.Mvvm.ComponentModel;
using PlcHmi.App.Services;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Models;

namespace PlcHmi.App.ViewModels;

/// <summary>轴速度表的一行：只含该轴的定位速度与它的寄存器地址。</summary>
public sealed partial class AxisSpeedRowViewModel : ObservableObject
{
    private readonly AxisDefinition _definitionItem;
    private readonly IRecipeService _recipe;
    private readonly bool _variableAddressing;

    [ObservableProperty] private string _positioningSpeedText = "0";

    /// <summary>
    /// 定位速度寄存器地址（如 <c>D1202</c>，各轴区偏移 2）。
    /// <para>仅管理员可改。<b>这里只是唯一编辑入口</b>，真正的写入由
    /// <c>IAxisAddressService</c> 统一做服务层校验并原子落盘——
    /// 页面自己写配置会绕过区间/重叠检查。</para>
    /// </summary>
    [ObservableProperty] private string _addressText = string.Empty;

    /// <summary>定位速度绑定的变量表变量名（选变量后自动填充）。</summary>
    [ObservableProperty] private string _variableText = string.Empty;

    public AxisSpeedRowViewModel(AxisDefinition DefinitionItem, IRecipeService recipeItem, bool VariableAddressing = false)
    {
        _definitionItem = DefinitionItem;
        _recipe = recipeItem;
        _variableAddressing = VariableAddressing;
    }

    public string Name => _definitionItem.Name;
    public string Unit => _definitionItem.Unit;
    public string AxisKey => _definitionItem.Axis;

    /// <summary>是否显示物理地址输入框（Modbus 模式）。OPC UA 模式改为只显示绑定变量。</summary>
    public bool ShowPhysicalAddress => !_variableAddressing;

    /// <summary>是否为 OPC UA 变量寻址模式（界面据此切换地址列显示）。</summary>
    public bool VariableAddressingMode => _variableAddressing;

    /// <summary>OPC UA 模式下的绑定显示（变量名；未绑定时给出提示）。</summary>
    public string BindShowText =>
        _bindVariable.Length > 0 ? _bindVariable : BindingDisplay.unboundHint;

    private string _bindVariable => VariableText.Trim();

    /// <summary>定位速度的参数键。</summary>
    public string PositioningSpeedKey => $"{_definitionItem.Axis}.LocateSpeed";

    /// <summary>从机种数据刷新（不触发写 PLC）。</summary>
    public void Refresh()
    {
        if (_recipe.CurrentParameter.TryGetValue(PositioningSpeedKey, out var Value))
        {
            PositioningSpeedText = CoordinateAux.Format(Value);
        }
    }

    /// <summary>刷新地址显示（保存轴地址后需要重读一次）。</summary>
    public void RefreshAddress()
    {
        var specItem = _recipe.ParameterSpec
            .FirstOrDefault(PointItem => string.Equals(PointItem.Key, PositioningSpeedKey, StringComparison.OrdinalIgnoreCase));

        AddressText = specItem is null ? string.Empty : $"D{specItem.Address}";
        VariableText = specItem?.Variable ?? string.Empty;
    }

    /// <summary>改数值只改内存，不写 PLC。</summary>
    partial void OnPositioningSpeedTextChanged(string value)
    {
        if (CoordinateAux.TryParse(value, out var resolved))
        {
            _recipe.SetParameter(PositioningSpeedKey, resolved);
        }
    }

    /// <summary>变量绑定变化时刷新 OPC UA 模式下的绑定显示。</summary>
    partial void OnVariableTextChanged(string value) => OnPropertyChanged(nameof(BindShowText));
}

/// <summary>
/// 「轴地址」表的一行：当前位置 / 正反转点动 / 点动速度档位。
///
/// <para><b>为什么没有定位速度地址这一列</b>：它在上方「轴速度」表里已经有唯一编辑入口，
/// 同一字段在两处可改必然导致漂移与“改了哪一处才算数”的困惑。</para>
/// </summary>
public sealed partial class AxisAddressRowViewModel : ObservableObject
{
    private readonly AxisAddressEntry _entry;

    [ObservableProperty] private string _positionAddress = string.Empty;
    [ObservableProperty] private string _jogForwardAddress = string.Empty;
    [ObservableProperty] private string _jogReverseAddress = string.Empty;
    [ObservableProperty] private string _jogSpeedLevelAddress = string.Empty;
    [ObservableProperty] private string _homeAddress = string.Empty;

    public AxisAddressRowViewModel(AxisAddressEntry EntryItem) => _entry = EntryItem;

    /// <summary>轴显示名，如 <c>X 轴</c>。</summary>
    public string Name => _entry.Name;

    /// <summary>轴标识，用于回写候选表。</summary>
    public string Axis => _entry.Axis;

    /// <summary>从服务快照刷新。</summary>
    public void Refresh()
    {
        PositionAddress = _entry.PositionAddress;
        JogForwardAddress = _entry.JogForwardAddress;
        JogReverseAddress = _entry.JogReverseAddress;
        JogSpeedLevelAddress = _entry.JogSpeedLevelAddress;
        HomeAddress = _entry.HomeAddress;
    }

    /// <summary>把界面上的值写回目标条目（供服务层校验）。</summary>
    public void App(AxisAddressEntry target)
    {
        target.PositionAddress = PositionAddress.Trim();
        target.JogForwardAddress = JogForwardAddress.Trim();
        target.JogReverseAddress = JogReverseAddress.Trim();
        target.JogSpeedLevelAddress = JogSpeedLevelAddress.Trim();
        target.HomeAddress = HomeAddress.Trim();
    }
}
