using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace 上位机.应用.视图;

/// <summary>折线图的一条数据系列。</summary>
public sealed class 折线系列
{
    /// <summary>系列名称（图例用），如 "今日" / "昨日"。</summary>
    public string 名称 { get; set; } = string.Empty;

    /// <summary>主题色资源键，如 "PrimaryBrush" / "WarningBrush"。</summary>
    public string 颜色键 { get; set; } = "PrimaryBrush";

    /// <summary>各点数值（与横轴标签一一对应）。</summary>
    public IReadOnlyList<double> 数值 { get; set; } = [];
}

/// <summary>
/// 轻量折线图（自绘，支持多系列对比）。
///
/// <para>系列集合整体替换实例即触发重绘；横轴标签由 <see cref="标签集合"/> 提供
/// （如 0–23 时 / 近 7 天日期）。比例尺取全系列最大值，自动圆整到
/// 1 / 2 / 2.5 / 5 × 10^k，两条系列才可比。</para>
/// </summary>
public partial class 折线图控件 : UserControl
{
    /// <summary>画布可用尺寸下限：低于此值不绘制，等布局给出真实尺寸再画。</summary>
    private const double 最小宽 = 320;

    private const double 最小高 = 170;

    /// <summary>本轮绘制使用的画布尺寸（= 布局给定的实际尺寸）。</summary>
    private double _图宽;

    private double _图高;

    /// <summary>
    /// 图表字号系数：画布比设计稿（720 宽）大多少，文字就等比例放大多少
    /// （上限 1.8，避免超大屏上字号失控）。全屏下按真实尺寸绘制后，
    /// 字号若仍是设计稿的小号，操作员两米外就读不清日期与图例了。
    /// </summary>
    private double 图字号 => Math.Clamp(_图宽 / 720, 1, 1.8);

    /// <summary>左边距随字号走：刻度数值变大后必须仍装得下。</summary>
    private double 左边距 => 42 * 图字号;

    private double 右边距 => 14 * 图字号;

    /// <summary>顶部要容纳图例行，底部要容纳横轴标签，都随字号放大。</summary>
    private double 顶边距 => 28 * 图字号;

    private double 底边距 => 32 * 图字号;

    public static readonly DependencyProperty 系列集合Property = DependencyProperty.Register(
        nameof(系列集合), typeof(IReadOnlyList<折线系列>), typeof(折线图控件),
        new PropertyMetadata(null, 数据已变更));

    public static readonly DependencyProperty 标签集合Property = DependencyProperty.Register(
        nameof(标签集合), typeof(IReadOnlyList<string>), typeof(折线图控件),
        new PropertyMetadata(null, 数据已变更));

    /// <summary>数据系列（1–2 条）。</summary>
    public IReadOnlyList<折线系列> 系列集合
    {
        get => (IReadOnlyList<折线系列>)GetValue(系列集合Property);
        set => SetValue(系列集合Property, value);
    }

    /// <summary>横轴标签。</summary>
    public IReadOnlyList<string> 标签集合
    {
        get => (IReadOnlyList<string>)GetValue(标签集合Property);
        set => SetValue(标签集合Property, value);
    }

    public 折线图控件()
    {
        InitializeComponent();

        // 尺寸变化（首次布局、窗口缩放、全屏切换）都按新尺寸原生重绘 ——
        // 这是"全屏图表发虚"的修复点：不再放大一张设计稿，而是画一幅新图
        SizeChanged += (_, _) => 重建图形();
    }

    private static void 数据已变更(DependencyObject 发送者, DependencyPropertyChangedEventArgs 事件参数) =>
        ((折线图控件)发送者).重建图形();

    private void 重建图形()
    {
        if (绘图板 is null)
        {
            return;
        }

        // 依赖属性回调可能早于首次布局（此时 ActualWidth 还是 0）：
        // 不画占位图，等 SizeChanged 带来真实尺寸再画
        if (绘图板.ActualWidth < 最小宽 || 绘图板.ActualHeight < 最小高)
        {
            return;
        }

        绘图板.Children.Clear();

        _图宽 = 绘图板.ActualWidth;
        _图高 = 绘图板.ActualHeight;

        var 系列列表 = 系列集合 ?? [];
        var 标签 = 标签集合 ?? [];

        var 点数 = 系列列表.FirstOrDefault()?.数值.Count ?? 0;

        if (点数 == 0)
        {
            return;
        }

        var 绘图高 = _图高 - 顶边距 - 底边距;
        var 基准线 = _图高 - 底边距;
        var 步长 = 点数 == 1 ? 0 : (_图宽 - 左边距 - 右边距) / (点数 - 1);
        var 满刻度 = 取整刻度(Math.Max(1,
            系列列表.SelectMany(系列 => 系列.数值).DefaultIfEmpty(0).Max()));

        画网格(绘图板, 满刻度, 绘图高, 基准线, 点数, 步长, 标签);

        // ── 折线与标记点 ──
        foreach (var 系列项 in 系列列表)
        {
            var 颜色 = 资源画刷(系列项.颜色键);
            var 折线 = new Polyline
            {
                Stroke = 颜色,
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            };

            for (var 序号 = 0; 序号 < 点数; 序号++)
            {
                var 值 = Math.Max(0, 序号 < 系列项.数值.Count ? 系列项.数值[序号] : 0);
                折线.Points.Add(new Point(点位横坐标(序号, 点数, 步长), 基准线 - 值 / 满刻度 * 绘图高));
            }

            绘图板.Children.Add(折线);

            // 标记点：白芯色圈，Tooltip 给出精确数值（尺寸随字号缩放，保持比例）
            var 标记径 = 6.4 * 图字号;

            for (var 序号 = 0; 序号 < 折线.Points.Count; 序号++)
            {
                var 圆点 = new Ellipse
                {
                    Width = 标记径,
                    Height = 标记径,
                    Fill = Brushes.White,
                    Stroke = 颜色,
                    StrokeThickness = 1.8,
                    // 系列长度以第一条为基准画，但 Tooltip 取值必须自己兜住短系列，
                    // 否则"某条系列比别的短"会抛 ArgumentOutOfRange 并变成顶栏红条
                    ToolTip = $"{系列项.名称} {标签文本(标签, 序号)} · "
                              + $"{(序号 < 系列项.数值.Count ? 系列项.数值[序号] : 0):0} 件",
                };

                Canvas.SetLeft(圆点, 折线.Points[序号].X - 标记径 / 2);
                Canvas.SetTop(圆点, 折线.Points[序号].Y - 标记径 / 2);
                绘图板.Children.Add(圆点);
            }
        }

        画横轴标签(绘图板, 标签, 点数, 步长);
        画图例(绘图板, 系列列表);
    }

    private double 点位横坐标(int 序号, int 点数, double 步长) =>
        点数 == 1 ? 左边距 : 左边距 + 序号 * 步长;

    private void 画网格(
        Canvas 板, double 满刻度, double 绘图高, double 基准线, int 点数, double 步长,
        IReadOnlyList<string> 标签)
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

    private void 画横轴标签(Canvas 板, IReadOnlyList<string> 标签, int 点数, double 步长)
    {
        for (var 序号 = 0; 序号 < 点数; 序号++)
        {
            var 文本 = new TextBlock
            {
                Text = 标签文本(标签, 序号),
                FontSize = 11 * 图字号,
                Foreground = 资源画刷("TextSecondaryBrush"),
                Width = Math.Max(步长, 18),
                TextAlignment = TextAlignment.Center,
            };

            Canvas.SetLeft(文本, 点位横坐标(序号, 点数, 步长) - 文本.Width / 2);
            Canvas.SetTop(文本, _图高 - 底边距 + 8);
            板.Children.Add(文本);
        }
    }

    private void 画图例(Canvas 板, IReadOnlyList<折线系列> 系列列表)
    {
        if (系列列表.Count == 0)
        {
            return;
        }

        // 图例从右上角向左排：色点 + 系列名
        var 图例右边界 = _图宽 - 右边距;

        for (var 序号 = 系列列表.Count - 1; 序号 >= 0; 序号--)
        {
            var 系列项 = 系列列表[序号];
            var 名称文本 = new TextBlock
            {
                Text = 系列项.名称,
                FontSize = 12 * 图字号,
                Foreground = 资源画刷("TextSecondaryBrush"),
            };

            量文本宽(名称文本);

            // 色点与文字等比：字号放大后圆点不变会显得"标错位置"
            var 点径 = 8 * 图字号;

            var 色点 = new Ellipse
            {
                Width = 点径,
                Height = 点径,
                Fill = 资源画刷(系列项.颜色键),
            };

            Canvas.SetLeft(名称文本, 图例右边界 - 名称文本.Width);
            Canvas.SetTop(名称文本, 2);
            Canvas.SetLeft(色点, 图例右边界 - 名称文本.Width - 点径 - 6);
            Canvas.SetTop(色点, 2 + 名称文本.FontSize * 0.55 - 点径 / 2);
            板.Children.Add(名称文本);
            板.Children.Add(色点);

            图例右边界 -= 名称文本.Width + 点径 + 20;
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
