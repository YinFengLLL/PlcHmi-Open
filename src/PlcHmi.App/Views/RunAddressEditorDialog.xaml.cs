using System.Windows;
using 上位机.核心.抽象;
using 上位机.核心.模型;

namespace 上位机.应用.视图;

/// <summary>
/// 运行监控页元件地址编辑器。
///
/// <para>只负责收集界面输入并就地写回传入列表的 <see cref="运行监控元件.地址"/>；
/// 地址合法性（D/M 区、重叠、保留区）的校验在服务层——界面提示只是辅助，
/// 不当最后一道防线。</para>
/// </summary>
public partial class 运行监控地址编辑对话框 : Window
{
    private readonly IReadOnlyList<运行监控元件> _元件列表;

    private 运行监控地址编辑对话框(IReadOnlyList<运行监控元件> 元件列表)
    {
        InitializeComponent();

        _元件列表 = 元件列表;
        元件列表控件.ItemsSource = _元件列表;
    }

    /// <summary>弹出编辑器。返回是否确定（确定时已就地修改传入列表的地址）。</summary>
    public static bool 显示(IReadOnlyList<运行监控元件> 元件列表)
    {
        var 对话框 = new 运行监控地址编辑对话框(元件列表)
        {
            Owner = Application.Current.MainWindow,
        };

        return 对话框.ShowDialog() == true;
    }

    /// <summary>仅供自检使用：实例化但不显示（用于验证 XAML 资源能否解析）。</summary>
    internal static Window 创建验证(IReadOnlyList<运行监控元件> 元件列表) =>
        new 运行监控地址编辑对话框(元件列表);

    private void 确认(object 发送者, RoutedEventArgs 事件参数)
    {
        var 空缺 = _元件列表.FirstOrDefault(条目项 => string.IsNullOrWhiteSpace(条目项.地址));
        if (空缺 is not null)
        {
            消息文本.Text = $"「{空缺.显示名称}」的地址不能为空。";
            return;
        }

        DialogResult = true;
    }

    private void 取消(object 发送者, RoutedEventArgs 事件参数) => DialogResult = false;
}
