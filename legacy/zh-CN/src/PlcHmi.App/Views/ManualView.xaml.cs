using System.Windows.Controls;
using 上位机.应用.服务;
using 上位机.应用.视图模型;

namespace 上位机.应用.视图;

/// <summary>
/// ManualView 的代码隐藏。
///
/// <para>业务逻辑全部在 ViewModel 中；这里只接一处 View 专属的东西：
/// 点动方向按钮的 <b>「按住才动、松开即停」手势</b>。
/// Command 只能表达"点击"，表达不了"按住"——按住/松开必须由 View 处理 MouseDown / MouseUp。</para>
/// </summary>
public partial class 手动视图 : UserControl
{
    private bool _点动手势已附加;

    public 手动视图()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => 附加点动手势();
    }

    private void 附加点动手势()
    {
        // DataContext 可能被多次赋值；重复挂会变成一次按下触发两次启动
        if (_点动手势已附加 || DataContext is not 手动视图模型 视图模型)
        {
            return;
        }

        _点动手势已附加 = true;

        点动手势.附加(点动反转按钮, () => 视图模型.开始点动(-1), 视图模型.结束点动);
        点动手势.附加(点动正转按钮, () => 视图模型.开始点动(1), 视图模型.结束点动);
    }
}
