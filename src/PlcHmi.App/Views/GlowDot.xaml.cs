using System.Windows;
using System.Windows.Media;

namespace PlcHmi.App.Views;

/// <summary>
/// 带辉光的状态指示灯：外圈同色低透明度光晕 + 内圈实心灯芯。
/// 辉光不用 DropShadowEffect（软件渲染掉帧，UI 铁律 1），用双层图形模拟。
/// </summary>
public partial class GlowStatusLamp : System.Windows.Controls.UserControl
{
    /// <summary>灯芯画刷依赖属性。</summary>
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(GlowStatusLamp),
        new FrameworkPropertyMetadata(Brushes.Gray));

    /// <summary>灯芯与光晕共用的画刷（光晕自动取其 28% 透明度放大圈）。</summary>
    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>初始化组件。</summary>
    public GlowStatusLamp()
    {
        InitializeComponent();
    }
}
