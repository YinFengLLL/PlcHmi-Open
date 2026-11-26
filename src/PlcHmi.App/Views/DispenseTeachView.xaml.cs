using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PlcHmi.App.Services;
using PlcHmi.App.ViewModels;

namespace PlcHmi.App.Views;

/// <summary>
/// 点胶示教视图的代码隐藏。业务逻辑全在 ViewModel；这里只接「按住才动、松开即停」手势。
///
/// <para><b>统一方向键盘（2026-10-13）：</b>凡是 <c>Tag="轴键,±1"</c> 的按钮都会被接上
/// 按住点动手势（▲Y− ▼Y+ ◀X− ▶X+，Z 另立一组上下箭头）。点胶页的轴键是
/// GlueX / Y（与锁附 Y 共轴）/ GlueZ。</para>
/// </summary>
public partial class DispenseTeachView : UserControl
{
    private bool _jogGestureAlreadyAdditional;

    public DispenseTeachView()
    {
        InitializeComponent();
        // 必须等 Loaded（进入可视树之后）再扫 Tag：DataContextChanged 可能在
        // 模板子元素尚未挂树时触发，那时 VisualTreeHelper 一个按钮都找不到。
        Loaded += (_, _) => AdditionalJogGesture();
    }

    private void AdditionalJogGesture()
    {
        if (_jogGestureAlreadyAdditional || DataContext is not PointTableTeachViewModel viewModel)
        {
            return;
        }

        _jogGestureAlreadyAdditional = true;
        AdditionalDirectionButton(this, viewModel);
    }

    /// <summary>递归找出所有 Tag="轴键,±1" 的按钮并接上按住点动手势。</summary>
    private static void AdditionalDirectionButton(DependencyObject root, PointTableTeachViewModel viewModel)
    {
        var Count = VisualTreeHelper.GetChildrenCount(root);

        for (var ordinal = 0; ordinal < Count; ordinal++)
        {
            var child = VisualTreeHelper.GetChild(root, ordinal);

            if (child is Button { Tag: string mark }
                && mark.Split(',') is [{ Length: > 0 } AxisKey, { Length: > 0 } directionText]
                && int.TryParse(directionText, out var direction)
                && direction is 1 or -1)
            {
                JogGesture.Additional(
                    (Button)child,
                    () => viewModel.StartJog(AxisKey, direction),
                    () => viewModel.EndJog(AxisKey));
            }

            AdditionalDirectionButton(child, viewModel);
        }
    }
}
