using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PlcHmi.App.Views;

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
public partial class BarChartWidget : UserControl
{
    /// <summary>画布可用尺寸下限：低于此值不绘制，等布局给出真实尺寸再画。</summary>
    private const double minWidth = 320;

    private const double minHeight = 170;

    /// <summary>本轮绘制使用的画布尺寸（= 布局给定的实际尺寸）。</summary>
    private double _chartWidth;

    private double _chartHeight;

    /// <summary>
    /// 图表字号系数：画布比设计稿（720 宽）大多少，文字就等比例放大多少
    /// （上限 1.8）。全屏下按真实尺寸绘制后，刻度与日期若仍是设计稿的小号，
    /// 两米外就读不清了。文字都在画布内绝对定位，不影响页面布局。
    /// </summary>
    private double ChartFontSize => Math.Clamp(_chartWidth / 720, 1, 1.8);

    /// <summary>左边距随字号走：刻度数值变大后必须仍装得下。</summary>
    private double LeftMargin => 42 * ChartFontSize;

    private double RightMargin => 14 * ChartFontSize;

    /// <summary>顶部要容纳峰值标注，底部要容纳横轴标签，都随字号放大。</summary>
    private double TopMargin => 28 * ChartFontSize;

    private double BottomMargin => 32 * ChartFontSize;

    public static readonly DependencyProperty numericValueCollectionProperty = DependencyProperty.Register(
        nameof(NumericValueCollection), typeof(IReadOnlyList<double>), typeof(BarChartWidget),
        new PropertyMetadata(null, NumericValueChanged));

    public static readonly DependencyProperty tagCollectionProperty = DependencyProperty.Register(
        nameof(TagCollection), typeof(IReadOnlyList<string>), typeof(BarChartWidget),
        new PropertyMetadata(null, NumericValueChanged));

    public static readonly DependencyProperty highlightIndexProperty = DependencyProperty.Register(
        nameof(HighlightIndex), typeof(int), typeof(BarChartWidget),
        new PropertyMetadata(-1, NumericValueChanged));

    /// <summary>各柱数值（与标签集合一一对应）。</summary>
    public IReadOnlyList<double> NumericValueCollection
    {
        get => (IReadOnlyList<double>)GetValue(numericValueCollectionProperty);
        set => SetValue(numericValueCollectionProperty, value);
    }

    /// <summary>横轴标签（如 0–23 时）。</summary>
    public IReadOnlyList<string> TagCollection
    {
        get => (IReadOnlyList<string>)GetValue(tagCollectionProperty);
        set => SetValue(tagCollectionProperty, value);
    }

    /// <summary>高亮柱索引（当前小时），-1 表示不高亮。</summary>
    public int HighlightIndex
    {
        get => (int)GetValue(highlightIndexProperty);
        set => SetValue(highlightIndexProperty, value);
    }

    public BarChartWidget()
    {
        InitializeComponent();

        // 尺寸变化（首次布局、窗口缩放、全屏切换）都按新尺寸原生重绘，
        // 不再放大"设计稿"——那正是全屏下图表发虚的原因（同 折线图控件）
        SizeChanged += (_, _) => RebuildChartShape();
    }

    private static void NumericValueChanged(DependencyObject sender, DependencyPropertyChangedEventArgs eventArgs) =>
        ((BarChartWidget)sender).RebuildChartShape();

    private void RebuildChartShape()
    {
        if (DrawChartBoard is null)
        {
            return;
        }

        // 依赖属性回调可能早于首次布局（ActualWidth 还是 0）：等 SizeChanged 再画
        if (DrawChartBoard.ActualWidth < minWidth || DrawChartBoard.ActualHeight < minHeight)
        {
            return;
        }

        DrawChartBoard.Children.Clear();

        _chartWidth = DrawChartBoard.ActualWidth;
        _chartHeight = DrawChartBoard.ActualHeight;

        var NumericValue = NumericValueCollection ?? [];
        var Tag = TagCollection ?? [];

        if (NumericValue.Count == 0)
        {
            return;
        }

        var drawChartHeight = _chartHeight - TopMargin - BottomMargin;
        var BaselineLine = _chartHeight - BottomMargin;
        var stepLong = (_chartWidth - LeftMargin - RightMargin) / NumericValue.Count;
        var barWidth = Math.Max(4, stepLong * 0.62);
        var fullScale = GetWholeScale(Math.Max(1, NumericValue.Max()));

        DrawGrid(DrawChartBoard, fullScale, drawChartHeight, BaselineLine);

        // ── 柱体 ──
        var normalRuleBar = ResourceBrush("PrimarySoftBrush");
        var highlightBar = ResourceBrush("PrimaryBrush");
        var peakIndex = 0;

        for (var Sequence = 0; Sequence < NumericValue.Count; Sequence++)
        {
            if (NumericValue[Sequence] > NumericValue[peakIndex])
            {
                peakIndex = Sequence;
            }

            var Value = Math.Max(0, NumericValue[Sequence]);
            var barHeight = Value / fullScale * drawChartHeight;

            if (Value > 0 && barHeight < 2)
            {
                barHeight = 2;
            }

            var barBody = new Rectangle
            {
                Width = barWidth,
                Height = barHeight,
                RadiusX = 2,
                RadiusY = 2,
                Fill = Sequence == HighlightIndex ? highlightBar : normalRuleBar,
                ToolTip = $"{TagText(Tag, Sequence)} · {Value:0} 件",
            };

            Canvas.SetLeft(barBody, LeftMargin + Sequence * stepLong + (stepLong - barWidth) / 2);
            Canvas.SetTop(barBody, BaselineLine - barHeight);
            DrawChartBoard.Children.Add(barBody);
        }

        // ── 峰值标注：最高柱顶上直接标数值，一眼看到产量峰值 ──
        if (NumericValue[peakIndex] > 0)
        {
            var peakText = new TextBlock
            {
                Text = $"{NumericValue[peakIndex]:0}",
                FontSize = 12 * ChartFontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = ResourceBrush("TextSecondaryBrush"),
            };

            AmountTextWidth(peakText);
            var peakTop = BaselineLine - NumericValue[peakIndex] / fullScale * drawChartHeight - 16;
            var peakLeft = LeftMargin + peakIndex * stepLong + (stepLong - peakText.Width) / 2;
            Canvas.SetLeft(peakText, Math.Min(_chartWidth - RightMargin - peakText.Width, Math.Max(0, peakLeft)));
            Canvas.SetTop(peakText, Math.Max(0, peakTop));
            DrawChartBoard.Children.Add(peakText);
        }

        // ── 横轴标签 ──
        for (var Sequence = 0; Sequence < NumericValue.Count; Sequence++)
        {
            var Text = new TextBlock
            {
                Text = TagText(Tag, Sequence),
                FontSize = 11 * ChartFontSize,
                Foreground = ResourceBrush("TextSecondaryBrush"),
                Width = stepLong,
                TextAlignment = TextAlignment.Center,
            };

            Canvas.SetLeft(Text, LeftMargin + Sequence * stepLong);
            Canvas.SetTop(Text, _chartHeight - BottomMargin + 8);
            DrawChartBoard.Children.Add(Text);
        }
    }

    private void DrawGrid(Canvas board, double fullScale, double drawChartHeight, double BaselineLine)
    {
        var lineBrush = ResourceBrush("BorderBrush");
        var scaleBrush = ResourceBrush("TextDisabledBrush");

        foreach (var (ratio, Remark) in new[] { (1d, fullScale), (0.5d, fullScale / 2), (0d, 0d) })
        {
            var verticalCoordinate = BaselineLine - ratio * drawChartHeight;
            var lineItem = new Rectangle
            {
                Width = _chartWidth - LeftMargin - RightMargin,
                Height = 1,
                Fill = lineBrush,
                Opacity = ratio == 0 ? 1 : 0.6,
            };

            Canvas.SetLeft(lineItem, LeftMargin);
            Canvas.SetTop(lineItem, verticalCoordinate);
            board.Children.Add(lineItem);

            var scale = new TextBlock
            {
                Text = $"{Remark:0}",
                FontSize = 10.5 * ChartFontSize,
                Foreground = scaleBrush,
                Width = LeftMargin - 8,
                TextAlignment = TextAlignment.Right,
            };

            Canvas.SetLeft(scale, 0);
            Canvas.SetTop(scale, verticalCoordinate - 6);
            board.Children.Add(scale);
        }
    }

    private static string TagText(IReadOnlyList<string> Tag, int Sequence) =>
        Sequence < Tag.Count ? Tag[Sequence] : Sequence.ToString();

    /// <summary>1 / 2 / 2.5 / 5 × 10^k 的"好看"刻度上限。</summary>
    internal static double GetWholeScale(double Value)
    {
        var countLevel = Math.Pow(10, Math.Floor(Math.Log10(Value)));

        foreach (var factor in new[] { 1d, 2d, 2.5d, 5d, 10d })
        {
            if (factor * countLevel >= Value)
            {
                return factor * countLevel;
            }
        }

        return 10 * countLevel;
    }

    private static Brush ResourceBrush(string Key) =>
        Application.Current?.TryFindResource(Key) as Brush ?? Brushes.SteelBlue;

    private static void AmountTextWidth(TextBlock Text)
    {
        Text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Text.Width = Text.DesiredSize.Width;
    }
}
