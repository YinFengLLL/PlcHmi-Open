using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PlcHmi.App.Services;
using PlcHmi.App.ViewModels;

namespace PlcHmi.App.Views;

/// <summary>
/// 标定 / 其它点视图的代码隐藏。业务逻辑全在 ViewModel；这里只接「按住才动、松开即停」手势。
///
/// <para><b>统一方向键盘（2026-10-13）：</b>凡是 <c>Tag="X-"/"Y+"/…</c> 形式的方向按钮
/// 都会接上按住点动手势（上 Y− / 下 Y+ / 左 X− / 右 X+，Z 另一组上下箭头），
/// 与锁附 / 点胶示教页保持同一套合并布局风格。</para>
/// </summary>
public partial class CalibrationView : UserControl
{
    private bool _jogGestureAlreadyAdditional;

    public CalibrationView()
    {
        InitializeComponent();
        // 必须等 Loaded（进入可视树之后）再扫 Tag：DataContextChanged 可能在
        // 模板子元素尚未挂树时触发，那时 VisualTreeHelper 一个按钮都找不到。
        Loaded += (_, _) => AdditionalJogGesture();
    }

    private void AdditionalJogGesture()
    {
        if (_jogGestureAlreadyAdditional || DataContext is not CalibrationViewModel viewModel)
        {
            return;
        }

        _jogGestureAlreadyAdditional = true;
        AdditionalDirectionButton(this, viewModel);
    }

    /// <summary>递归找出所有 Tag 为方向键（如 "X-" / "Z+"）的按钮并接上按住点动手势。</summary>
    private static void AdditionalDirectionButton(DependencyObject root, CalibrationViewModel viewModel)
    {
        var Count = VisualTreeHelper.GetChildrenCount(root);

        for (var ordinal = 0; ordinal < Count; ordinal++)
        {
            var child = VisualTreeHelper.GetChild(root, ordinal);

            if (child is Button { Tag: string directionKey }
                && directionKey.Length == 2
                && directionKey[1] is '-' or '+')
            {
                JogGesture.Additional(
                    (Button)child,
                    () => viewModel.StartCalibrationJog(directionKey),
                    viewModel.EndCalibrationJog);
            }

            AdditionalDirectionButton(child, viewModel);
        }
    }
}
