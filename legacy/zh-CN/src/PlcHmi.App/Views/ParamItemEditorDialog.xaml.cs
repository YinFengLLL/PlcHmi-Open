using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using 上位机.核心.模型;

namespace 上位机.应用.视图;

/// <summary>
/// 参数页条目编辑器（延时 / 按钮共用）。
///
/// <para>地址区校验在服务层（延时必须 D 区、按钮必须 M 区），这里只做必填与数值解析。</para>
///
/// <para><b>变量绑定</b>：通过「选变量…」绑定变量后，地址与变量名同时写入；
/// 手工改动地址文本会自动解除绑定（防止“变量说 A、地址写 B”）。</para>
/// </summary>
public partial class 参数条目编辑器对话框 : Window
{
    private readonly 参数表格类别 _类别;
    private readonly 参数条目定义 _条目项;
    private readonly IReadOnlyList<变量表条目>? _变量列表;

    /// <summary>选变量后暂存的绑定名，确认时写回条目。</summary>
    private string _变量 = string.Empty;

    private 参数条目编辑器对话框(参数表格类别 类别, 参数条目定义 条目, IReadOnlyList<变量表条目>? 变量列表)
    {
        InitializeComponent();

        _类别 = 类别;
        _条目项 = 条目;
        _变量列表 = 变量列表;
        _变量 = 条目.变量;

        var 是否延迟 = 类别 == 参数表格类别.延迟;

        页眉文本.Text = 是否延迟 ? "编辑延时" : "编辑按钮";
        提示文本.Text = 是否延迟
            ? "延时用于动作之间的等待，值保存在 PLC 的 D 区（32 位整数毫秒 / DINT，占 2 个字，低字在前）。"
              + "地址须落在 D1000–D1098 且与其它延时相隔 2（如 D1000 / D1002），否则会互相覆盖高位字。"
            : "按钮用于触发动作，绑定 M 区内部继电器，由 PLC 决定何时驱动输出。";

        地址标签.Text = 是否延迟 ? "地址（D 区）" : "地址（M 区）";

        名称框.Text = 条目.名称;
        地址框.Text = 条目.地址;
        值框.Text = 条目.值.ToString("0.###", CultureInfo.InvariantCulture);
        最小值框.Text = 条目.最小.ToString("0.###", CultureInfo.InvariantCulture);
        最大值框.Text = 条目.最大.ToString("0.###", CultureInfo.InvariantCulture);
        启用框.IsChecked = 条目.启用;

        单位框.ItemsSource = new[] { "ms", "s" };
        单位框.SelectedItem = 条目.单元;

        if (单位框.SelectedIndex < 0)
        {
            单位框.SelectedIndex = 0;
        }

        // 按钮没有"值"与"范围"的概念
        值面板.Visibility = 是否延迟 ? Visibility.Visible : Visibility.Collapsed;
        范围面板.Visibility = 是否延迟 ? Visibility.Visible : Visibility.Collapsed;

        Height = 是否延迟 ? 560 : 460;

        Loaded += (_, _) => 名称框.Focus();
    }

    /// <summary>弹出编辑器。确定时已就地修改 <paramref name="条目"/>。</summary>
    public static bool 显示(参数表格类别 类别, 参数条目定义 条目, IReadOnlyList<变量表条目>? 变量列表 = null)
    {
        var 对话框 = new 参数条目编辑器对话框(类别, 条目, 变量列表)
        {
            Owner = Application.Current.MainWindow,
        };

        if (对话框.ShowDialog() != true)
        {
            return false;
        }

        条目.名称 = 对话框.名称框.Text.Trim();
        条目.地址 = 对话框.地址框.Text.Trim();
        条目.变量 = 对话框._变量;
        条目.启用 = 对话框.启用框.IsChecked ?? false;
        条目.单元 = 对话框.单位框.SelectedItem as string ?? "ms";

        // 解析失败时保留原值；真正的范围校验在服务层
        条目.值 = 解析(对话框.值框.Text, 条目.值);
        条目.最小 = 解析(对话框.最小值框.Text, 条目.最小);
        条目.最大 = 解析(对话框.最大值框.Text, 条目.最大);

        return true;
    }

    /// <summary>仅供自检使用：实例化但不显示（用于验证 XAML 资源能否解析）。</summary>
    internal static Window 创建验证(参数表格类别 类别, 参数条目定义 条目) =>
        new 参数条目编辑器对话框(类别, 条目, null);

    // ── 变量绑定 ────────────────────────────────────────────────────────

    private void 地址选变量(object 发送者, RoutedEventArgs 事件参数)
    {
        if (变量选择对话框.显示(_变量列表 ?? [], "为地址选择变量") is not { } 条目)
        {
            return;
        }

        _变量 = 条目.变量名;
        地址框.Text = 条目.软元件地址;
        绑定提示文本.Text = $"已绑定变量 {条目.变量名}（{条目.数据类型}）";
    }

    /// <summary>手工改动地址时解除变量绑定，避免“变量说 A、地址写 B”。</summary>
    private void 地址已手动修改(object 发送者, TextChangedEventArgs 事件参数)
    {
        if (_变量.Length == 0 || 地址框 is null)
        {
            return;
        }

        var 条目 = _变量列表?.FirstOrDefault(变量项 =>
            string.Equals(变量项.变量名, _变量, StringComparison.OrdinalIgnoreCase));

        if (条目 is null || !string.Equals(地址框.Text.Trim(), 条目.软元件地址, StringComparison.OrdinalIgnoreCase))
        {
            _变量 = string.Empty;
            绑定提示文本.Text = string.Empty;
        }
    }

    private static double 解析(string 文本, double 回退) =>
        double.TryParse(文本, NumberStyles.Float, CultureInfo.InvariantCulture, out var 值)
            ? 值
            : 回退;

    private void 确认(object 发送者, RoutedEventArgs 事件参数)
    {
        if (string.IsNullOrWhiteSpace(名称框.Text))
        {
            消息文本.Text = "请填写名称。";
            return;
        }

        if (string.IsNullOrWhiteSpace(地址框.Text))
        {
            消息文本.Text = "请填写地址。";
            return;
        }

        if (_类别 == 参数表格类别.延迟)
        {
            if (!尝试读取(值框.Text, out _))
            {
                消息文本.Text = "当前值必须是数字。";
                return;
            }

            if (!尝试读取(最小值框.Text, out _) || !尝试读取(最大值框.Text, out _))
            {
                消息文本.Text = "最小值与最大值必须是数字。";
                return;
            }
        }

        DialogResult = true;
    }

    private static bool 尝试读取(string 文本, out double 值) =>
        double.TryParse(文本, NumberStyles.Float, CultureInfo.InvariantCulture, out 值);

    private void 取消(object 发送者, RoutedEventArgs 事件参数) => DialogResult = false;
}
