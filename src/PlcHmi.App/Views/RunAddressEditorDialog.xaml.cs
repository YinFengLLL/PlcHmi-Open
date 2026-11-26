using System.Windows;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Models;

namespace PlcHmi.App.Views;

/// <summary>
/// 运行监控页元件地址编辑器。
///
/// <para>只负责收集界面输入并就地写回传入列表的 <see cref="RunMonitorComponent.Address"/>；
/// 地址合法性（D/M 区、重叠、保留区）的校验在服务层——界面提示只是辅助，
/// 不当最后一道防线。</para>
/// </summary>
public partial class RunMonitorAddressEditDialog : Window
{
    private readonly IReadOnlyList<RunMonitorComponent> _componentList;

    private RunMonitorAddressEditDialog(IReadOnlyList<RunMonitorComponent> ComponentList)
    {
        InitializeComponent();

        _componentList = ComponentList;
        ComponentListWidget.ItemsSource = _componentList;
    }

    /// <summary>弹出编辑器。返回是否确定（确定时已就地修改传入列表的地址）。</summary>
    public static bool Show(IReadOnlyList<RunMonitorComponent> ComponentList)
    {
        var Dialog = new RunMonitorAddressEditDialog(ComponentList)
        {
            Owner = Application.Current.MainWindow,
        };

        return Dialog.ShowDialog() == true;
    }

    /// <summary>仅供自检使用：实例化但不显示（用于验证 XAML 资源能否解析）。</summary>
    internal static Window CreateVerify(IReadOnlyList<RunMonitorComponent> ComponentList) =>
        new RunMonitorAddressEditDialog(ComponentList);

    private void Confirm(object sender, RoutedEventArgs eventArgs)
    {
        var vacant = _componentList.FirstOrDefault(EntryItem => string.IsNullOrWhiteSpace(EntryItem.Address));
        if (vacant is not null)
        {
            MessageText.Text = $"「{vacant.DisplayName}」的地址不能为空。";
            return;
        }

        DialogResult = true;
    }

    private void Cancel(object sender, RoutedEventArgs eventArgs) => DialogResult = false;
}
