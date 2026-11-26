using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace 上位机.应用.转换器;

/// <summary>
/// 把资源键字符串（如 <c>"SuccessBrush"</c>）转换为对应的画刷。
/// <para>ViewModel 里用字符串表达颜色语义，避免 ViewModel 直接依赖 WPF 类型。</para>
/// </summary>
public sealed class 资源键画刷转换器 : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? 值, Type 目标类型, object? 参数, CultureInfo 区域)
    {
        if (值 is not string 键值 || string.IsNullOrWhiteSpace(键值))
        {
            return Brushes.Transparent;
        }

        if (Application.Current?.TryFindResource(键值) is Brush 画刷)
        {
            return 画刷;
        }

        return Brushes.Transparent;
    }

    /// <inheritdoc />
    public object ConvertBack(object? 值, Type 目标类型, object? 参数, CultureInfo 区域) =>
        throw new NotSupportedException();
}

/// <summary>
/// 把资源键字符串（如 <c>"Icon.Nav.Run"</c>）转换为对应的几何图形。
/// <para>配合 Images/PlcHmi.Icons.xaml 中的 Geometry 资源与 Path 元素使用；
/// 键不存在时返回空几何，避免绑定错误抛异常。</para>
/// </summary>
public sealed class 资源键几何转换器 : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? 值, Type 目标类型, object? 参数, CultureInfo 区域)
    {
        if (值 is not string 键值 || string.IsNullOrWhiteSpace(键值))
        {
            return Geometry.Empty;
        }

        if (Application.Current?.TryFindResource(键值) is Geometry 几何)
        {
            return 几何;
        }

        return Geometry.Empty;
    }

    /// <inheritdoc />
    public object ConvertBack(object? 值, Type 目标类型, object? 参数, CultureInfo 区域) =>
        throw new NotSupportedException();
}

/// <summary>
/// 布尔值转可见性。<paramref name="参数"/> 传 <c>"Invert"</c> 时取反。
/// </summary>
public sealed class 布尔可见性转换器 : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? 值, Type 目标类型, object? 参数, CultureInfo 区域)
    {
        var 标志 = 值 is true;

        if (参数 is string 文本 && 文本.Equals("Invert", StringComparison.OrdinalIgnoreCase))
        {
            标志 = !标志;
        }

        return 标志 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <inheritdoc />
    public object ConvertBack(object? 值, Type 目标类型, object? 参数, CultureInfo 区域) =>
        值 is Visibility.Visible;
}

/// <summary>
/// 布尔值转状态指示灯颜色：true 为运行绿，false 为灰色。
/// </summary>
public sealed class 布尔状态画刷转换器 : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? 值, Type 目标类型, object? 参数, CultureInfo 区域)
    {
        var 键值 = 值 is true ? "SuccessBrush" : "OffBrush";

        return Application.Current?.TryFindResource(键值) as Brush ?? Brushes.Gray;
    }

    /// <inheritdoc />
    public object ConvertBack(object? 值, Type 目标类型, object? 参数, CultureInfo 区域) =>
        throw new NotSupportedException();
}

/// <summary>
/// 报警等级转背景色（用于表格行内的等级标签）。
/// </summary>
public sealed class 等级背景转换器 : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? 值, Type 目标类型, object? 参数, CultureInfo 区域)
    {
        var 键值 = (值 as string) switch
        {
            "DangerSoftBrush" => "DangerSoftBrush",
            "WarningSoftBrush" => "WarningSoftBrush",
            _ => "PrimarySoftBrush",
        };

        return Application.Current?.TryFindResource(键值) as Brush ?? Brushes.Transparent;
    }

    /// <inheritdoc />
    public object ConvertBack(object? 值, Type 目标类型, object? 参数, CultureInfo 区域) =>
        throw new NotSupportedException();
}

/// <summary>
/// 侧栏收起状态转栏宽：展开 196px（图标 + 名称），收起 64px（纯图标栏）。
/// <para>供主窗口侧栏列宽绑定使用（P3 响应式）。</para>
/// </summary>
public sealed class 侧栏宽度转换器 : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? 值, Type 目标类型, object? 参数, CultureInfo 区域) =>
        值 is true ? new GridLength(64) : new GridLength(196);

    /// <inheritdoc />
    public object ConvertBack(object? 值, Type 目标类型, object? 参数, CultureInfo 区域) =>
        throw new NotSupportedException();
}

/// <summary>
/// 设备状态文本转状态图标几何：空闲中/运行中/待机中/复位中/报警中各有专属图标，
/// 未识别文本回退到离线图标。图标形状 + 颜色 + 文字三者同时呈现状态，
/// 满足"颜色不单独承载信息"的 UI 铁律。
/// </summary>
public sealed class 设备状态图标转换器 : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? 值, Type 目标类型, object? 参数, CultureInfo 区域)
    {
        var 键值 = (值 as string) switch
        {
            "运行中" => "Icon.Status.Run",        // 圆环 + 实心点
            "空闲中" => "Icon.Status.Idle",       // 圆环 + 双竖条（就绪等待）
            "待机中" => "Icon.Status.Stop",       // 圆环 + 方块（暂停停止）
            "复位中" => "Icon.Action.Reset",      // 圆形箭头（复位流程进行中）
            "报警中" => "Icon.Status.Fault",      // 八角叉（报警）
            _ => "Icon.Status.Offline",           // 含"未知"：未拿到有效状态字
        };

        return Application.Current?.TryFindResource(键值) as Geometry ?? Geometry.Empty;
    }

    /// <inheritdoc />
    public object ConvertBack(object? 值, Type 目标类型, object? 参数, CultureInfo 区域) =>
        throw new NotSupportedException();
}
