using System.Windows;
using 上位机.核心.抽象;

namespace 上位机.应用.视图;

/// <summary>
/// IO 通信诊断对话框：X / Y 段 × FC01 / FC02 的原始读数并排，附结论与配置建议。
///
/// <para><b>为什么做成对话框而不是日志：</b>"显示与物理对不上"是现场最难自查的一类问题——
/// 日志里只有"读成功 / 读失败"，看不出"读到的其实是另一块内存"。把两个功能码的原始位
/// 并排摆出来，现场 30 秒就能定位，不必再把 PLC 带回来。</para>
/// </summary>
public partial class IO诊断对话框 : Window
{
    private readonly IO诊断服务接口 _诊断;
    private readonly int _X基址;
    private readonly int _Y基址;

    private IO诊断对话框(IO诊断服务接口 诊断, int X基址, int Y基址)
    {
        InitializeComponent();

        _诊断 = 诊断;
        _X基址 = X基址;
        _Y基址 = Y基址;

        Owner = Application.Current.MainWindow;

        // 打开即诊断一次：现场点开就是想看结果，不该再按一次按钮
        _ = 异步诊断并展示();
    }

    /// <summary>弹出诊断对话框。</summary>
    public static void 显示(IO诊断服务接口 诊断, int X基址, int Y基址)
    {
        new IO诊断对话框(诊断, X基址, Y基址).ShowDialog();
    }

    private async Task 异步诊断并展示()
    {
        读数表格.ItemsSource = null;
        结论文本.Text = "正在读取……";

        var 报告 = await _诊断.异步诊断(_X基址, _Y基址);

        读数表格.ItemsSource = 报告.项集合;
        结论文本.Text = 报告.结论;
    }

    private void 重新诊断(object 发送者, RoutedEventArgs 事件参数) => _ = 异步诊断并展示();

    private void 关闭(object 发送者, RoutedEventArgs 事件参数) => Close();
}
