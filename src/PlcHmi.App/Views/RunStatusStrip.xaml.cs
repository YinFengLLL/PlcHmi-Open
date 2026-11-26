namespace PlcHmi.App.Views;

/// <summary>
/// 运行监控页左下角状态条：通信 / 锁附 / 点胶 / 加工 / 已锁附颗数 / 报警。
///
/// <para><b>2026-10-16 现场要求：</b>在通讯状态灯之后补三盏工位指示灯
/// （<c>0</c> = 未完成、<c>1</c> = 完成）与「已锁附颗数」（读一个 INT 型地址）。</para>
///
/// <para>数据全部来自页面 DataContext（三盏灯、颗数、报警）与主窗口 DataContext（通信），
/// 因此这里不定义依赖属性、也没有代码后置逻辑——控件自身不持有任何状态。</para>
/// </summary>
public partial class RunStatusStrip : System.Windows.Controls.UserControl
{
    /// <summary>初始化组件。</summary>
    public RunStatusStrip()
    {
        InitializeComponent();
    }
}
