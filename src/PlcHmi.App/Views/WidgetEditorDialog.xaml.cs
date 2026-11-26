using System.Windows;
using System.Windows.Controls;
using PlcHmi.Core.Models;

namespace PlcHmi.App.Views;

/// <summary>
/// 手动页元件编辑器。
///
/// <para>只负责收集界面输入并写回传入的对象；<b>地址是否为 M 区的校验在服务层</b>——
/// 界面提示只是辅助，不当最后一道防线。</para>
///
/// <para><b>变量绑定</b>：通过「选变量…」从变量表选取变量后，地址与变量名同时写入；
/// 若之后手工改动地址文本，绑定自动解除（防止“变量说 A、地址写 B”的漂移）。</para>
/// </summary>
public partial class ComponentEditorDialog : Window
{
    private readonly ManualComponentDefinition _componentItem;
    private readonly IReadOnlyList<VariableTableEntry>? _variableList;

    /// <summary>选变量后暂存的绑定名，确认时写回元件。</summary>
    private string _controlVariable = string.Empty;
    private string _stateVariable = string.Empty;

    private ComponentEditorDialog(ManualComponentDefinition ComponentItem, IReadOnlyList<VariableTableEntry>? VariableList)
    {
        InitializeComponent();

        _componentItem = ComponentItem;
        _variableList = VariableList;
        _controlVariable = ComponentItem.Variable;
        _stateVariable = ComponentItem.StateValueVariable;

        TypeFrame.ItemsSource = new[] { "气缸（伸出 / 缩回）", "按钮（脉冲触发）", "指示灯（只读监测）" };
        TypeFrame.SelectedIndex = (int)ComponentItem.Kind;

        NameFrame.Text = ComponentItem.Name;
        ExtendFrame.Text = ComponentItem.Address;
        StateFrame.Text = ComponentItem.StateAddress;

        UpdateFieldVisibility();
    }

    /// <summary>弹出编辑器。返回是否确定（确定时已就地修改 <paramref name="ComponentItem"/>）。</summary>
    public static bool Show(ManualComponentDefinition ComponentItem, IReadOnlyList<VariableTableEntry>? VariableList = null)
    {
        var Dialog = new ComponentEditorDialog(ComponentItem, VariableList)
        {
            Owner = Application.Current.MainWindow,
        };

        if (Dialog.ShowDialog() != true)
        {
            return false;
        }

        ComponentItem.Kind = (ManualComponentType)Dialog.TypeFrame.SelectedIndex;
        ComponentItem.Name = Dialog.NameFrame.Text.Trim();
        ComponentItem.Address = Dialog.ExtendFrame.Text.Trim();
        ComponentItem.StateAddress = Dialog.StateFrame.Text.Trim();
        ComponentItem.Variable = Dialog._controlVariable;
        ComponentItem.StateValueVariable = Dialog._stateVariable;

        return true;
    }

    /// <summary>仅供自检使用：实例化但不显示（用于验证 XAML 资源能否解析）。</summary>
    internal static Window CreateVerify(ManualComponentDefinition ComponentItem) =>
        new ComponentEditorDialog(ComponentItem, null);

    /// <summary>按类型切换需要填的字段。</summary>
    private void TypeChanged(object sender, SelectionChangedEventArgs eventArgs) => UpdateFieldVisibility();

    private void UpdateFieldVisibility()
    {
        if (ExtendFaceBoard is null || StateFaceBoard is null)
        {
            return;   // 初始化过程中控件尚未就绪
        }

        var type = (ManualComponentType)Math.Max(0, TypeFrame.SelectedIndex);

        StateFaceBoard.Visibility = type == ManualComponentType.Indicator ? Visibility.Visible : Visibility.Collapsed;
        ExtendFaceBoard.Visibility = type == ManualComponentType.Indicator ? Visibility.Collapsed : Visibility.Visible;

        switch (type)
        {
            case ManualComponentType.Cylinder:
                ExtendTag.Text = "控制地址（M 区）";
                ExtendHint.Text = "单线圈气缸：置位该位 = 伸出，复位 = 缩回。";
                break;

            case ManualComponentType.Button:
                ExtendTag.Text = "触发地址（M 区）";
                ExtendHint.Text = "按下时该位发出一个短脉冲。";
                break;

            default:
                ExtendHint.Text = string.Empty;
                break;
        }

        MessageText.Text = string.Empty;
    }

    // ── 变量绑定 ────────────────────────────────────────────────────────

    private void ExtendAddressSelectVariable(object sender, RoutedEventArgs eventArgs)
    {
        if (VariableSelectDialog.Show(_variableList ?? [], "为控制地址选择变量") is not { } Entry)
        {
            return;
        }

        _controlVariable = Entry.VariableName;
        ExtendFrame.Text = Entry.SoftElementAddress;
        ExtendHint.Text = $"已绑定变量 {Entry.VariableName}（{Entry.DataType}）";
    }

    private void StateAddressSelectVariable(object sender, RoutedEventArgs eventArgs)
    {
        if (VariableSelectDialog.Show(_variableList ?? [], "为监测地址选择变量") is not { } Entry)
        {
            return;
        }

        _stateVariable = Entry.VariableName;
        StateFrame.Text = Entry.SoftElementAddress;
    }

    /// <summary>手工改动地址时解除变量绑定，避免“变量说 A、地址写 B”。</summary>
    private void AddressManuallyModified(object sender, TextChangedEventArgs eventArgs)
    {
        if (_controlVariable.Length == 0 || ExtendFrame is null)
        {
            return;
        }

        var Entry = _variableList?.FirstOrDefault(variableItem =>
            string.Equals(variableItem.VariableName, _controlVariable, StringComparison.OrdinalIgnoreCase));

        if (Entry is null || !string.Equals(ExtendFrame.Text.Trim(), Entry.SoftElementAddress, StringComparison.OrdinalIgnoreCase))
        {
            _controlVariable = string.Empty;
        }
    }

    /// <summary>手工改动监测地址时解除变量绑定（同上）。</summary>
    private void StateAddressManuallyModified(object sender, TextChangedEventArgs eventArgs)
    {
        if (_stateVariable.Length == 0 || StateFrame is null)
        {
            return;
        }

        var Entry = _variableList?.FirstOrDefault(variableItem =>
            string.Equals(variableItem.VariableName, _stateVariable, StringComparison.OrdinalIgnoreCase));

        if (Entry is null || !string.Equals(StateFrame.Text.Trim(), Entry.SoftElementAddress, StringComparison.OrdinalIgnoreCase))
        {
            _stateVariable = string.Empty;
        }
    }

    private void Confirm(object sender, RoutedEventArgs eventArgs)
    {
        if (string.IsNullOrWhiteSpace(NameFrame.Text))
        {
            MessageText.Text = "请填写显示名称。";
            return;
        }

        var type = (ManualComponentType)Math.Max(0, TypeFrame.SelectedIndex);

        if (type != ManualComponentType.Indicator && string.IsNullOrWhiteSpace(ExtendFrame.Text))
        {
            MessageText.Text = type == ManualComponentType.Button ? "请填写触发地址。" : "请填写控制地址。";
            return;
        }

        if (type == ManualComponentType.Indicator && string.IsNullOrWhiteSpace(StateFrame.Text))
        {
            MessageText.Text = "请填写监测地址。";
            return;
        }

        DialogResult = true;
    }

    private void Cancel(object sender, RoutedEventArgs eventArgs) => DialogResult = false;
}
