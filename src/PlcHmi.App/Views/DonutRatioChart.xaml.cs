using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PlcHmi.App.Views;

/// <summary>
/// 轻量环形占比图（两段：甲 = 良品绿，乙 = 不良品红，自绘无第三方库）。
///
/// <para><b>颜色编码为什么安全：</b>圆环只用绿 / 红还不够——图例同时给出
/// 数量与百分比，色弱用户靠文字也能读全信息（对比度审查同款要求）。</para>
///
/// <para>总量为 0 时画灰色空环并显示"—"，不显示误导性的 100%。</para>
/// </summary>
public partial class DonutRatioChart : UserControl
{
    /// <summary>环形容器可用尺寸下限：低于此值不绘制，等布局给出真实尺寸再画。</summary>
    private const double minWidth = 120;

    private const double minHeight = 120;

    /// <summary>外半径上限：全屏时环不该随空间无限变大，超出后留白居中。</summary>
    private const double maxOuterRadius = 140;

    /// <summary>本轮绘制使用的圆环几何参数（由容器实际尺寸推算）。</summary>
    private double _centerx;

    private double _centery;

    private double _outerRadius;

    private double _innerRadius;

    public static readonly DependencyProperty valueAProperty = DependencyProperty.Register(
        nameof(ValueA), typeof(double), typeof(DonutRatioChart),
        new PropertyMetadata(0d, DataChanged));

    public static readonly DependencyProperty valueBProperty = DependencyProperty.Register(
        nameof(ValueB), typeof(double), typeof(DonutRatioChart),
        new PropertyMetadata(0d, DataChanged));

    public static readonly DependencyProperty labelAProperty = DependencyProperty.Register(
        nameof(LabelA), typeof(string), typeof(DonutRatioChart),
        new PropertyMetadata("良品"));

    public static readonly DependencyProperty labelBProperty = DependencyProperty.Register(
        nameof(LabelB), typeof(string), typeof(DonutRatioChart),
        new PropertyMetadata("不良品"));

    /// <summary>甲段数值（良品数量）。</summary>
    public double ValueA
    {
        get => (double)GetValue(valueAProperty);
        set => SetValue(valueAProperty, value);
    }

    /// <summary>乙段数值（不良品数量）。</summary>
    public double ValueB
    {
        get => (double)GetValue(valueBProperty);
        set => SetValue(valueBProperty, value);
    }

    /// <summary>甲段图例名称。</summary>
    public string LabelA
    {
        get => (string)GetValue(labelAProperty);
        set => SetValue(labelAProperty, value);
    }

    /// <summary>乙段图例名称。</summary>
    public string LabelB
    {
        get => (string)GetValue(labelBProperty);
        set => SetValue(labelBProperty, value);
    }

    public DonutRatioChart()
    {
        InitializeComponent();

        // 尺寸变化（首次布局、窗口缩放、全屏切换）都按新尺寸重算圆环几何 ——
        // 不再放大"设计稿"，中心读数字号也随环半径走，全屏下依然锐利
        SizeChanged += (_, _) => RebuildChartShape();
    }

    private static void DataChanged(DependencyObject sender, DependencyPropertyChangedEventArgs eventArgs) =>
        ((DonutRatioChart)sender).RebuildChartShape();

    private void RebuildChartShape()
    {
        // 依赖属性回调可能早于首次布局（ActualWidth 还是 0）：等 SizeChanged 再画
        if (DonutContainer.ActualWidth < minWidth || DonutContainer.ActualHeight < minHeight)
        {
            return;
        }

        // 圆环按容器短边取半径，保持与旧版相近的留白比例
        _outerRadius = Math.Min(maxOuterRadius, Math.Min(DonutContainer.ActualWidth, DonutContainer.ActualHeight) / 2 * 0.82);
        _innerRadius = _outerRadius * 0.62;
        _centerx = DonutContainer.ActualWidth / 2;
        _centery = DonutContainer.ActualHeight / 2;

        // 中心读数随环变大：设计稿时代它是靠 Viewbox 整体放大才看得清的
        TotalAmountText.FontSize = Math.Max(18, _outerRadius * 0.42);
        TotalAmountTagText.FontSize = Math.Max(10, _outerRadius * 0.15);
        TotalAmountTagText.Margin = new Thickness(0, Math.Max(2, _outerRadius * 0.06), 0, 0);

        var item = Math.Max(0, ValueA);
        var item2 = Math.Max(0, ValueB);
        var totalAmount = item + item2;

        if (totalAmount <= 0)
        {
            // 空数据：灰色整环，比例显示 "—"，不制造 100% 的假象
            Sectionitem.Data = HollowRing();
            Sectionitem.Fill = FindBrush("BorderBrush");
            Sectionitem2.Data = null;
            TotalAmountText.Text = "0";
            ItemValueText.Text = "0 (—)";
            Item2ValueText.Text = "0 (—)";
            return;
        }

        var percentA = 100 * item / totalAmount;
        var percentB = 100 * item2 / totalAmount;

        if (item2 <= 0)
        {
            Sectionitem.Data = HollowRing();
            Sectionitem.Fill = FindBrush("SuccessBrush");
            Sectionitem2.Data = null;
        }
        else if (item <= 0)
        {
            Sectionitem.Data = null;
            Sectionitem2.Data = HollowRing();
            Sectionitem2.Fill = FindBrush("DangerBrush");
        }
        else
        {
            // 从 12 点方向顺时针：甲先画，乙接余下弧段
            var angleA = 360d * item / totalAmount;
            Sectionitem.Data = DonutSection(0, angleA);
            Sectionitem.Fill = FindBrush("SuccessBrush");
            Sectionitem2.Data = DonutSection(angleA, 360 - angleA);
            Sectionitem2.Fill = FindBrush("DangerBrush");
        }

        TotalAmountText.Text = totalAmount.ToString("N0");
        ItemValueText.Text = $"{item:N0} ({percentA:0.0}%)";
        Item2ValueText.Text = $"{item2:N0} ({percentB:0.0}%)";
    }

    /// <summary>从 12 点方向起、顺时针扫过 <paramref name="scanBeyondAngle"/> 度的圆环段。</summary>
    private Geometry DonutSection(double startAngle, double scanBeyondAngle)
    {
        if (scanBeyondAngle >= 359.99)
        {
            return HollowRing();
        }

        Point dot(double angle, double radius)
        {
            var radian = (angle - 90) * Math.PI / 180;
            return new Point(_centerx + radius * Math.Cos(radian), _centery + radius * Math.Sin(radian));
        }

        var endAngle = startAngle + scanBeyondAngle;
        var isLargeArc = scanBeyondAngle > 180;

        var geometry = new StreamGeometry();

        using (var context = geometry.Open())
        {
            context.BeginFigure(dot(startAngle, _outerRadius), true, true);
            context.ArcTo(dot(endAngle, _outerRadius), new Size(_outerRadius, _outerRadius),
                0, isLargeArc, SweepDirection.Clockwise, true, false);
            context.LineTo(dot(endAngle, _innerRadius), true, false);
            context.ArcTo(dot(startAngle, _innerRadius), new Size(_innerRadius, _innerRadius),
                0, isLargeArc, SweepDirection.Counterclockwise, true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>整圈空心圆环（外圆 + 内圆偶或填充）。</summary>
    private Geometry HollowRing()
    {
        var groupValid = new GeometryGroup { FillRule = FillRule.EvenOdd };
        groupValid.Children.Add(new EllipseGeometry(new Point(_centerx, _centery), _outerRadius, _outerRadius));
        groupValid.Children.Add(new EllipseGeometry(new Point(_centerx, _centery), _innerRadius, _innerRadius));
        groupValid.Freeze();
        return groupValid;
    }

    private static Brush FindBrush(string Key) =>
        Application.Current?.TryFindResource(Key) as Brush ?? Brushes.Gray;
}
