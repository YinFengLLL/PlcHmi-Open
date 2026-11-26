using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PlcHmi.App.Views;

/// <summary>折线图的一条数据系列。</summary>
public sealed class LineSeries
{
    /// <summary>系列名称（图例用），如 "今日" / "昨日"。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>主题色资源键，如 "PrimaryBrush" / "WarningBrush"。</summary>
    public string ColorKey { get; set; } = "PrimaryBrush";

    /// <summary>各点数值（与横轴标签一一对应）。</summary>
    public IReadOnlyList<double> NumericValue { get; set; } = [];
}

/// <summary>
/// 轻量折线图（自绘，支持多系列对比）。
///
/// <para>系列集合整体替换实例即触发重绘；横轴标签由 <see cref="TagCollection"/> 提供
/// （如 0–23 时 / 近 7 天日期）。比例尺取全系列最大值，自动圆整到
/// 1 / 2 / 2.5 / 5 × 10^k，两条系列才可比。</para>
/// </summary>
public partial class LineChartWidget : UserControl
{
    /// <summary>画布可用尺寸下限：低于此值不绘制，等布局给出真实尺寸再画。</summary>
    private const double minWidth = 320;

    private const double minHeight = 170;

    /// <summary>本轮绘制使用的画布尺寸（= 布局给定的实际尺寸）。</summary>
    private double _chartWidth;

    private double _chartHeight;

    /// <summary>
    /// 图表字号系数：画布比设计稿（720 宽）大多少，文字就等比例放大多少
    /// （上限 1.8，避免超大屏上字号失控）。全屏下按真实尺寸绘制后，
    /// 字号若仍是设计稿的小号，操作员两米外就读不清日期与图例了。
    /// </summary>
    private double ChartFontSize => Math.Clamp(_chartWidth / 720, 1, 1.8);

    /// <summary>左边距随字号走：刻度数值变大后必须仍装得下。</summary>
    private double LeftMargin => 42 * ChartFontSize;

    private double RightMargin => 14 * ChartFontSize;

    /// <summary>顶部要容纳图例行，底部要容纳横轴标签，都随字号放大。</summary>
    private double TopMargin => 28 * ChartFontSize;

    private double BottomMargin => 32 * ChartFontSize;

    public static readonly DependencyProperty seriesCollectionProperty = DependencyProperty.Register(
        nameof(SeriesCollection), typeof(IReadOnlyList<LineSeries>), typeof(LineChartWidget),
        new PropertyMetadata(null, DataChanged));

    public static readonly DependencyProperty tagCollectionProperty = DependencyProperty.Register(
        nameof(TagCollection), typeof(IReadOnlyList<string>), typeof(LineChartWidget),
        new PropertyMetadata(null, DataChanged));

    /// <summary>数据系列（1–2 条）。</summary>
    public IReadOnlyList<LineSeries> SeriesCollection
    {
        get => (IReadOnlyList<LineSeries>)GetValue(seriesCollectionProperty);
        set => SetValue(seriesCollectionProperty, value);
    }

    /// <summary>横轴标签。</summary>
    public IReadOnlyList<string> TagCollection
    {
        get => (IReadOnlyList<string>)GetValue(tagCollectionProperty);
        set => SetValue(tagCollectionProperty, value);
    }

    public LineChartWidget()
    {
        InitializeComponent();

        // 尺寸变化（首次布局、窗口缩放、全屏切换）都按新尺寸原生重绘 ——
        // 这是"全屏图表发虚"的修复点：不再放大一张设计稿，而是画一幅新图
        SizeChanged += (_, _) => RebuildChartShape();
    }

    private static void DataChanged(DependencyObject sender, DependencyPropertyChangedEventArgs eventArgs) =>
        ((LineChartWidget)sender).RebuildChartShape();

    private void RebuildChartShape()
    {
        if (DrawChartBoard is null)
        {
            return;
        }

        // 依赖属性回调可能早于首次布局（此时 ActualWidth 还是 0）：
        // 不画占位图，等 SizeChanged 带来真实尺寸再画
        if (DrawChartBoard.ActualWidth < minWidth || DrawChartBoard.ActualHeight < minHeight)
        {
            return;
        }

        DrawChartBoard.Children.Clear();

        _chartWidth = DrawChartBoard.ActualWidth;
        _chartHeight = DrawChartBoard.ActualHeight;

        var seriesList = SeriesCollection ?? [];
        var Tag = TagCollection ?? [];

        var PointCount = seriesList.FirstOrDefault()?.NumericValue.Count ?? 0;

        if (PointCount == 0)
        {
            return;
        }

        var drawChartHeight = _chartHeight - TopMargin - BottomMargin;
        var BaselineLine = _chartHeight - BottomMargin;
        var stepLong = PointCount == 1 ? 0 : (_chartWidth - LeftMargin - RightMargin) / (PointCount - 1);
        var fullScale = GetWholeScale(Math.Max(1,
            seriesList.SelectMany(Series => Series.NumericValue).DefaultIfEmpty(0).Max()));

        DrawGrid(DrawChartBoard, fullScale, drawChartHeight, BaselineLine, PointCount, stepLong, Tag);

        // ── 折线与标记点 ──
        foreach (var seriesItem in seriesList)
        {
            var Color = ResourceBrush(seriesItem.ColorKey);
            var line = new Polyline
            {
                Stroke = Color,
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            };

            for (var Sequence = 0; Sequence < PointCount; Sequence++)
            {
                var Value = Math.Max(0, Sequence < seriesItem.NumericValue.Count ? seriesItem.NumericValue[Sequence] : 0);
                line.Points.Add(new Point(PointHorizontalCoordinate(Sequence, PointCount, stepLong), BaselineLine - Value / fullScale * drawChartHeight));
            }

            DrawChartBoard.Children.Add(line);

            // 标记点：白芯色圈，Tooltip 给出精确数值（尺寸随字号缩放，保持比例）
            var markerRadius = 6.4 * ChartFontSize;

            for (var Sequence = 0; Sequence < line.Points.Count; Sequence++)
            {
                var Bullet = new Ellipse
                {
                    Width = markerRadius,
                    Height = markerRadius,
                    Fill = Brushes.White,
                    Stroke = Color,
                    StrokeThickness = 1.8,
                    // 系列长度以第一条为基准画，但 Tooltip 取值必须自己兜住短系列，
                    // 否则"某条系列比别的短"会抛 ArgumentOutOfRange 并变成顶栏红条
                    ToolTip = $"{seriesItem.Name} {TagText(Tag, Sequence)} · "
                              + $"{(Sequence < seriesItem.NumericValue.Count ? seriesItem.NumericValue[Sequence] : 0):0} 件",
                };

                Canvas.SetLeft(Bullet, line.Points[Sequence].X - markerRadius / 2);
                Canvas.SetTop(Bullet, line.Points[Sequence].Y - markerRadius / 2);
                DrawChartBoard.Children.Add(Bullet);
            }
        }

        DrawHorizontalAxisTag(DrawChartBoard, Tag, PointCount, stepLong);
        DrawLegend(DrawChartBoard, seriesList);
    }

    private double PointHorizontalCoordinate(int Sequence, int PointCount, double stepLong) =>
        PointCount == 1 ? LeftMargin : LeftMargin + Sequence * stepLong;

    private void DrawGrid(
        Canvas board, double fullScale, double drawChartHeight, double BaselineLine, int PointCount, double stepLong,
        IReadOnlyList<string> Tag)
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

    private void DrawHorizontalAxisTag(Canvas board, IReadOnlyList<string> Tag, int PointCount, double stepLong)
    {
        for (var Sequence = 0; Sequence < PointCount; Sequence++)
        {
            var Text = new TextBlock
            {
                Text = TagText(Tag, Sequence),
                FontSize = 11 * ChartFontSize,
                Foreground = ResourceBrush("TextSecondaryBrush"),
                Width = Math.Max(stepLong, 18),
                TextAlignment = TextAlignment.Center,
            };

            Canvas.SetLeft(Text, PointHorizontalCoordinate(Sequence, PointCount, stepLong) - Text.Width / 2);
            Canvas.SetTop(Text, _chartHeight - BottomMargin + 8);
            board.Children.Add(Text);
        }
    }

    private void DrawLegend(Canvas board, IReadOnlyList<LineSeries> seriesList)
    {
        if (seriesList.Count == 0)
        {
            return;
        }

        // 图例从右上角向左排：色点 + 系列名
        var legendRightBoundary = _chartWidth - RightMargin;

        for (var Sequence = seriesList.Count - 1; Sequence >= 0; Sequence--)
        {
            var seriesItem = seriesList[Sequence];
            var nameText = new TextBlock
            {
                Text = seriesItem.Name,
                FontSize = 12 * ChartFontSize,
                Foreground = ResourceBrush("TextSecondaryBrush"),
            };

            AmountTextWidth(nameText);

            // 色点与文字等比：字号放大后圆点不变会显得"标错位置"
            var pointRadius = 8 * ChartFontSize;

            var colorPoint = new Ellipse
            {
                Width = pointRadius,
                Height = pointRadius,
                Fill = ResourceBrush(seriesItem.ColorKey),
            };

            Canvas.SetLeft(nameText, legendRightBoundary - nameText.Width);
            Canvas.SetTop(nameText, 2);
            Canvas.SetLeft(colorPoint, legendRightBoundary - nameText.Width - pointRadius - 6);
            Canvas.SetTop(colorPoint, 2 + nameText.FontSize * 0.55 - pointRadius / 2);
            board.Children.Add(nameText);
            board.Children.Add(colorPoint);

            legendRightBoundary -= nameText.Width + pointRadius + 20;
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
