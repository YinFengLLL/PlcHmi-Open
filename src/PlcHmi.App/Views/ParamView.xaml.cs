using System.Windows.Controls;

namespace PlcHmi.App.Views;

/// <summary>
/// ParamView 的代码隐藏。
///
/// <para>参数页现在是三张纯表格（轴速度 / 延时 / 按钮），
/// 不再需要点动相关的鼠标事件 —— 点动测试已移到示教页。</para>
/// </summary>
public partial class ParameterView : UserControl
{
    public ParameterView()
    {
        InitializeComponent();
    }
}
