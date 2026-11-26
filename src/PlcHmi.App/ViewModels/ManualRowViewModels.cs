using CommunityToolkit.Mvvm.ComponentModel;
using PlcHmi.App.Services;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Models;

namespace PlcHmi.App.ViewModels;

/// <summary>
/// 手动页的一个元件（气缸 / 按钮 / 指示灯）。
///
/// <para><b>地址硬约束：</b>元件只能绑定 M 区。服务层会再校验一次，
/// 即使配置文件被手工改错也不会执行到非 M 区地址。</para>
/// </summary>
public sealed partial class ManualComponentViewModel : ObservableObject
{
    private readonly IManualWidgetService _manual;
    private readonly bool _variableAddressing;

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private string _stateText = string.Empty;

    public ManualComponentViewModel(ManualComponentDefinition DefinitionItem, IManualWidgetService Manual, bool VariableAddressing = false)
    {
        Definition = DefinitionItem;
        _manual = Manual;
        _variableAddressing = VariableAddressing;
        UpdateStateText();
    }

    /// <summary>底层定义（编辑用）。</summary>
    public ManualComponentDefinition Definition { get; }

    public string Id => Definition.Id;
    public string Name => Definition.Name;
    public bool IsCylinder => Definition.IsCylinder;
    public bool IsButton => Definition.IsButton;
    public bool IsIndicator => Definition.IsIndicator;

    /// <summary>
    /// 绑定地址提示。
    /// <para>OPC UA 模式且已绑定变量时显示<b>变量名</b>（按变量名符号寻址，物理地址无关）；
    /// 否则显示软元件地址。</para>
    /// </summary>
    public string AddressText => BindingDisplay.AddressText(
        _variableAddressing,
        Definition.IsIndicator ? Definition.StateValueVariable : Definition.Variable,
        Definition.IsIndicator ? Definition.StateAddress : Definition.Address);

    /// <summary>主操作按钮的文案。</summary>
    public string ActionText => Definition.IsButton ? "触发" : IsActive ? "缩回" : "伸出";

    partial void OnIsActiveChanged(bool value) => UpdateStateText();

    private void UpdateStateText()
    {
        if (Definition.IsIndicator)
        {
            StateText = IsActive ? "ON" : "OFF";
        }
        else
        {
            StateText = IsActive ? "伸出" : "缩回";
        }

        OnPropertyChanged(nameof(ActionText));
    }

    /// <summary>
    /// 气缸伸出（置位）/ 按钮触发（脉冲）。
    ///
    /// <para><b>单线圈气缸</b>：置位该位即伸出、复位即缩回，不需要两个地址。</para>
    /// </summary>
    public async Task ActiveAsync()
    {
        if (Definition.IsButton)
        {
            await _manual.PulseCoilAsync(Definition.Address, TimeSpan.Zero).ConfigureAwait(false);
            return;
        }

        await _manual.WriteCoilAsync(Definition.Address, true).ConfigureAwait(false);
        IsActive = true;
    }

    /// <summary>气缸缩回（复位同一位）。</summary>
    public async Task StopUseAsync()
    {
        if (!Definition.IsCylinder)
        {
            return;
        }

        await _manual.WriteCoilAsync(Definition.Address, false).ConfigureAwait(false);
        IsActive = false;
    }
}
