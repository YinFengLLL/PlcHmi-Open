using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace 上位机.应用.视图;

/// <summary>
/// 轻量环形占比图（两段：甲 = 良品绿，乙 = 不良品红，自绘无第三方库）。
///
/// <para><b>颜色编码为什么安全：</b>圆环只用绿 / 红还不够——图例同时给出
/// 数量与百分比，色弱用户靠文字也能读全信息（对比度审查同款要求）。</para>
///
/// <para>总量为 0 时画灰色空环并显示"—"，不显示误导性的 100%。</para>
/// </summary>
public partial class 环形占比图 : UserControl
{
    /// <summary>环形容器可用尺寸下限：低于此值不绘制，等布局给出真实尺寸再画。</summary>
    private const double 最小宽 = 120;

    private const double 最小高 = 120;

    /// <summary>外半径上限：全屏时环不该随空间无限变大，超出后留白居中。</summary>
    private const double 最大外半径 = 140;

    /// <summary>本轮绘制使用的圆环几何参数（由容器实际尺寸推算）。</summary>
    private double _中心x;

    private double _中心y;

    private double _外半径;

    private double _内半径;

    public static readonly DependencyProperty 值甲Property = DependencyProperty.Register(
        nameof(值甲), typeof(double), typeof(环形占比图),
        new PropertyMetadata(0d, 数据已变更));

    public static readonly DependencyProperty 值乙Property = DependencyProperty.Register(
        nameof(值乙), typeof(double), typeof(环形占比图),
        new PropertyMetadata(0d, 数据已变更));

    public static readonly DependencyProperty 标签甲Property = DependencyProperty.Register(
        nameof(标签甲), typeof(string), typeof(环形占比图),
        new PropertyMetadata("良品"));

    public static readonly DependencyProperty 标签乙Property = DependencyProperty.Register(
        nameof(标签乙), typeof(string), typeof(环形占比图),
        new PropertyMetadata("不良品"));

    /// <summary>甲段数值（良品数量）。</summary>
    public double 值甲
    {
        get => (double)GetValue(值甲Property);
        set => SetValue(值甲Property, value);
    }

    /// <summary>乙段数值（不良品数量）。</summary>
    public double 值乙
    {
        get => (double)GetValue(值乙Property);
        set => SetValue(值乙Property, value);
    }

    /// <summary>甲段图例名称。</summary>
    public string 标签甲
    {
        get => (string)GetValue(标签甲Property);
        set => SetValue(标签甲Property, value);
    }

    /// <summary>乙段图例名称。</summary>
    public string 标签乙
    {
        get => (string)GetValue(标签乙Property);
        set => SetValue(标签乙Property, value);
    }

    public 环形占比图()
    {
        InitializeComponent();

        // 尺寸变化（首次布局、窗口缩放、全屏切换）都按新尺寸重算圆环几何 ——
        // 不再放大"设计稿"，中心读数字号也随环半径走，全屏下依然锐利
        SizeChanged += (_, _) => 重建图形();
    }

    private static void 数据已变更(DependencyObject 发送者, DependencyPropertyChangedEventArgs 事件参数) =>
        ((环形占比图)发送者).重建图形();

    private void 重建图形()
    {
        // 依赖属性回调可能早于首次布局（ActualWidth 还是 0）：等 SizeChanged 再画
        if (环形容器.ActualWidth < 最小宽 || 环形容器.ActualHeight < 最小高)
        {
            return;
        }

        // 圆环按容器短边取半径，保持与旧版相近的留白比例
        _外半径 = Math.Min(最大外半径, Math.Min(环形容器.ActualWidth, 环形容器.ActualHeight) / 2 * 0.82);
        _内半径 = _外半径 * 0.62;
        _中心x = 环形容器.ActualWidth / 2;
        _中心y = 环形容器.ActualHeight / 2;

        // 中心读数随环变大：设计稿时代它是靠 Viewbox 整体放大才看得清的
        总量文本.FontSize = Math.Max(18, _外半径 * 0.42);
        总量标签文本.FontSize = Math.Max(10, _外半径 * 0.15);
        总量标签文本.Margin = new Thickness(0, Math.Max(2, _外半径 * 0.06), 0, 0);

        var 甲 = Math.Max(0, 值甲);
        var 乙 = Math.Max(0, 值乙);
        var 总量 = 甲 + 乙;

        if (总量 <= 0)
        {
            // 空数据：灰色整环，比例显示 "—"，不制造 100% 的假象
            段甲.Data = 空心圆环();
            段甲.Fill = 找画刷("BorderBrush");
            段乙.Data = null;
            总量文本.Text = "0";
            甲值文本.Text = "0 (—)";
            乙值文本.Text = "0 (—)";
            return;
        }

        var 甲百分比 = 100 * 甲 / 总量;
        var 乙百分比 = 100 * 乙 / 总量;

        if (乙 <= 0)
        {
            段甲.Data = 空心圆环();
            段甲.Fill = 找画刷("SuccessBrush");
            段乙.Data = null;
        }
        else if (甲 <= 0)
        {
            段甲.Data = null;
            段乙.Data = 空心圆环();
            段乙.Fill = 找画刷("DangerBrush");
        }
        else
        {
            // 从 12 点方向顺时针：甲先画，乙接余下弧段
            var 甲角度 = 360d * 甲 / 总量;
            段甲.Data = 环形段(0, 甲角度);
            段甲.Fill = 找画刷("SuccessBrush");
            段乙.Data = 环形段(甲角度, 360 - 甲角度);
            段乙.Fill = 找画刷("DangerBrush");
        }

        总量文本.Text = 总量.ToString("N0");
        甲值文本.Text = $"{甲:N0} ({甲百分比:0.0}%)";
        乙值文本.Text = $"{乙:N0} ({乙百分比:0.0}%)";
    }

    /// <summary>从 12 点方向起、顺时针扫过 <paramref name="扫过角度"/> 度的圆环段。</summary>
    private Geometry 环形段(double 起始角度, double 扫过角度)
    {
        if (扫过角度 >= 359.99)
        {
            return 空心圆环();
        }

        Point 点(double 角度, double 半径)
        {
            var 弧度 = (角度 - 90) * Math.PI / 180;
            return new Point(_中心x + 半径 * Math.Cos(弧度), _中心y + 半径 * Math.Sin(弧度));
        }

        var 结束角度 = 起始角度 + 扫过角度;
        var 是否大弧 = 扫过角度 > 180;

        var 几何 = new StreamGeometry();

        using (var 上下文 = 几何.Open())
        {
            上下文.BeginFigure(点(起始角度, _外半径), true, true);
            上下文.ArcTo(点(结束角度, _外半径), new Size(_外半径, _外半径),
                0, 是否大弧, SweepDirection.Clockwise, true, false);
            上下文.LineTo(点(结束角度, _内半径), true, false);
            上下文.ArcTo(点(起始角度, _内半径), new Size(_内半径, _内半径),
                0, 是否大弧, SweepDirection.Counterclockwise, true, false);
        }

        几何.Freeze();
        return 几何;
    }

    /// <summary>整圈空心圆环（外圆 + 内圆偶或填充）。</summary>
    private Geometry 空心圆环()
    {
        var 组合 = new GeometryGroup { FillRule = FillRule.EvenOdd };
        组合.Children.Add(new EllipseGeometry(new Point(_中心x, _中心y), _外半径, _外半径));
        组合.Children.Add(new EllipseGeometry(new Point(_中心x, _中心y), _内半径, _内半径));
        组合.Freeze();
        return 组合;
    }

    private static Brush 找画刷(string 键) =>
        Application.Current?.TryFindResource(键) as Brush ?? Brushes.Gray;
}
