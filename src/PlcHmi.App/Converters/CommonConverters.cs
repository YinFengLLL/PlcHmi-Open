using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace PlcHmi.App.Converters;

/// <summary>
/// 把资源键字符串（如 <c>"SuccessBrush"</c>）转换为对应的画刷。
/// <para>ViewModel 里用字符串表达颜色语义，避免 ViewModel 直接依赖 WPF 类型。</para>
/// </summary>
public sealed class ResourceKeyBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? Value, Type targetType, object? Parameter, CultureInfo Area)
    {
        if (Value is not string keyValue || string.IsNullOrWhiteSpace(keyValue))
        {
            return Brushes.Transparent;
        }

        if (Application.Current?.TryFindResource(keyValue) is Brush brush)
        {
            return brush;
        }

        return Brushes.Transparent;
    }

    /// <inheritdoc />
    public object ConvertBack(object? Value, Type targetType, object? Parameter, CultureInfo Area) =>
        throw new NotSupportedException();
}

/// <summary>
/// 把资源键字符串（如 <c>"Icon.Nav.Run"</c>）转换为对应的几何图形。
/// <para>配合 Images/PlcHmi.Icons.xaml 中的 Geometry 资源与 Path 元素使用；
/// 键不存在时返回空几何，避免绑定错误抛异常。</para>
/// </summary>
public sealed class ResourceKeyGeometryConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? Value, Type targetType, object? Parameter, CultureInfo Area)
    {
        if (Value is not string keyValue || string.IsNullOrWhiteSpace(keyValue))
        {
            return Geometry.Empty;
        }

        if (Application.Current?.TryFindResource(keyValue) is Geometry geometry)
        {
            return geometry;
        }

        return Geometry.Empty;
    }

    /// <inheritdoc />
    public object ConvertBack(object? Value, Type targetType, object? Parameter, CultureInfo Area) =>
        throw new NotSupportedException();
}

/// <summary>
/// 布尔值转可见性。<paramref name="Parameter"/> 传 <c>"Invert"</c> 时取反。
/// </summary>
public sealed class BooleanVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? Value, Type targetType, object? Parameter, CultureInfo Area)
    {
        var MarkLog = Value is true;

        if (Parameter is string Text && Text.Equals("Invert", StringComparison.OrdinalIgnoreCase))
        {
            MarkLog = !MarkLog;
        }

        return MarkLog ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <inheritdoc />
    public object ConvertBack(object? Value, Type targetType, object? Parameter, CultureInfo Area) =>
        Value is Visibility.Visible;
}

/// <summary>
/// 布尔值转状态指示灯颜色：true 为运行绿，false 为灰色。
/// </summary>
public sealed class BooleanStateBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? Value, Type targetType, object? Parameter, CultureInfo Area)
    {
        var keyValue = Value is true ? "SuccessBrush" : "OffBrush";

        return Application.Current?.TryFindResource(keyValue) as Brush ?? Brushes.Gray;
    }

    /// <inheritdoc />
    public object ConvertBack(object? Value, Type targetType, object? Parameter, CultureInfo Area) =>
        throw new NotSupportedException();
}

/// <summary>
/// 报警等级转背景色（用于表格行内的等级标签）。
/// </summary>
public sealed class LevelBackgroundConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? Value, Type targetType, object? Parameter, CultureInfo Area)
    {
        var keyValue = (Value as string) switch
        {
            "DangerSoftBrush" => "DangerSoftBrush",
            "WarningSoftBrush" => "WarningSoftBrush",
            _ => "PrimarySoftBrush",
        };

        return Application.Current?.TryFindResource(keyValue) as Brush ?? Brushes.Transparent;
    }

    /// <inheritdoc />
    public object ConvertBack(object? Value, Type targetType, object? Parameter, CultureInfo Area) =>
        throw new NotSupportedException();
}

/// <summary>
/// 侧栏收起状态转栏宽：展开 196px（图标 + 名称），收起 64px（纯图标栏）。
/// <para>供主窗口侧栏列宽绑定使用（P3 响应式）。</para>
/// </summary>
public sealed class SidebarWidthConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? Value, Type targetType, object? Parameter, CultureInfo Area) =>
        Value is true ? new GridLength(64) : new GridLength(196);

    /// <inheritdoc />
    public object ConvertBack(object? Value, Type targetType, object? Parameter, CultureInfo Area) =>
        throw new NotSupportedException();
}

/// <summary>
/// 设备状态文本转状态图标几何：空闲中/运行中/待机中/复位中/报警中各有专属图标，
/// 未识别文本回退到离线图标。图标形状 + 颜色 + 文字三者同时呈现状态，
/// 满足"颜色不单独承载信息"的 UI 铁律。
/// </summary>
public sealed class DeviceStateIconConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? Value, Type targetType, object? Parameter, CultureInfo Area)
    {
        var keyValue = (Value as string) switch
        {
            "运行中" => "Icon.Status.Run",        // 圆环 + 实心点
            "空闲中" => "Icon.Status.Idle",       // 圆环 + 双竖条（就绪等待）
            "待机中" => "Icon.Status.Stop",       // 圆环 + 方块（暂停停止）
            "复位中" => "Icon.Action.Reset",      // 圆形箭头（复位流程进行中）
            "报警中" => "Icon.Status.Fault",      // 八角叉（报警）
            _ => "Icon.Status.Offline",           // 含"未知"：未拿到有效状态字
        };

        return Application.Current?.TryFindResource(keyValue) as Geometry ?? Geometry.Empty;
    }

    /// <inheritdoc />
    public object ConvertBack(object? Value, Type targetType, object? Parameter, CultureInfo Area) =>
        throw new NotSupportedException();
}
