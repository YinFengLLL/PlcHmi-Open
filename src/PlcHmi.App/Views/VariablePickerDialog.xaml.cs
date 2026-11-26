using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PlcHmi.Core.Models;

namespace PlcHmi.App.Views;

/// <summary>
/// 变量表选择对话框。按变量名 / 地址关键字过滤，供各配置编辑界面选取变量。
///
/// <para><b>选定语义</b>：返回选中的 <see cref="VariableTableEntry"/>；调用方负责用它的
/// 软元件地址与数据类型回填各自字段。</para>
/// </summary>
public partial class VariableSelectDialog : Window
{
    private readonly IReadOnlyList<VariableTableEntry> _allEntry;

    private VariableSelectDialog(IReadOnlyList<VariableTableEntry> EntryCollection, string titleText)
    {
        InitializeComponent();

        _allEntry = EntryCollection;
        PageHeaderText.Text = titleText;
        Title = titleText;

        VariableList.ItemsSource = _allEntry;
        Loaded += (_, _) => SearchFrame.Focus();
    }

    /// <summary>弹出选择器。返回选中的变量条目；取消返回 <c>null</c>。</summary>
    public static VariableTableEntry? Show(IReadOnlyList<VariableTableEntry> EntryCollection, string titleText)
    {
        if (EntryCollection.Count == 0)
        {
            System.Windows.MessageBox.Show(
                "变量表为空。请先在「系统设置」导入汇川变量表 CSV。",
                "无可用变量", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        var Dialog = new VariableSelectDialog(EntryCollection, titleText)
        {
            Owner = Application.Current.MainWindow,
        };

        return Dialog.ShowDialog() == true ? Dialog.CurrentSelected : null;
    }

    /// <summary>当前选中的变量条目。</summary>
    public VariableTableEntry? CurrentSelected => VariableList.SelectedItem as VariableTableEntry;

    /// <summary>仅供自检使用：实例化但不显示（用于验证 XAML 资源能否解析）。</summary>
    internal static Window CreateVerify(IReadOnlyList<VariableTableEntry> EntryCollection) =>
        new VariableSelectDialog(EntryCollection, "从变量表选择变量");

    private void SearchTextChanged(object sender, TextChangedEventArgs eventArgs)
    {
        if (VariableList is null)
        {
            return;   // 初始化过程中控件尚未就绪
        }

        var keyword = SearchFrame.Text.Trim();

        VariableList.ItemsSource = keyword.Length == 0
            ? _allEntry
            : _allEntry.Where(Entry =>
                    Entry.VariableName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    Entry.SoftElementAddress.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .ToList();
    }

    private void Confirm(object sender, RoutedEventArgs eventArgs)
    {
        if (CurrentSelected is null)
        {
            System.Windows.MessageBox.Show("请先在列表中选择一个变量。", "未选择",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }

    private void Cancel(object sender, RoutedEventArgs eventArgs) => DialogResult = false;

    private void ListDoubleHit(object sender, MouseButtonEventArgs eventArgs)
    {
        if (CurrentSelected is not null)
        {
            DialogResult = true;
        }
    }
}
