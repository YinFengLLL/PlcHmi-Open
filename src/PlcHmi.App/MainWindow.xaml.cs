using System.Windows;
using System.Windows.Controls;
using PlcHmi.App.Services;
using PlcHmi.App.ViewModels;
using PlcHmi.Business.Configuration;

namespace PlcHmi.App;

/// <summary>
/// 主窗口。顶栏、侧栏导航、内容区与底部报警条均绑定 <see cref="MasterViewModel"/>。
/// <para>窗口实例由依赖注入容器创建，因此构造函数直接接收 ViewModel 并设为 DataContext。</para>
/// </summary>
public partial class MasterWindow : Window
{
    /// <summary>构造函数注入。</summary>
    public MasterWindow(MasterViewModel viewModel, UiOptions UiOptions)
    {
        InitializeComponent();
        DataContext = viewModel;

        // 应用配置的主题（深/浅）：整体替换调色板字典，DynamicResource 即时刷新。
        // 放主窗口构造而非 App.xaml.cs —— 后者已在规模门禁基线上，不允许再增行；
        // 此处在窗口 Show 之前执行，首帧即为目标主题，无闪变。
        ThemeManager.App(UiOptions.Theme);

        // ── 最大化内容补偿 ─────────────────────────────────────────────
        // WindowStyle=None + WindowChrome 最大化时，窗口矩形会向四周外扩一个
        // 系统非客户区边框量（含贴边缩放余量），内容随之越出屏幕 ——
        // 表现为右上角 X 键、底栏被裁掉一截。把根容器整体内收同量补偿。
        StateChanged += (_, _) => ApplyMaximizeMarginCompensation();
        ApplyMaximizeMarginCompensation();

        UiSelfCheckWhenCloseInFieldMoveEffect();
    }

    /// <summary>最大化时把根容器内收系统非客户区边框量；还原/最小化时归零。</summary>
    private void ApplyMaximizeMarginCompensation() =>
        RootContainer.Margin = WindowState == WindowState.Maximized
            ? SystemParameters.WindowNonClientFrameThickness
            : default(Thickness);

    // ── 自绘标题栏的窗口控制 ───────────────────────────────────────────
    // WindowStyle="None" 之后系统标题栏整条消失，最小化/最大化/关闭三个键
    // 必须自己接上，否则窗口关不掉 —— 这不是"样式问题"，是可用性问题。

    private void Minimize_Click(object sender, RoutedEventArgs eventArgs) =>
        WindowState = WindowState.Minimized;

    private void MaximizeOrRestore_Click(object sender, RoutedEventArgs eventArgs) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs eventArgs) => Close();

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

    private static readonly string[] selfCheckParameter = ["--smoke", "--shots", "--export-csv"];

    private static void UiSelfCheckWhenCloseInFieldMoveEffect()
    {
        var commandRow = Environment.GetCommandLineArgs();

        if (!selfCheckParameter.Any(name => commandRow.Contains(name)))
        {
            return;
        }

        if (Application.Current is { } App)
        {
            App.Resources["PageTransitionStyle"] = new Style(typeof(FrameworkElement));

            // 卡片 stagger 入场同理：换成"只有 MetricCard 外观、没有触发器"的样式，
            // 否则截图会落在逐卡淡入的中途（末卡 0.37s 才播完）
            var cardBaseSample = (Style)App.Resources["MetricCard"];
            App.Resources["CardEnterStyle1"] = new Style(typeof(Border), cardBaseSample);
            App.Resources["CardEnterStyle2"] = new Style(typeof(Border), cardBaseSample);
            App.Resources["CardEnterStyle3"] = new Style(typeof(Border), cardBaseSample);
        }
    }
}
