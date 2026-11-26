using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using 上位机.核心.模型;

namespace 上位机.应用.视图;

/// <summary>
/// 变量表选择对话框。按变量名 / 地址关键字过滤，供各配置编辑界面选取变量。
///
/// <para><b>选定语义</b>：返回选中的 <see cref="变量表条目"/>；调用方负责用它的
/// 软元件地址与数据类型回填各自字段。</para>
/// </summary>
public partial class 变量选择对话框 : Window
{
    private readonly IReadOnlyList<变量表条目> _全部条目;

    private 变量选择对话框(IReadOnlyList<变量表条目> 条目集合, string 标题文本)
    {
        InitializeComponent();

        _全部条目 = 条目集合;
        页眉文本.Text = 标题文本;
        Title = 标题文本;

        变量列表.ItemsSource = _全部条目;
        Loaded += (_, _) => 搜索框.Focus();
    }

    /// <summary>弹出选择器。返回选中的变量条目；取消返回 <c>null</c>。</summary>
    public static 变量表条目? 显示(IReadOnlyList<变量表条目> 条目集合, string 标题文本)
    {
        if (条目集合.Count == 0)
        {
            System.Windows.MessageBox.Show(
                "变量表为空。请先在「系统设置」导入汇川变量表 CSV。",
                "无可用变量", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        var 对话框 = new 变量选择对话框(条目集合, 标题文本)
        {
            Owner = Application.Current.MainWindow,
        };

        return 对话框.ShowDialog() == true ? 对话框.当前选中 : null;
    }

    /// <summary>当前选中的变量条目。</summary>
    public 变量表条目? 当前选中 => 变量列表.SelectedItem as 变量表条目;

    /// <summary>仅供自检使用：实例化但不显示（用于验证 XAML 资源能否解析）。</summary>
    internal static Window 创建验证(IReadOnlyList<变量表条目> 条目集合) =>
        new 变量选择对话框(条目集合, "从变量表选择变量");

    private void 搜索文本已变更(object 发送者, TextChangedEventArgs 事件参数)
    {
        if (变量列表 is null)
        {
            return;   // 初始化过程中控件尚未就绪
        }

        var 关键字 = 搜索框.Text.Trim();

        变量列表.ItemsSource = 关键字.Length == 0
            ? _全部条目
            : _全部条目.Where(条目 =>
                    条目.变量名.Contains(关键字, StringComparison.OrdinalIgnoreCase) ||
                    条目.软元件地址.Contains(关键字, StringComparison.OrdinalIgnoreCase))
                .ToList();
    }

    private void 确认(object 发送者, RoutedEventArgs 事件参数)
    {
        if (当前选中 is null)
        {
            System.Windows.MessageBox.Show("请先在列表中选择一个变量。", "未选择",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }

    private void 取消(object 发送者, RoutedEventArgs 事件参数) => DialogResult = false;

    private void 列表双击(object 发送者, MouseButtonEventArgs 事件参数)
    {
        if (当前选中 is not null)
        {
            DialogResult = true;
        }
    }
}
