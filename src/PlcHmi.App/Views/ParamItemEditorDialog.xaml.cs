using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using PlcHmi.Core.Models;

namespace PlcHmi.App.Views;

/// <summary>
/// 参数页条目编辑器（延时 / 按钮共用）。
///
/// <para>地址区校验在服务层（延时必须 D 区、按钮必须 M 区），这里只做必填与数值解析。</para>
///
/// <para><b>变量绑定</b>：通过「选变量…」绑定变量后，地址与变量名同时写入；
/// 手工改动地址文本会自动解除绑定（防止“变量说 A、地址写 B”）。</para>
/// </summary>
public partial class ParameterEntryEditorDialog : Window
{
    private readonly ParameterTableCategory _category;
    private readonly ParameterEntryDefinition _entryItem;
    private readonly IReadOnlyList<VariableTableEntry>? _variableList;

    /// <summary>选变量后暂存的绑定名，确认时写回条目。</summary>
    private string _variable = string.Empty;

    private ParameterEntryEditorDialog(ParameterTableCategory Category, ParameterEntryDefinition Entry, IReadOnlyList<VariableTableEntry>? VariableList)
    {
        InitializeComponent();

        _category = Category;
        _entryItem = Entry;
        _variableList = VariableList;
        _variable = Entry.Variable;

        var isDelay = Category == ParameterTableCategory.Delay;

        PageHeaderText.Text = isDelay ? "编辑延时" : "编辑按钮";
        HintText.Text = isDelay
            ? "延时用于动作之间的等待，值保存在 PLC 的 D 区（32 位整数毫秒 / DINT，占 2 个字，低字在前）。"
              + "地址须落在 D1000–D1098 且与其它延时相隔 2（如 D1000 / D1002），否则会互相覆盖高位字。"
            : "按钮用于触发动作，绑定 M 区内部继电器，由 PLC 决定何时驱动输出。";

        AddressTag.Text = isDelay ? "地址（D 区）" : "地址（M 区）";

        NameFrame.Text = Entry.Name;
        AddressFrame.Text = Entry.Address;
        ValueFrame.Text = Entry.Value.ToString("0.###", CultureInfo.InvariantCulture);
        MinValueFrame.Text = Entry.Min.ToString("0.###", CultureInfo.InvariantCulture);
        MaxValueFrame.Text = Entry.Max.ToString("0.###", CultureInfo.InvariantCulture);
        EnabledFrame.IsChecked = Entry.Enabled;

        SingleBitFrame.ItemsSource = new[] { "ms", "s" };
        SingleBitFrame.SelectedItem = Entry.Unit;

        if (SingleBitFrame.SelectedIndex < 0)
        {
            SingleBitFrame.SelectedIndex = 0;
        }

        // 按钮没有"值"与"范围"的概念
        ValueFaceBoard.Visibility = isDelay ? Visibility.Visible : Visibility.Collapsed;
        RangeFaceBoard.Visibility = isDelay ? Visibility.Visible : Visibility.Collapsed;

        Height = isDelay ? 560 : 460;

        Loaded += (_, _) => NameFrame.Focus();
    }

    /// <summary>弹出编辑器。确定时已就地修改 <paramref name="Entry"/>。</summary>
    public static bool Show(ParameterTableCategory Category, ParameterEntryDefinition Entry, IReadOnlyList<VariableTableEntry>? VariableList = null)
    {
        var Dialog = new ParameterEntryEditorDialog(Category, Entry, VariableList)
        {
            Owner = Application.Current.MainWindow,
        };

        if (Dialog.ShowDialog() != true)
        {
            return false;
        }

        Entry.Name = Dialog.NameFrame.Text.Trim();
        Entry.Address = Dialog.AddressFrame.Text.Trim();
        Entry.Variable = Dialog._variable;
        Entry.Enabled = Dialog.EnabledFrame.IsChecked ?? false;
        Entry.Unit = Dialog.SingleBitFrame.SelectedItem as string ?? "ms";

        // 解析失败时保留原值；真正的范围校验在服务层
        Entry.Value = Parse(Dialog.ValueFrame.Text, Entry.Value);
        Entry.Min = Parse(Dialog.MinValueFrame.Text, Entry.Min);
        Entry.Max = Parse(Dialog.MaxValueFrame.Text, Entry.Max);

        return true;
    }

    /// <summary>仅供自检使用：实例化但不显示（用于验证 XAML 资源能否解析）。</summary>
    internal static Window CreateVerify(ParameterTableCategory Category, ParameterEntryDefinition Entry) =>
        new ParameterEntryEditorDialog(Category, Entry, null);

    // ── 变量绑定 ────────────────────────────────────────────────────────

    private void AddressSelectVariable(object sender, RoutedEventArgs eventArgs)
    {
        if (VariableSelectDialog.Show(_variableList ?? [], "为地址选择变量") is not { } Entry)
        {
            return;
        }

        _variable = Entry.VariableName;
        AddressFrame.Text = Entry.SoftElementAddress;
        BindHintText.Text = $"已绑定变量 {Entry.VariableName}（{Entry.DataType}）";
    }

    /// <summary>手工改动地址时解除变量绑定，避免“变量说 A、地址写 B”。</summary>
    private void AddressManuallyModified(object sender, TextChangedEventArgs eventArgs)
    {
        if (_variable.Length == 0 || AddressFrame is null)
        {
            return;
        }

        var Entry = _variableList?.FirstOrDefault(variableItem =>
            string.Equals(variableItem.VariableName, _variable, StringComparison.OrdinalIgnoreCase));

        if (Entry is null || !string.Equals(AddressFrame.Text.Trim(), Entry.SoftElementAddress, StringComparison.OrdinalIgnoreCase))
        {
            _variable = string.Empty;
            BindHintText.Text = string.Empty;
        }
    }

    private static double Parse(string Text, double fallback) =>
        double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var Value)
            ? Value
            : fallback;

    private void Confirm(object sender, RoutedEventArgs eventArgs)
    {
        if (string.IsNullOrWhiteSpace(NameFrame.Text))
        {
            MessageText.Text = "请填写名称。";
            return;
        }

        if (string.IsNullOrWhiteSpace(AddressFrame.Text))
        {
            MessageText.Text = "请填写地址。";
            return;
        }

        if (_category == ParameterTableCategory.Delay)
        {
            if (!TryRead(ValueFrame.Text, out _))
            {
                MessageText.Text = "当前值必须是数字。";
                return;
            }

            if (!TryRead(MinValueFrame.Text, out _) || !TryRead(MaxValueFrame.Text, out _))
            {
                MessageText.Text = "最小值与最大值必须是数字。";
                return;
            }
        }

        DialogResult = true;
    }

    private static bool TryRead(string Text, out double Value) =>
        double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out Value);

    private void Cancel(object sender, RoutedEventArgs eventArgs) => DialogResult = false;
}
