using System.Windows;
using PlcHmi.Core.Abstractions;

namespace PlcHmi.App.Views;

/// <summary>
/// IO 通信诊断对话框：X / Y 段 × FC01 / FC02 的原始读数并排，附结论与配置建议。
///
/// <para><b>为什么做成对话框而不是日志：</b>"显示与物理对不上"是现场最难自查的一类问题——
/// 日志里只有"读成功 / 读失败"，看不出"读到的其实是另一块内存"。把两个功能码的原始位
/// 并排摆出来，现场 30 秒就能定位，不必再把 PLC 带回来。</para>
/// </summary>
public partial class IoDiagnosticDialog : Window
{
    private readonly IIoDiagnosticService _diagnostic;
    private readonly int _xBaseAddress;
    private readonly int _yBaseAddress;

    private IoDiagnosticDialog(IIoDiagnosticService Diagnostic, int XBaseAddress, int YBaseAddress)
    {
        InitializeComponent();

        _diagnostic = Diagnostic;
        _xBaseAddress = XBaseAddress;
        _yBaseAddress = YBaseAddress;

        Owner = Application.Current.MainWindow;

        // 打开即诊断一次：现场点开就是想看结果，不该再按一次按钮
        _ = DiagnosticAndShowAsync();
    }

    /// <summary>弹出诊断对话框。</summary>
    public static void Show(IIoDiagnosticService Diagnostic, int XBaseAddress, int YBaseAddress)
    {
        new IoDiagnosticDialog(Diagnostic, XBaseAddress, YBaseAddress).ShowDialog();
    }

    private async Task DiagnosticAndShowAsync()
    {
        ReadNumberTable.ItemsSource = null;
        ConclusionText.Text = "正在读取……";

        var report = await _diagnostic.DiagnosticAsync(_xBaseAddress, _yBaseAddress);

        ReadNumberTable.ItemsSource = report.ItemCollection;
        ConclusionText.Text = report.Conclusion;
    }

    private void ReDiagnostic(object sender, RoutedEventArgs eventArgs) => _ = DiagnosticAndShowAsync();

    private void Close(object sender, RoutedEventArgs eventArgs) => Close();
}
