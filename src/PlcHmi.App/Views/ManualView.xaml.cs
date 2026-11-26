using System.Windows.Controls;
using PlcHmi.App.Services;
using PlcHmi.App.ViewModels;

namespace PlcHmi.App.Views;

/// <summary>
/// ManualView 的代码隐藏。
///
/// <para>业务逻辑全部在 ViewModel 中；这里只接一处 View 专属的东西：
/// 点动方向按钮的 <b>「按住才动、松开即停」手势</b>。
/// Command 只能表达"点击"，表达不了"按住"——按住/松开必须由 View 处理 MouseDown / MouseUp。</para>
/// </summary>
public partial class ManualView : UserControl
{
    private bool _jogGestureAlreadyAdditional;

    public ManualView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AdditionalJogGesture();
    }

    private void AdditionalJogGesture()
    {
        // DataContext 可能被多次赋值；重复挂会变成一次按下触发两次启动
        if (_jogGestureAlreadyAdditional || DataContext is not ManualViewModel viewModel)
        {
            return;
        }

        _jogGestureAlreadyAdditional = true;

        JogGesture.Additional(JogReverseButton, () => viewModel.StartJog(-1), viewModel.EndJog);
        JogGesture.Additional(JogForwardButton, () => viewModel.StartJog(1), viewModel.EndJog);
    }
}
