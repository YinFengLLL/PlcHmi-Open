using System.Globalization;
using System.Windows;
using PlcHmi.Core.Models;

namespace PlcHmi.App.Views;

/// <summary>
/// 其它点（辅助点）编辑器。
///
/// <para>只负责收集界面输入并就地写回传入的 <see cref="AuxPointDefinition"/>；
/// 地址合法性与权限守卫在服务层——界面提示只是辅助。</para>
///
/// <para><b>名称可改（2026-10-15 现场反馈）</b>：原先编辑时锁死名称（当时把名称当主键），
/// 现场改不了点位的叫法。现在名称只是标识，重命名由服务层按原名定位后整体替换；
/// 是否与其它点重名由服务层判定。</para>
/// </summary>
public partial class AuxPointEditDialog : Window
{
    private readonly AuxPointDefinition _pointEntry;

    private AuxPointEditDialog(AuxPointDefinition PointEntry, bool isNewBuild)
    {
        InitializeComponent();

        _pointEntry = PointEntry;
        TitleWidget.Text = isNewBuild ? "新增其它点" : "编辑其它点";
        NameFrame.Text = PointEntry.Name;
        XFrame.Text = PointEntry.XBaseAddress.ToString(CultureInfo.InvariantCulture);
        YFrame.Text = PointEntry.YBaseAddress.ToString(CultureInfo.InvariantCulture);
        ZFrame.Text = PointEntry.ZBaseAddress.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>弹出编辑器。返回是否确定（确定时已就地修改传入对象）。</summary>
    public static bool Show(AuxPointDefinition PointEntry, bool isNewBuild)
    {
        var Dialog = new AuxPointEditDialog(PointEntry, isNewBuild)
        {
            Owner = Application.Current.MainWindow,
        };

        return Dialog.ShowDialog() == true;
    }

    /// <summary>仅供自检使用：实例化但不显示。</summary>
    internal static Window CreateVerify() => new AuxPointEditDialog(new AuxPointDefinition { Name = "自检点" }, true);

    private void Confirm(object sender, RoutedEventArgs eventArgs)
    {
        var nameValue = NameFrame.Text?.Trim() ?? string.Empty;

        if (nameValue.Length == 0)
        {
            MessageText.Text = "名称不能为空。";
            return;
        }

        if (!TryGetAddress(XFrame.Text, out var X) || !TryGetAddress(YFrame.Text, out var Y) || !TryGetAddress(ZFrame.Text, out var Z))
        {
            MessageText.Text = "X / Y / Z 基址必须是正整数（D 区序号）。";
            return;
        }

        _pointEntry.Name = nameValue;
        _pointEntry.XBaseAddress = X;
        _pointEntry.YBaseAddress = Y;
        _pointEntry.ZBaseAddress = Z;

        DialogResult = true;
    }

    private static bool TryGetAddress(string? Text, out int Value) =>
        int.TryParse((Text ?? string.Empty).Trim().TrimStart('D', 'd', 'M', 'm'),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out Value) && Value > 0;

    private void Cancel(object sender, RoutedEventArgs eventArgs) => DialogResult = false;
}
