using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace 上位机.应用.服务;

/// <summary>
/// 「按住才动、松开即停」手势助手。
///
/// <para><b>为什么不能只绑按钮自己的 MouseUp</b>：按下后把鼠标拖出按钮、甚至拖到窗口外再松开，
/// 按钮收不到 MouseUp —— 轴会一直动下去。这是<b>安全事故</b>，不是体验问题。</para>
///
/// <para>因此挂四处兜底，任一触发都立即停止：</para>
/// <list type="number">
///   <item>按钮自身收到 MouseUp（正常情况）；</item>
///   <item>按钮失去鼠标捕获（拖出后系统夺走捕获）；</item>
///   <item>鼠标在窗口内任意位置抬起（窗口级 PreviewMouseUp）；</item>
///   <item>窗口失去激活（Alt+Tab 切走、被弹窗抢焦点）。</item>
/// </list>
///
/// <para>按下时用 <see cref="Mouse.Capture(IInputElement, CaptureMode)"/> 捕获鼠标，
/// 这样即使指针移出按钮，MouseUp 仍会回到本控件。</para>
///
/// <para><b>窗口级处理器必须在 Unloaded 时解绑</b>：页面视图每次导航都会被重建
/// （DataTemplate 重新实例化），而窗口是长生命周期的；不解绑就会累积一批指向已废弃按钮的
/// 处理器——既是内存泄漏，也会让一次松手触发多次停止。</para>
///
/// <para>本类只负责"手势"，异步与异常由调用方（ViewModel）自行兜底，避免在这里再引一个日志器。</para>
/// </summary>
internal static class 点动手势
{
    /// <summary>
    /// 把「按住才动、松开即停」挂到方向按钮上。
    /// </summary>
    /// <param name="button">方向按钮。<b>不要给它绑 Command</b> —— 本助手自己管按下与松开。</param>
    /// <param name="start">按下时执行（启动点动）。</param>
    /// <param name="stop">松开或任一兜底触发时执行（停止点动，必须无条件生效）。</param>
    public static void 附加(Button 按钮项, Action 启动, Action 停止动作)
    {
        ArgumentNullException.ThrowIfNull(按钮项);
        ArgumentNullException.ThrowIfNull(启动);
        ArgumentNullException.ThrowIfNull(停止动作);

        var 活动 = false;
        Window? 主机 = null;

        void 停止()
        {
            if (!活动)
            {
                return;
            }

            活动 = false;

            if (ReferenceEquals(Mouse.Captured, 按钮项))
            {
                Mouse.Capture(null);
            }

            停止动作();
        }

        MouseButtonEventHandler 窗口鼠标抬起 = (_, _) => 停止();
        EventHandler 窗口停用时 = (_, _) => 停止();

        按钮项.PreviewMouseLeftButtonDown += (_, _) =>
        {
            if (活动 || !按钮项.IsEnabled)
            {
                return;
            }

            // 刻意不设 e.Handled：保留按钮自带的按下视觉反馈
            活动 = true;
            Mouse.Capture(按钮项, CaptureMode.Element);
            启动();
        };

        按钮项.PreviewMouseLeftButtonUp += (_, _) => 停止();
        按钮项.LostMouseCapture += (_, _) => 停止();

        按钮项.Loaded += (_, _) =>
        {
            主机 = Window.GetWindow(按钮项);

            if (主机 is null)
            {
                return;
            }

            主机.PreviewMouseUp += 窗口鼠标抬起;
            主机.Deactivated += 窗口停用时;
        };

        按钮项.Unloaded += (_, _) =>
        {
            停止();

            if (主机 is null)
            {
                return;
            }

            主机.PreviewMouseUp -= 窗口鼠标抬起;
            主机.Deactivated -= 窗口停用时;
            主机 = null;
        };
    }
}
