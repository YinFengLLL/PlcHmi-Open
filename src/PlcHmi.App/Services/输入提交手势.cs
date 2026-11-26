using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace 上位机.应用.服务;

/// <summary>
/// "填完就写"的行：输入框失焦或按下回车时，由手势通知本行提交。
/// <para>由 <see cref="辅助地址行视图模型"/> 实现——它知道自己在改哪个地址、
/// 也知道当前是否允许写（手动使能）。</para>
/// </summary>
public interface 输入提交行
{
    /// <summary>操作员已进入 / 离开输入框（周期回读据此让路）。</summary>
    void 标记编辑中(bool 编辑中);

    /// <summary>提交这一次编辑（回车或失焦）。</summary>
    void 提交();
}

/// <summary>
/// 辅助地址输入框的「回车 / 失焦即提交」手势（2026-11-12 现场要求）。
///
/// <para><b>为什么不再要「写入」按钮：</b>一个辅助地址区往往有七八行，
/// 改完每行都要去点一次右边的「写入」，现场的实际做法是"改完就直接走"，
/// 于是界面显示的值和 PLC 里的值悄悄不一致。改成"填完即写"后，
/// 界面上看到的永远是 PLC 里真实的那份。</para>
///
/// <para><b>为什么不用 UpdateSourceTrigger=PropertyChanged 直接写：</b>
/// 那样每敲一个字符都会写一次 PLC（输入 12.5 会先写 1、再写 12）。
/// 数值是工艺参数，中间态不能落到设备里。</para>
///
/// <para><b>只在 TextBox 上挂</b>；勾选框走 <c>IsChecked</c> 变更即写（见行视图模型）。</para>
/// </summary>
public static class 输入提交手势
{
    /// <summary>是否挂上本手势。</summary>
    public static readonly DependencyProperty 附加Property = DependencyProperty.RegisterAttached(
        "附加", typeof(bool), typeof(输入提交手势), new PropertyMetadata(false, 附加已变更));

    /// <summary>内部标记：避免同一控件重复挂接（属性被重复设置时不会叠加处理器）。</summary>
    private static readonly DependencyProperty 已挂接Property = DependencyProperty.RegisterAttached(
        "已挂接", typeof(bool), typeof(输入提交手势), new PropertyMetadata(false));

    public static void Set附加(DependencyObject 元素, bool 值) => 元素.SetValue(附加Property, 值);

    public static bool Get附加(DependencyObject 元素) => (bool)元素.GetValue(附加Property);

    private static void 附加已变更(DependencyObject 元素, DependencyPropertyChangedEventArgs 事件参数)
    {
        if (元素 is not TextBox 输入框 || 事件参数.NewValue is not true)
        {
            return;
        }

        if ((bool)输入框.GetValue(已挂接Property))
        {
            return;
        }

        输入框.SetValue(已挂接Property, true);

        // DataContext 在事件发生时才解析：列表重建 / 虚拟化会换行对象，闭包里不能缓存它
        输入框.GotKeyboardFocus += (_, _) => 取行(输入框)?.标记编辑中(true);

        输入框.LostKeyboardFocus += (_, _) =>
        {
            if (取行(输入框) is not { } 行)
            {
                return;
            }

            行.标记编辑中(false);
            行.提交();
        };

        输入框.PreviewKeyDown += (_, 事件参数值) =>
        {
            if (事件参数值.Key != Key.Enter)
            {
                return;
            }

            事件参数值.Handled = true;

            if (取行(输入框) is not { } 行)
            {
                return;
            }

            行.提交();

            // 回车后把焦点交出去：停在原框里再输入会被下一次周期回读覆盖，
            // 也会让人误以为"还要再确认一次"
            输入框.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        };
    }

    private static 输入提交行? 取行(FrameworkElement 输入框) =>
        输入框.DataContext as 输入提交行;
}
