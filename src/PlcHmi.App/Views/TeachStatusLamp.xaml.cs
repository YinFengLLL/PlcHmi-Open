namespace PlcHmi.App.Views;

/// <summary>
/// 示教页顶部固定状态灯（锁附示教 / 点胶示教共用）。
///
/// <para><b>2026-10-16 现场要求：</b>读指定 M 区位的值——<c>0</c> = 各轴未回原（灯闪烁），
/// <c>1</c> = 各轴回原完成（灯常亮）。位置固定在页面工具条中段（页与页之间不漂移），
/// 状态由页面 ViewModel 每秒回读一次（见 <c>PointTableTeachViewModel.Lamp.cs</c>）。</para>
///
/// <para>数据全部来自页面的 DataContext（两个示教页 VM 属性同名），因此这里不定义依赖属性，
/// 也不需要代码后置逻辑——没有任何"控件自己的状态"需要维护。</para>
/// </summary>
public partial class TeachStatusLamp : System.Windows.Controls.UserControl
{
    /// <summary>初始化组件。</summary>
    public TeachStatusLamp()
    {
        InitializeComponent();
    }
}
