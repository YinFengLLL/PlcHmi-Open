using System.Windows;

namespace 上位机.应用.视图;

/// <summary>
/// 危险操作分级。颜色与确认按钮样式随级别变化，
/// 让操作员在按下前就能区分"普通操作"与"会写 PLC / 删数据"的操作。
/// </summary>
public enum 确认等级
{
    /// <summary>普通操作（界面内可撤销）：主蓝。</summary>
    中性 = 0,

    /// <summary>写 PLC / 断开重连：警示橙。</summary>
    警告 = 1,

    /// <summary>不可恢复（删数据 / 清历史）：危险红。</summary>
    危险 = 2,
}

/// <summary>
/// 操作确认对话框。
///
/// <para><b>安全设计（工业 HMI 防误触）：</b></para>
/// <list type="bullet">
/// <item>默认焦点在「取消」——回车 = 取消，执行必须显式点击；</item>
/// <item>ESC = 取消（<c>IsCancel</c>）；</item>
/// <item>确认按钮颜色随 <see cref="确认等级"/> 分级，文案写后果不复述按钮名。</item>
/// </list>
/// </summary>
public partial class 确认对话框 : Window
{
    private 确认对话框()
    {
        InitializeComponent();
        Loaded += (_, _) => 取消按钮.Focus();
    }

    /// <summary>
    /// 弹出确认框。返回是否点了「确认」。
    /// </summary>
    /// <param name="title">标题（如「清除历史」）。</param>
    /// <param name="message">后果描述（写"会发生什么"，不复述按钮名）。</param>
    /// <param name="level">危险分级，决定图标与确认按钮配色。</param>
    /// <param name="confirmText">确认按钮文字（默认「确认执行」）。</param>
    /// <param name="detail">可选补充说明（影响范围 / 撤销方式）。</param>
    public static bool 显示(
        string 标题文本,
        string 消息,
        确认等级 等级 = 确认等级.中性,
        string 确认文本 = "确认执行",
        string? 问题详情 = null)
    {
        var 对话框 = new 确认对话框
        {
            Owner = Application.Current.MainWindow,
            Title = 标题文本,
        };

        对话框.标题栏文本.Text = 标题文本;
        对话框.消息文本.Text = 消息;

        if (string.IsNullOrWhiteSpace(问题详情))
        {
            对话框.详情文本.Visibility = Visibility.Collapsed;
        }
        else
        {
            对话框.详情文本.Text = 问题详情;
            对话框.详情文本.Visibility = Visibility.Visible;
        }

        对话框.确认按钮.Content = 确认文本;

        // 分级配色：图标盒软底色 + 图标描边 + 确认按钮样式
        switch (等级)
        {
            case 确认等级.危险:
                对话框.图标框.Background = 查找软画刷("DangerSoftBrush");
                对话框.图标字形.Stroke = 查找软画刷("DangerBrush");
                对话框.确认按钮.Style = 查找样式(对话框, "ButtonDanger");
                break;
            case 确认等级.警告:
                对话框.图标框.Background = 查找软画刷("WarningSoftBrush");
                对话框.图标字形.Stroke = 查找软画刷("WarningStrongBrush");
                对话框.确认按钮.Style = 查找样式(对话框, "ButtonWarning");
                break;
            default:
                对话框.图标框.Background = 查找软画刷("PrimarySoftBrush");
                对话框.图标字形.Stroke = 查找软画刷("PrimaryLightBrush");
                对话框.确认按钮.Style = 查找样式(对话框, "PrimaryButton");
                break;
        }

        return 对话框.ShowDialog() == true;
    }

    /// <summary>
    /// 仅供自检使用：实例化但不显示（XAML 的 StaticResource 写错键名会当场抛异常，
    /// 自检 new 一次可把"只在打开对话框时才炸"的 bug 提前暴露）。
    /// </summary>
    internal static Window 创建验证() => new 确认对话框();

    private void 确认(object 发送者, RoutedEventArgs 事件参数) => DialogResult = true;

    private static System.Windows.Media.Brush 查找软画刷(string 键值)
    {
        // TryFindResource + 回退：键缺失或 Application.Current 为 null（单测 / 自检）时
        // 不再抛异常——全站其它查找均为容错口径，这里是漏改的一处
        if (Application.Current?.TryFindResource(键值) is System.Windows.Media.Brush 画刷)
        {
            return 画刷;
        }

        return System.Windows.Media.Brushes.Gray;
    }

    /// <summary>查找按钮样式；键缺失时返回 null（保持模板默认样式），不抛异常。</summary>
    private static Style? 查找样式(System.Windows.FrameworkElement 元素, string 键值) =>
        元素.TryFindResource(键值) as Style;
}
