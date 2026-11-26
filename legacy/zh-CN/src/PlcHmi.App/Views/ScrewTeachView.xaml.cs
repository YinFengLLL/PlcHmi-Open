using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using 上位机.应用.服务;
using 上位机.应用.视图模型;

namespace 上位机.应用.视图;

/// <summary>
/// 锁附示教视图的代码隐藏。业务逻辑全在 ViewModel；这里只接「按住才动、松开即停」手势。
///
/// <para><b>统一方向键盘（2026-10-13）：</b>凡是 <c>Tag="轴键,±1"</c> 的按钮都会被接上
/// 按住点动手势——箭头布局按现场口径排布（▲Y− ▼Y+ ◀X− ▶X+，Z/ZZ 另立一组上下箭头），
/// 以后增删箭头只改 XAML，不用动这里。</para>
/// </summary>
public partial class 锁附示教视图 : UserControl
{
    private bool _点动手势已附加;

    public 锁附示教视图()
    {
        InitializeComponent();
        // 必须等 Loaded（进入可视树之后）再扫 Tag：DataContextChanged 可能在
        // 模板子元素尚未挂树时触发，那时 VisualTreeHelper 一个按钮都找不到。
        Loaded += (_, _) => 附加点动手势();
    }

    private void 附加点动手势()
    {
        if (_点动手势已附加 || DataContext is not 点表示教视图模型 视图模型)
        {
            return;
        }

        _点动手势已附加 = true;
        附加方向按钮(this, 视图模型);
    }

    /// <summary>递归找出所有 Tag="轴键,±1" 的按钮并接上按住点动手势。</summary>
    private static void 附加方向按钮(DependencyObject 根, 点表示教视图模型 视图模型)
    {
        var 数量 = VisualTreeHelper.GetChildrenCount(根);

        for (var 序 = 0; 序 < 数量; 序++)
        {
            var 子 = VisualTreeHelper.GetChild(根, 序);

            if (子 is Button { Tag: string 标记 }
                && 标记.Split(',') is [{ Length: > 0 } 轴键, { Length: > 0 } 方向文本]
                && int.TryParse(方向文本, out var 方向)
                && 方向 is 1 or -1)
            {
                点动手势.附加(
                    (Button)子,
                    () => 视图模型.开始点动(轴键, 方向),
                    () => 视图模型.结束点动(轴键));
            }

            附加方向按钮(子, 视图模型);
        }
    }
}
