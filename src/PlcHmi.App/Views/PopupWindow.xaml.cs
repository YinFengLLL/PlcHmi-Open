using System.ComponentModel;
using System.Windows;
using 上位机.应用.服务;
using 上位机.应用.视图模型;
using 上位机.业务.服务;

namespace 上位机.应用.视图;

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
public partial class 弹窗窗口 : Window
{
    private readonly 弹窗宿主视图模型 _宿主;

    private 弹窗窗口(弹窗宿主视图模型 宿主, 弹窗定义 定义)
    {
        InitializeComponent();

        _宿主 = 宿主;
        DataContext = 定义;

        // 复归型：按住 → 置位对应 M 线圈；松开 → 复位。与手动调试页的点动同一套手势。
        // 走即发即忘重载：失败必定留痕，不会以无人观察的 Task 触发"程序异常"红条。
        点动手势.附加(
            按钮一,
            () => _宿主.即发即忘按下(0),
            () => _宿主.即发即忘松开(0));
        点动手势.附加(
            按钮二,
            () => _宿主.即发即忘按下(1),
            () => _宿主.即发即忘松开(1));

        // 触发线圈仍闭合时不允许关闭：弹窗的显示/消失完全由 M 区实时决定
        Closing += (_, 参数) =>
        {
            if (!_允许关闭 && _宿主.当前弹窗 is not null)
            {
                参数.Cancel = true;
            }
        };

        // 主窗关闭（退出程序）时必须放行，否则残留窗口会拖住进程
        Loaded += (_, _) =>
        {
            if (Owner is Window 主窗)
            {
                主窗.Closed += (_, _) => 强制关闭();
            }
        };
    }

    /// <summary>允许关闭的标志：只在"程序退出 / 主窗已关"这种必须放行的场合置位。</summary>
    private bool _允许关闭;

    /// <summary>无条件关闭（程序退出路径用）。</summary>
    internal void 强制关闭()
    {
        _允许关闭 = true;
        Close();
    }

    /// <summary>显示弹窗（由 弹窗宿主视图模型 调用；内容随触发切换实时更新）。</summary>
    internal static 弹窗窗口 创建(弹窗宿主视图模型 宿主, 弹窗定义 定义) => new(宿主, 定义);

    /// <summary>触发切换到另一个弹窗时换掉内容（无需重建窗口）。</summary>
    internal void 更新(弹窗定义 定义) => DataContext = 定义;

    /// <summary>仅供自检使用：实例化但不显示。</summary>
    internal static Window 创建验证(弹窗宿主视图模型 宿主) =>
        创建(宿主, new 弹窗定义
        {
            名称 = "自检弹窗",
            触发地址 = "M0",
            内容 = "自检用弹窗内容：验证布局与按钮排布。",
            按钮一名称 = "确认",
            按钮一地址 = "M0",
            按钮二名称 = "稍后处理",
            按钮二地址 = "M1",
        });
}
