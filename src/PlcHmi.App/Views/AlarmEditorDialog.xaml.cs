using System.Windows;
using System.Windows.Controls;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.App.Views;

/// <summary>
/// 报警定义编辑器。
///
/// <para>只负责收集界面输入；<b>地址区域校验在服务层</b>（必须落在 D550–D554 或 M500–M600）。
/// 这里做实时预览和基本必填检查，方便管理员边填边看。</para>
/// </summary>
public partial class AlarmEditorDialog : Window
{
    /// <summary>位号下拉：-1 表示不按位取（M 区用）。</summary>
    private static readonly object[] bitOptions =
        ["-1（整位／不按位取）", "0", "1", "2", "3", "4", "5", "6", "7",
         "8", "9", "10", "11", "12", "13", "14", "15"];

    private readonly AlarmDefinition _alarmItem;

    private AlarmEditorDialog(AlarmDefinition AlarmItem, bool isNewBuild)
    {
        InitializeComponent();

        _alarmItem = AlarmItem;

        Title = isNewBuild ? "新增报警定义" : "编辑报警定义";
        PageHeaderTitleText.Text = isNewBuild ? "新增报警" : "编辑报警";

        LevelFrame.ItemsSource = new[] { "提示 Info", "警告 Warning", "紧急 Critical" };
        LevelFrame.SelectedIndex = (int)AlarmItem.Level;

        BitFrame.ItemsSource = bitOptions;
        BitFrame.SelectedIndex = AlarmItem.Bit + 1;

        NumberFrame.Text = AlarmItem.Id;
        MessageFrame.Text = AlarmItem.Message;
        SuggestionFrame.Text = AlarmItem.Suggestion;
        AddressFrame.Text = AlarmItem.Address;
        EnabledFrame.IsChecked = AlarmItem.Enabled;

        // 编号是 Update 按 Id 定位的主键：编辑时锁定，只允许新增时填写。
        // 否则管理员改了编号，Update 按新编号找不到原条目，保存必然失败。
        if (!isNewBuild)
        {
            NumberFrame.IsEnabled = false;
            NumberFrame.ToolTip = "编号是报警的唯一标识，编辑时不可修改；如需换编号，请删除后重新新增。";
            NumberHintText.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) =>
        {
            MessageFrame.Focus();
            UpdatePreview();
        };
    }

    /// <summary>弹出编辑器。确定时已就地修改 <paramref name="AlarmItem"/>。</summary>
    /// <param name="isNew">新增传 <c>true</c>：编号可填写；编辑传 <c>false</c>：编号锁定。</param>
    public static bool Show(AlarmDefinition AlarmItem, bool isNewBuild)
    {
        var Dialog = new AlarmEditorDialog(AlarmItem, isNewBuild)
        {
            Owner = Application.Current.MainWindow,
        };

        if (Dialog.ShowDialog() != true)
        {
            return false;
        }

        AlarmItem.Id = Dialog.NumberFrame.Text.Trim();
        AlarmItem.Level = (AlarmLevel)Math.Max(0, Dialog.LevelFrame.SelectedIndex);
        AlarmItem.Message = Dialog.MessageFrame.Text.Trim();
        AlarmItem.Suggestion = Dialog.SuggestionFrame.Text.Trim();
        AlarmItem.Address = Dialog.AddressFrame.Text.Trim();
        AlarmItem.Bit = Dialog.BitFrame.SelectedIndex - 1;
        AlarmItem.Enabled = Dialog.EnabledFrame.IsChecked ?? false;

        return true;
    }

    /// <summary>仅供自检使用：实例化但不显示（用于验证 XAML 资源能否解析）。</summary>
    internal static Window CreateVerify(AlarmDefinition AlarmItem, bool isNewBuild = true) =>
        new AlarmEditorDialog(AlarmItem, isNewBuild);

    /// <summary>地址或文本变动时更新预览，让管理员看到最终显示效果。</summary>
    private void UpdatePreview()
    {
        if (PreviewText is null)
        {
            return;
        }

        var addressValue = AddressFrame.Text.Trim();
        var Bit = BitFrame.SelectedIndex - 1;

        var Source = Bit >= 0 ? $"{addressValue}.{Bit}" : addressValue;
        var Message = string.IsNullOrWhiteSpace(MessageFrame.Text) ? "（未填写报警文本）" : MessageFrame.Text.Trim();

        PreviewText.Text = $"[{Source}] {Message}";
    }

    private void AddressChanged(object sender, TextChangedEventArgs eventArgs) => UpdatePreview();

    private void BitChanged(object sender, SelectionChangedEventArgs eventArgs) => UpdatePreview();

    private void MessageChanged(object sender, TextChangedEventArgs eventArgs) => UpdatePreview();

    private void Confirm(object sender, RoutedEventArgs eventArgs)
    {
        if (string.IsNullOrWhiteSpace(NumberFrame.Text))
        {
            MessageText.Text = "请填写报警编号。";
            return;
        }

        if (string.IsNullOrWhiteSpace(MessageFrame.Text))
        {
            MessageText.Text = "请填写报警文本。";
            return;
        }

        if (string.IsNullOrWhiteSpace(AddressFrame.Text))
        {
            MessageText.Text = "请填写读取地址。";
            return;
        }

        DialogResult = true;
    }

    private void Cancel(object sender, RoutedEventArgs eventArgs) => DialogResult = false;
}
