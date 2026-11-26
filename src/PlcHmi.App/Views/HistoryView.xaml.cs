using System.Windows.Controls;

namespace PlcHmi.App.Views;

/// <summary>历史记录页。逻辑全部在 <c>历史记录视图模型</c>，此处只做 XAML 装载。</summary>
public partial class HistoryRecordView : UserControl
{
    public HistoryRecordView()
    {
        InitializeComponent();
    }
}
