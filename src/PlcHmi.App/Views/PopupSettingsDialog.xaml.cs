using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using PlcHmi.Business.Services;
using PlcHmi.Core.Abstractions;

namespace PlcHmi.App.Views;

/// <summary>
/// 弹窗设置对话框（仅管理员入口）。
///
/// <para>新增 / 编辑 / 删除可配置弹窗：每个弹窗有一个 M 区<b>触发地址</b>（闭合即显示、断开即消失）、
/// 正文文案，以及两颗按钮——各配名称、M 区地址与<b>工作模式</b>（复归型 / 置位 ON / 复位 OFF）。</para>
///
/// <para>只负责收集与校验输入；落盘由调用方经 弹窗监控服务 原子写入 popups.json。</para>
/// </summary>
public partial class PopupSettingsDialog : Window
{
    /// <summary>一行的可编辑状态。</summary>
    public sealed partial class PopupRow : ObservableObject
    {
        private readonly PopupDefinition _definition;

        [ObservableProperty] private string _name = string.Empty;
        [ObservableProperty] private string _triggerAddress = string.Empty;
        [ObservableProperty] private string _content = string.Empty;
        [ObservableProperty] private string _button1Name = string.Empty;
        [ObservableProperty] private string _button1Address = string.Empty;
        [ObservableProperty] private string _button2Name = string.Empty;
        [ObservableProperty] private string _button2Address = string.Empty;

        /// <summary>按钮一模式索引（0 复归型 / 1 置位 ON / 2 复位 OFF）。</summary>
        [ObservableProperty] private int _button1ModeIndex;

        /// <summary>按钮二模式索引（同上）。</summary>
        [ObservableProperty] private int _button2ModeIndex;

        /// <summary>模式下拉数据源（与 <see cref="PopupDefinition.ModeOptions"/> 同一份）。</summary>
        public IReadOnlyList<string> ModeOptions => PopupDefinition.ModeOptions;

        public PopupRow(PopupDefinition Definition)
        {
            _definition = Definition;
            Name = Definition.Name;
            TriggerAddress = Definition.TriggerAddress;
            Content = Definition.Content;
            Button1Name = Definition.Button1Name;
            Button1Address = Definition.Button1Address;
            Button2Name = Definition.Button2Name;
            Button2Address = Definition.Button2Address;
            Button1ModeIndex = (int)Definition.Button1Mode;
            Button2ModeIndex = (int)Definition.Button2Mode;
        }

        /// <summary>底层定义（回写用）。</summary>
        public PopupDefinition Definition => _definition;
    }

    private readonly IAddressParser _parser;

    private PopupSettingsDialog(PopupMonitorService MonitorControl, IAddressParser Parser)
    {
        InitializeComponent();

        _parser = Parser;

        foreach (var Definition in MonitorControl.PopupCollection)
        {
            PopupCollection.Add(new PopupRow(Definition));
        }

        DataContext = this;
    }

    /// <summary>可编辑行集合。</summary>
    public ObservableCollection<PopupRow> PopupCollection { get; } = new();

    /// <summary>
    /// 弹出编辑器。返回编辑后的弹窗定义集合；<c>null</c> = 取消（调用方据此决定是否落盘）。
    /// </summary>
    public static IReadOnlyList<PopupDefinition>? Show(PopupMonitorService MonitorControl, IAddressParser Parser)
    {
        var Dialog = new PopupSettingsDialog(MonitorControl, Parser)
        {
            Owner = Application.Current.MainWindow,
        };

        return Dialog.ShowDialog() == true ? Dialog.CollectSet() : null;
    }

    /// <summary>仅供自检使用：实例化但不显示。</summary>
    internal static Window CreateVerify(PopupMonitorService MonitorControl, IAddressParser Parser) =>
        new PopupSettingsDialog(MonitorControl, Parser);

    private void AddPopup(object sender, RoutedEventArgs eventArgs)
    {
        var Sequence = PopupCollection.Count + 1;

        PopupCollection.Add(new PopupRow(new PopupDefinition
        {
            Name = $"新弹窗{Sequence}",
            TriggerAddress = "M0",
            Content = "请填写弹窗内容。",
            Button1Name = "确认",
            Button1Address = "M0",
            Button1Mode = PopupButtonMode.ReturnType,
        }));
    }

    private void DeletePopup(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is FrameworkElement ElementItem && ElementItem.DataContext is PopupRow Row)
        {
            PopupCollection.Remove(Row);
        }
    }

    private void Cancel(object sender, RoutedEventArgs eventArgs) => DialogResult = false;

    private void Confirm(object sender, RoutedEventArgs eventArgs)
    {
        foreach (var Row in PopupCollection)
        {
            if (string.IsNullOrWhiteSpace(Row.Name))
            {
                MessageText.Text = "弹窗名称不能为空。";
                return;
            }

            if (Validate(Row.TriggerAddress, $"「{Row.Name}」的触发地址") is { } triggerIssue)
            {
                MessageText.Text = triggerIssue;
                return;
            }

            if (string.IsNullOrWhiteSpace(Row.Button1Name))
            {
                MessageText.Text = $"「{Row.Name}」的按钮一名称不能为空。";
                return;
            }

            if (Validate(Row.Button1Address, $"「{Row.Name}」的按钮一地址") is { } button1Issue)
            {
                MessageText.Text = button1Issue;
                return;
            }

            // 按钮二：名称与地址要么都有、要么都空
            var hasTwoName = !string.IsNullOrWhiteSpace(Row.Button2Name);
            var hasTwoAddress = !string.IsNullOrWhiteSpace(Row.Button2Address);

            if (hasTwoName != hasTwoAddress)
            {
                MessageText.Text = $"「{Row.Name}」的按钮二：名称与地址必须同时填写，或同时留空。";
                return;
            }

            if (hasTwoAddress && Validate(Row.Button2Address, $"「{Row.Name}」的按钮二地址") is { } button2Issue)
            {
                MessageText.Text = button2Issue;
                return;
            }

            // 触发地址与按钮地址互不相同：同一线圈既触发又"确认"会让弹窗按住期间反复触发
            if (SameAddress(Row.TriggerAddress, Row.Button1Address) || SameAddress(Row.TriggerAddress, Row.Button2Address))
            {
                MessageText.Text = $"「{Row.Name}」的按钮地址不能与触发地址相同。";
                return;
            }
        }

        DialogResult = true;
    }

    /// <summary>地址校验：空地址不算错（触发地址必填在校验顺序里单独管），非法/非 M 区给原因。</summary>
    private string? Validate(string AddressText, string usePurpose)
    {
        if (string.IsNullOrWhiteSpace(AddressText))
        {
            return $"{usePurpose}不能为空。";
        }

        return PopupMonitorService.ValidateAddress(_parser, AddressText, usePurpose);
    }

    private bool SameAddress(string item, string item2) =>
        !string.IsNullOrWhiteSpace(item2)
        && string.Equals(item.Trim(), item2.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>把编辑结果回写成定义集合（确认后调用）。</summary>
    private IReadOnlyList<PopupDefinition> CollectSet()
    {
        var result = new List<PopupDefinition>();

        foreach (var Row in PopupCollection)
        {
            Row.Definition.Name = Row.Name.Trim();
            Row.Definition.TriggerAddress = Row.TriggerAddress.Trim();
            Row.Definition.Content = Row.Content.Trim();
            Row.Definition.Button1Name = Row.Button1Name.Trim();
            Row.Definition.Button1Address = Row.Button1Address.Trim();
            Row.Definition.Button1Mode = (PopupButtonMode)Math.Clamp(Row.Button1ModeIndex, 0, PopupDefinition.ModeOptions.Count - 1);
            Row.Definition.Button2Name = Row.Button2Name.Trim();
            Row.Definition.Button2Address = Row.Button2Address.Trim();
            Row.Definition.Button2Mode = (PopupButtonMode)Math.Clamp(Row.Button2ModeIndex, 0, PopupDefinition.ModeOptions.Count - 1);

            result.Add(Row.Definition);
        }

        return result;
    }
}
