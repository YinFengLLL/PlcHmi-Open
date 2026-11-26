using System.Windows;
using System.Windows.Controls;
using 上位机.应用.服务;
using 上位机.应用.视图模型;
using 上位机.业务.配置;

namespace 上位机.应用;

/// <summary>
/// 主窗口。顶栏、侧栏导航、内容区与底部报警条均绑定 <see cref="主视图模型"/>。
/// <para>窗口实例由依赖注入容器创建，因此构造函数直接接收 ViewModel 并设为 DataContext。</para>
/// </summary>
public partial class 主窗口 : Window
{
    /// <summary>构造函数注入。</summary>
    public 主窗口(主视图模型 视图模型, 界面选项 界面选项)
    {
        InitializeComponent();
        DataContext = 视图模型;

        // 应用配置的主题（深/浅）：整体替换调色板字典，DynamicResource 即时刷新。
        // 放主窗口构造而非 App.xaml.cs —— 后者已在规模门禁基线上，不允许再增行；
        // 此处在窗口 Show 之前执行，首帧即为目标主题，无闪变。
        主题管理器.应用(界面选项.主题);

        // ── 最大化内容补偿 ─────────────────────────────────────────────
        // WindowStyle=None + WindowChrome 最大化时，窗口矩形会向四周外扩一个
        // 系统非客户区边框量（含贴边缩放余量），内容随之越出屏幕 ——
        // 表现为右上角 X 键、底栏被裁掉一截。把根容器整体内收同量补偿。
        StateChanged += (_, _) => 应用最大化边距补偿();
        应用最大化边距补偿();

        界面自检时关闭入场动效();
    }

    /// <summary>最大化时把根容器内收系统非客户区边框量；还原/最小化时归零。</summary>
    private void 应用最大化边距补偿() =>
        根容器.Margin = WindowState == WindowState.Maximized
            ? SystemParameters.WindowNonClientFrameThickness
            : default(Thickness);

    // ── 自绘标题栏的窗口控制 ───────────────────────────────────────────
    // WindowStyle="None" 之后系统标题栏整条消失，最小化/最大化/关闭三个键
    // 必须自己接上，否则窗口关不掉 —— 这不是"样式问题"，是可用性问题。

    private void 最小化_Click(object 发送者, RoutedEventArgs 事件参数) =>
        WindowState = WindowState.Minimized;

    private void 最大化或还原_Click(object 发送者, RoutedEventArgs 事件参数) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void 关闭_Click(object 发送者, RoutedEventArgs 事件参数) => Close();

    // ── 界面自检模式关闭入场动效 ───────────────────────────────────────
    //
    // --shots 用 RenderTargetBitmap 离屏渲染，而渲染发生在页面 Loaded 之后
    // 同一个 Dispatcher 批次之内 —— 动画时钟还没走过第一帧，
    // 页面入场的 0.22 秒里首帧 Opacity=0，截图会得到 8 张接近全白的图。
    // 而且这是**静默失败**：退出码 0、文件齐全，只有人眼看得出不对。
    //
    // 所以自检模式下把 PageTransitionStyle 整体替换为空样式。
    // 这也是各页必须用 DynamicResource 引用它的原因：StaticResource 换不掉。
    //
    // 为什么写在这里而不是 App.xaml.cs —— 后者已在代码规模门禁的基线上
    // （tools/check_thresholds.py，727 行），不允许再增行。

    private static readonly string[] 自检参数 = ["--smoke", "--shots", "--export-csv"];

    private static void 界面自检时关闭入场动效()
    {
        var 命令行 = Environment.GetCommandLineArgs();

        if (!自检参数.Any(名 => 命令行.Contains(名)))
        {
            return;
        }

        if (Application.Current is { } 应用)
        {
            应用.Resources["PageTransitionStyle"] = new Style(typeof(FrameworkElement));

            // 卡片 stagger 入场同理：换成"只有 MetricCard 外观、没有触发器"的样式，
            // 否则截图会落在逐卡淡入的中途（末卡 0.37s 才播完）
            var 卡片基样 = (Style)应用.Resources["MetricCard"];
            应用.Resources["CardEnterStyle1"] = new Style(typeof(Border), 卡片基样);
            应用.Resources["CardEnterStyle2"] = new Style(typeof(Border), 卡片基样);
            应用.Resources["CardEnterStyle3"] = new Style(typeof(Border), 卡片基样);
        }
    }
}
