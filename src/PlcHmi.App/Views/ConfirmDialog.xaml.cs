using System.Windows;

namespace PlcHmi.App.Views;

/// <summary>
/// 危险操作分级。颜色与确认按钮样式随级别变化，
/// 让操作员在按下前就能区分"普通操作"与"会写 PLC / 删数据"的操作。
/// </summary>
public enum ConfirmLevel
{
    /// <summary>普通操作（界面内可撤销）：主蓝。</summary>
    Neutral = 0,

    /// <summary>写 PLC / 断开重连：警示橙。</summary>
    Warning = 1,

    /// <summary>不可恢复（删数据 / 清历史）：危险红。</summary>
    Danger = 2,
}

/// <summary>
/// 操作确认对话框。
///
/// <para><b>安全设计（工业 HMI 防误触）：</b></para>
/// <list type="bullet">
/// <item>默认焦点在「取消」——回车 = 取消，执行必须显式点击；</item>
/// <item>ESC = 取消（<c>IsCancel</c>）；</item>
/// <item>确认按钮颜色随 <see cref="ConfirmLevel"/> 分级，文案写后果不复述按钮名。</item>
/// </list>
/// </summary>
public partial class ConfirmDialog : Window
{
    private ConfirmDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => CancelButton.Focus();
    }

    /// <summary>
    /// 弹出确认框。返回是否点了「确认」。
    /// </summary>
    /// <param name="title">标题（如「清除历史」）。</param>
    /// <param name="message">后果描述（写"会发生什么"，不复述按钮名）。</param>
    /// <param name="level">危险分级，决定图标与确认按钮配色。</param>
    /// <param name="confirmText">确认按钮文字（默认「确认执行」）。</param>
    /// <param name="detail">可选补充说明（影响范围 / 撤销方式）。</param>
    public static bool Show(
        string titleText,
        string Message,
        ConfirmLevel Level = ConfirmLevel.Neutral,
        string confirmText = "确认执行",
        string? issueDetail = null)
    {
        var Dialog = new ConfirmDialog
        {
            Owner = Application.Current.MainWindow,
            Title = titleText,
        };

        Dialog.TitleBarText.Text = titleText;
        Dialog.MessageText.Text = Message;

        if (string.IsNullOrWhiteSpace(issueDetail))
        {
            Dialog.DetailText.Visibility = Visibility.Collapsed;
        }
        else
        {
            Dialog.DetailText.Text = issueDetail;
            Dialog.DetailText.Visibility = Visibility.Visible;
        }

        Dialog.ConfirmButton.Content = confirmText;

        // 分级配色：图标盒软底色 + 图标描边 + 确认按钮样式
        switch (Level)
        {
            case ConfirmLevel.Danger:
                Dialog.IconFrame.Background = FindSoftBrush("DangerSoftBrush");
                Dialog.IconWordShape.Stroke = FindSoftBrush("DangerBrush");
                Dialog.ConfirmButton.Style = FindStyle(Dialog, "ButtonDanger");
                break;
            case ConfirmLevel.Warning:
                Dialog.IconFrame.Background = FindSoftBrush("WarningSoftBrush");
                Dialog.IconWordShape.Stroke = FindSoftBrush("WarningStrongBrush");
                Dialog.ConfirmButton.Style = FindStyle(Dialog, "ButtonWarning");
                break;
            default:
                Dialog.IconFrame.Background = FindSoftBrush("PrimarySoftBrush");
                Dialog.IconWordShape.Stroke = FindSoftBrush("PrimaryLightBrush");
                Dialog.ConfirmButton.Style = FindStyle(Dialog, "PrimaryButton");
                break;
        }

        return Dialog.ShowDialog() == true;
    }

    /// <summary>
    /// 仅供自检使用：实例化但不显示（XAML 的 StaticResource 写错键名会当场抛异常，
    /// 自检 new 一次可把"只在打开对话框时才炸"的 bug 提前暴露）。
    /// </summary>
    internal static Window CreateVerify() => new ConfirmDialog();

    private void Confirm(object sender, RoutedEventArgs eventArgs) => DialogResult = true;

    private static System.Windows.Media.Brush FindSoftBrush(string keyValue)
    {
        // TryFindResource + 回退：键缺失或 Application.Current 为 null（单测 / 自检）时
        // 不再抛异常——全站其它查找均为容错口径，这里是漏改的一处
        if (Application.Current?.TryFindResource(keyValue) is System.Windows.Media.Brush brush)
        {
            return brush;
        }

        return System.Windows.Media.Brushes.Gray;
    }

    /// <summary>查找按钮样式；键缺失时返回 null（保持模板默认样式），不抛异常。</summary>
    private static Style? FindStyle(System.Windows.FrameworkElement ElementItem, string keyValue) =>
        ElementItem.TryFindResource(keyValue) as Style;
}
