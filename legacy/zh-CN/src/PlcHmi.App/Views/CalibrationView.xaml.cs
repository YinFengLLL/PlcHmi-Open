using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using 上位机.应用.服务;
using 上位机.应用.视图模型;

namespace 上位机.应用.视图;

/// <summary>
/// 标定 / 其它点视图的代码隐藏。业务逻辑全在 ViewModel；这里只接「按住才动、松开即停」手势。
///
/// <para><b>统一方向键盘（2026-10-13）：</b>凡是 <c>Tag="X-"/"Y+"/…</c> 形式的方向按钮
/// 都会接上按住点动手势（上 Y− / 下 Y+ / 左 X− / 右 X+，Z 另一组上下箭头），
/// 与锁附 / 点胶示教页保持同一套合并布局风格。</para>
/// </summary>
public partial class 标定视图 : UserControl
{
    private bool _点动手势已附加;

    public 标定视图()
    {
        InitializeComponent();
        // 必须等 Loaded（进入可视树之后）再扫 Tag：DataContextChanged 可能在
        // 模板子元素尚未挂树时触发，那时 VisualTreeHelper 一个按钮都找不到。
        Loaded += (_, _) => 附加点动手势();
    }

    private void 附加点动手势()
    {
        if (_点动手势已附加 || DataContext is not 标定视图模型 视图模型)
        {
            return;
        }

        _点动手势已附加 = true;
        附加方向按钮(this, 视图模型);
    }

    /// <summary>递归找出所有 Tag 为方向键（如 "X-" / "Z+"）的按钮并接上按住点动手势。</summary>
    private static void 附加方向按钮(DependencyObject 根, 标定视图模型 视图模型)
    {
        var 数量 = VisualTreeHelper.GetChildrenCount(根);

        for (var 序 = 0; 序 < 数量; 序++)
        {
            var 子 = VisualTreeHelper.GetChild(根, 序);

            if (子 is Button { Tag: string 方向键 }
                && 方向键.Length == 2
                && 方向键[1] is '-' or '+')
            {
                点动手势.附加(
                    (Button)子,
                    () => 视图模型.开始标定点动(方向键),
                    视图模型.结束标定点动);
            }

            附加方向按钮(子, 视图模型);
        }
    }
}
