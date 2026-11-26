using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace 上位机.应用.视图;

/// <summary>
/// 轻量柱状图（自绘，无第三方图表库）。
///
/// <para><b>为什么不用 LiveCharts 等库：</b>只需要"一排柱子 + 刻度 + 悬停数值"，
/// 引整包图表库徒增体积与样式打架；自绘与全站 Geometry 图标同一套做法，
/// 主题色直接取调色板资源。</para>
///
/// <para>数值 / 标签集合每次整体替换实例（触发依赖属性回调重绘）；
/// 高亮索引用于标记"当前小时"。</para>
/// </summary>
public partial class 柱状图控件 : UserControl
{
    /// <summary>画布可用尺寸下限：低于此值不绘制，等布局给出真实尺寸再画。</summary>
    private const double 最小宽 = 320;

    private const double 最小高 = 170;

    /// <summary>本轮绘制使用的画布尺寸（= 布局给定的实际尺寸）。</summary>
    private double _图宽;

    private double _图高;

    /// <summary>
    /// 图表字号系数：画布比设计稿（720 宽）大多少，文字就等比例放大多少
    /// （上限 1.8）。全屏下按真实尺寸绘制后，刻度与日期若仍是设计稿的小号，
    /// 两米外就读不清了。文字都在画布内绝对定位，不影响页面布局。
    /// </summary>
    private double 图字号 => Math.Clamp(_图宽 / 720, 1, 1.8);

    /// <summary>左边距随字号走：刻度数值变大后必须仍装得下。</summary>
    private double 左边距 => 42 * 图字号;

    private double 右边距 => 14 * 图字号;

    /// <summary>顶部要容纳峰值标注，底部要容纳横轴标签，都随字号放大。</summary>
    private double 顶边距 => 28 * 图字号;

    private double 底边距 => 32 * 图字号;

    public static readonly DependencyProperty 数值集合Property = DependencyProperty.Register(
        nameof(数值集合), typeof(IReadOnlyList<double>), typeof(柱状图控件),
        new PropertyMetadata(null, 数值已变更));

    public static readonly DependencyProperty 标签集合Property = DependencyProperty.Register(
        nameof(标签集合), typeof(IReadOnlyList<string>), typeof(柱状图控件),
        new PropertyMetadata(null, 数值已变更));

    public static readonly DependencyProperty 高亮索引Property = DependencyProperty.Register(
        nameof(高亮索引), typeof(int), typeof(柱状图控件),
        new PropertyMetadata(-1, 数值已变更));

    /// <summary>各柱数值（与标签集合一一对应）。</summary>
    public IReadOnlyList<double> 数值集合
    {
        get => (IReadOnlyList<double>)GetValue(数值集合Property);
        set => SetValue(数值集合Property, value);
    }

    /// <summary>横轴标签（如 0–23 时）。</summary>
    public IReadOnlyList<string> 标签集合
    {
        get => (IReadOnlyList<string>)GetValue(标签集合Property);
        set => SetValue(标签集合Property, value);
    }

    /// <summary>高亮柱索引（当前小时），-1 表示不高亮。</summary>
    public int 高亮索引
    {
        get => (int)GetValue(高亮索引Property);
        set => SetValue(高亮索引Property, value);
    }

    public 柱状图控件()
    {
        InitializeComponent();

        // 尺寸变化（首次布局、窗口缩放、全屏切换）都按新尺寸原生重绘，
        // 不再放大"设计稿"——那正是全屏下图表发虚的原因（同 折线图控件）
        SizeChanged += (_, _) => 重建图形();
    }

    private static void 数值已变更(DependencyObject 发送者, DependencyPropertyChangedEventArgs 事件参数) =>
        ((柱状图控件)发送者).重建图形();

    private void 重建图形()
    {
        if (绘图板 is null)
        {
            return;
        }

        // 依赖属性回调可能早于首次布局（ActualWidth 还是 0）：等 SizeChanged 再画
        if (绘图板.ActualWidth < 最小宽 || 绘图板.ActualHeight < 最小高)
        {
            return;
        }

        绘图板.Children.Clear();

        _图宽 = 绘图板.ActualWidth;
        _图高 = 绘图板.ActualHeight;

        var 数值 = 数值集合 ?? [];
        var 标签 = 标签集合 ?? [];

        if (数值.Count == 0)
        {
            return;
        }

        var 绘图高 = _图高 - 顶边距 - 底边距;
        var 基准线 = _图高 - 底边距;
        var 步长 = (_图宽 - 左边距 - 右边距) / 数值.Count;
        var 柱宽 = Math.Max(4, 步长 * 0.62);
        var 满刻度 = 取整刻度(Math.Max(1, 数值.Max()));

        画网格(绘图板, 满刻度, 绘图高, 基准线);

        // ── 柱体 ──
        var 常规柱 = 资源画刷("PrimarySoftBrush");
        var 高亮柱 = 资源画刷("PrimaryBrush");
        var 峰值索引 = 0;

        for (var 序号 = 0; 序号 < 数值.Count; 序号++)
        {
            if (数值[序号] > 数值[峰值索引])
            {
                峰值索引 = 序号;
            }

            var 值 = Math.Max(0, 数值[序号]);
            var 柱高 = 值 / 满刻度 * 绘图高;

            if (值 > 0 && 柱高 < 2)
            {
                柱高 = 2;
            }

            var 柱体 = new Rectangle
            {
                Width = 柱宽,
                Height = 柱高,
                RadiusX = 2,
                RadiusY = 2,
                Fill = 序号 == 高亮索引 ? 高亮柱 : 常规柱,
                ToolTip = $"{标签文本(标签, 序号)} · {值:0} 件",
            };

            Canvas.SetLeft(柱体, 左边距 + 序号 * 步长 + (步长 - 柱宽) / 2);
            Canvas.SetTop(柱体, 基准线 - 柱高);
            绘图板.Children.Add(柱体);
        }

        // ── 峰值标注：最高柱顶上直接标数值，一眼看到产量峰值 ──
        if (数值[峰值索引] > 0)
        {
            var 峰值文本 = new TextBlock
            {
                Text = $"{数值[峰值索引]:0}",
                FontSize = 12 * 图字号,
                FontWeight = FontWeights.SemiBold,
                Foreground = 资源画刷("TextSecondaryBrush"),
            };

            量文本宽(峰值文本);
            var 峰值顶 = 基准线 - 数值[峰值索引] / 满刻度 * 绘图高 - 16;
            var 峰值左 = 左边距 + 峰值索引 * 步长 + (步长 - 峰值文本.Width) / 2;
            Canvas.SetLeft(峰值文本, Math.Min(_图宽 - 右边距 - 峰值文本.Width, Math.Max(0, 峰值左)));
            Canvas.SetTop(峰值文本, Math.Max(0, 峰值顶));
            绘图板.Children.Add(峰值文本);
        }

        // ── 横轴标签 ──
        for (var 序号 = 0; 序号 < 数值.Count; 序号++)
        {
            var 文本 = new TextBlock
            {
                Text = 标签文本(标签, 序号),
                FontSize = 11 * 图字号,
                Foreground = 资源画刷("TextSecondaryBrush"),
                Width = 步长,
                TextAlignment = TextAlignment.Center,
            };

            Canvas.SetLeft(文本, 左边距 + 序号 * 步长);
            Canvas.SetTop(文本, _图高 - 底边距 + 8);
            绘图板.Children.Add(文本);
        }
    }

    private void 画网格(Canvas 板, double 满刻度, double 绘图高, double 基准线)
    {
        var 线画刷 = 资源画刷("BorderBrush");
        var 刻度画刷 = 资源画刷("TextDisabledBrush");

        foreach (var (比例, 说明) in new[] { (1d, 满刻度), (0.5d, 满刻度 / 2), (0d, 0d) })
        {
            var 纵坐标 = 基准线 - 比例 * 绘图高;
            var 线条 = new Rectangle
            {
                Width = _图宽 - 左边距 - 右边距,
                Height = 1,
                Fill = 线画刷,
                Opacity = 比例 == 0 ? 1 : 0.6,
            };

            Canvas.SetLeft(线条, 左边距);
            Canvas.SetTop(线条, 纵坐标);
            板.Children.Add(线条);

            var 刻度 = new TextBlock
            {
                Text = $"{说明:0}",
                FontSize = 10.5 * 图字号,
                Foreground = 刻度画刷,
                Width = 左边距 - 8,
                TextAlignment = TextAlignment.Right,
            };

            Canvas.SetLeft(刻度, 0);
            Canvas.SetTop(刻度, 纵坐标 - 6);
            板.Children.Add(刻度);
        }
    }

    private static string 标签文本(IReadOnlyList<string> 标签, int 序号) =>
        序号 < 标签.Count ? 标签[序号] : 序号.ToString();

    /// <summary>1 / 2 / 2.5 / 5 × 10^k 的"好看"刻度上限。</summary>
    internal static double 取整刻度(double 值)
    {
        var 数量级 = Math.Pow(10, Math.Floor(Math.Log10(值)));

        foreach (var 系数 in new[] { 1d, 2d, 2.5d, 5d, 10d })
        {
            if (系数 * 数量级 >= 值)
            {
                return 系数 * 数量级;
            }
        }

        return 10 * 数量级;
    }

    private static Brush 资源画刷(string 键) =>
        Application.Current?.TryFindResource(键) as Brush ?? Brushes.SteelBlue;

    private static void 量文本宽(TextBlock 文本)
    {
        文本.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        文本.Width = 文本.DesiredSize.Width;
    }
}
