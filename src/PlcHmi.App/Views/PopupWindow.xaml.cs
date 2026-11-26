using System.ComponentModel;
using System.Windows;
using PlcHmi.App.Services;
using PlcHmi.App.ViewModels;
using PlcHmi.Business.Services;

namespace PlcHmi.App.Views;

/// <summary>
/// 可配置弹窗的显示窗口。
///
/// <para><b>为什么是独立窗口而不是主窗内的悬浮层：</b>悬浮层受主窗布局（行高、滚动、裁剪）摆布，
/// 现场实测出现"卡片被顶栏行裁掉、内容显示不全"。改用与<b>登录界面同一套窗口参数</b>
/// （固定宽度 + <c>SizeToContent="Height"</c> + <c>WindowStartupLocation="CenterOwner"</c>）
/// 后，内容多长窗口就多高，永远完整可见且居中——定位方式与登录界面完全一致。</para>
///
/// <para><b>关闭规则：</b>窗口只能由触发线圈复位来关闭（<c>Closing</c> 拦截）——
/// 它与 M 区是实时关系，操作员随手关掉会造成"弹窗被关、设备还停在提示状态"的假象。</para>
/// </summary>
public partial class PopupWindow : Window
{
    private readonly PopupHostViewModel _hostShell;

    private PopupWindow(PopupHostViewModel HostShell, PopupDefinition Definition)
    {
        InitializeComponent();

        _hostShell = HostShell;
        DataContext = Definition;

        // 复归型：按住 → 置位对应 M 线圈；松开 → 复位。与手动调试页的点动同一套手势。
        // 走即发即忘重载：失败必定留痕，不会以无人观察的 Task 触发"程序异常"红条。
        JogGesture.Additional(
            Button1,
            () => _hostShell.FireAndForgetPress(0),
            () => _hostShell.FireAndForgetRelease(0));
        JogGesture.Additional(
            Button2,
            () => _hostShell.FireAndForgetPress(1),
            () => _hostShell.FireAndForgetRelease(1));

        // 触发线圈仍闭合时不允许关闭：弹窗的显示/消失完全由 M 区实时决定
        Closing += (_, Parameter) =>
        {
            if (!_allowClose && _hostShell.CurrentPopup is not null)
            {
                Parameter.Cancel = true;
            }
        };

        // 主窗关闭（退出程序）时必须放行，否则残留窗口会拖住进程
        Loaded += (_, _) =>
        {
            if (Owner is Window masterWindow)
            {
                masterWindow.Closed += (_, _) => ForceClose();
            }
        };
    }

    /// <summary>允许关闭的标志：只在"程序退出 / 主窗已关"这种必须放行的场合置位。</summary>
    private bool _allowClose;

    /// <summary>无条件关闭（程序退出路径用）。</summary>
    internal void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    /// <summary>显示弹窗（由 弹窗宿主视图模型 调用；内容随触发切换实时更新）。</summary>
    internal static PopupWindow Create(PopupHostViewModel HostShell, PopupDefinition Definition) => new(HostShell, Definition);

    /// <summary>触发切换到另一个弹窗时换掉内容（无需重建窗口）。</summary>
    internal void Update(PopupDefinition Definition) => DataContext = Definition;

    /// <summary>仅供自检使用：实例化但不显示。</summary>
    internal static Window CreateVerify(PopupHostViewModel HostShell) =>
        Create(HostShell, new PopupDefinition
        {
            Name = "自检弹窗",
            TriggerAddress = "M0",
            Content = "自检用弹窗内容：验证布局与按钮排布。",
            Button1Name = "确认",
            Button1Address = "M0",
            Button2Name = "稍后处理",
            Button2Address = "M1",
        });
}
