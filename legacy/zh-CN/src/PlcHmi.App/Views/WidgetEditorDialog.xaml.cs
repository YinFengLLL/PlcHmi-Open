using System.Windows;
using System.Windows.Controls;
using 上位机.核心.模型;

namespace 上位机.应用.视图;

/// <summary>
/// 手动页元件编辑器。
///
/// <para>只负责收集界面输入并写回传入的对象；<b>地址是否为 M 区的校验在服务层</b>——
/// 界面提示只是辅助，不当最后一道防线。</para>
///
/// <para><b>变量绑定</b>：通过「选变量…」从变量表选取变量后，地址与变量名同时写入；
/// 若之后手工改动地址文本，绑定自动解除（防止“变量说 A、地址写 B”的漂移）。</para>
/// </summary>
public partial class 元件编辑器对话框 : Window
{
    private readonly 手动元件定义 _元件项;
    private readonly IReadOnlyList<变量表条目>? _变量列表;

    /// <summary>选变量后暂存的绑定名，确认时写回元件。</summary>
    private string _控制变量 = string.Empty;
    private string _状态变量 = string.Empty;

    private 元件编辑器对话框(手动元件定义 元件项, IReadOnlyList<变量表条目>? 变量列表)
    {
        InitializeComponent();

        _元件项 = 元件项;
        _变量列表 = 变量列表;
        _控制变量 = 元件项.变量;
        _状态变量 = 元件项.状态值变量;

        类型框.ItemsSource = new[] { "气缸（伸出 / 缩回）", "按钮（脉冲触发）", "指示灯（只读监测）" };
        类型框.SelectedIndex = (int)元件项.类型;

        名称框.Text = 元件项.名称;
        扩展框.Text = 元件项.地址;
        状态框.Text = 元件项.状态地址;

        更新字段可见性();
    }

    /// <summary>弹出编辑器。返回是否确定（确定时已就地修改 <paramref name="元件项"/>）。</summary>
    public static bool 显示(手动元件定义 元件项, IReadOnlyList<变量表条目>? 变量列表 = null)
    {
        var 对话框 = new 元件编辑器对话框(元件项, 变量列表)
        {
            Owner = Application.Current.MainWindow,
        };

        if (对话框.ShowDialog() != true)
        {
            return false;
        }

        元件项.类型 = (手动元件类型)对话框.类型框.SelectedIndex;
        元件项.名称 = 对话框.名称框.Text.Trim();
        元件项.地址 = 对话框.扩展框.Text.Trim();
        元件项.状态地址 = 对话框.状态框.Text.Trim();
        元件项.变量 = 对话框._控制变量;
        元件项.状态值变量 = 对话框._状态变量;

        return true;
    }

    /// <summary>仅供自检使用：实例化但不显示（用于验证 XAML 资源能否解析）。</summary>
    internal static Window 创建验证(手动元件定义 元件项) =>
        new 元件编辑器对话框(元件项, null);

    /// <summary>按类型切换需要填的字段。</summary>
    private void 类型已变更(object 发送者, SelectionChangedEventArgs 事件参数) => 更新字段可见性();

    private void 更新字段可见性()
    {
        if (扩展面板 is null || 状态面板 is null)
        {
            return;   // 初始化过程中控件尚未就绪
        }

        var type = (手动元件类型)Math.Max(0, 类型框.SelectedIndex);

        状态面板.Visibility = type == 手动元件类型.指示灯 ? Visibility.Visible : Visibility.Collapsed;
        扩展面板.Visibility = type == 手动元件类型.指示灯 ? Visibility.Collapsed : Visibility.Visible;

        switch (type)
        {
            case 手动元件类型.气缸:
                扩展标签.Text = "控制地址（M 区）";
                扩展提示.Text = "单线圈气缸：置位该位 = 伸出，复位 = 缩回。";
                break;

            case 手动元件类型.按钮:
                扩展标签.Text = "触发地址（M 区）";
                扩展提示.Text = "按下时该位发出一个短脉冲。";
                break;

            default:
                扩展提示.Text = string.Empty;
                break;
        }

        消息文本.Text = string.Empty;
    }

    // ── 变量绑定 ────────────────────────────────────────────────────────

    private void 扩展地址选变量(object 发送者, RoutedEventArgs 事件参数)
    {
        if (变量选择对话框.显示(_变量列表 ?? [], "为控制地址选择变量") is not { } 条目)
        {
            return;
        }

        _控制变量 = 条目.变量名;
        扩展框.Text = 条目.软元件地址;
        扩展提示.Text = $"已绑定变量 {条目.变量名}（{条目.数据类型}）";
    }

    private void 状态地址选变量(object 发送者, RoutedEventArgs 事件参数)
    {
        if (变量选择对话框.显示(_变量列表 ?? [], "为监测地址选择变量") is not { } 条目)
        {
            return;
        }

        _状态变量 = 条目.变量名;
        状态框.Text = 条目.软元件地址;
    }

    /// <summary>手工改动地址时解除变量绑定，避免“变量说 A、地址写 B”。</summary>
    private void 地址已手动修改(object 发送者, TextChangedEventArgs 事件参数)
    {
        if (_控制变量.Length == 0 || 扩展框 is null)
        {
            return;
        }

        var 条目 = _变量列表?.FirstOrDefault(变量项 =>
            string.Equals(变量项.变量名, _控制变量, StringComparison.OrdinalIgnoreCase));

        if (条目 is null || !string.Equals(扩展框.Text.Trim(), 条目.软元件地址, StringComparison.OrdinalIgnoreCase))
        {
            _控制变量 = string.Empty;
        }
    }

    /// <summary>手工改动监测地址时解除变量绑定（同上）。</summary>
    private void 状态地址已手动修改(object 发送者, TextChangedEventArgs 事件参数)
    {
        if (_状态变量.Length == 0 || 状态框 is null)
        {
            return;
        }

        var 条目 = _变量列表?.FirstOrDefault(变量项 =>
            string.Equals(变量项.变量名, _状态变量, StringComparison.OrdinalIgnoreCase));

        if (条目 is null || !string.Equals(状态框.Text.Trim(), 条目.软元件地址, StringComparison.OrdinalIgnoreCase))
        {
            _状态变量 = string.Empty;
        }
    }

    private void 确认(object 发送者, RoutedEventArgs 事件参数)
    {
        if (string.IsNullOrWhiteSpace(名称框.Text))
        {
            消息文本.Text = "请填写显示名称。";
            return;
        }

        var type = (手动元件类型)Math.Max(0, 类型框.SelectedIndex);

        if (type != 手动元件类型.指示灯 && string.IsNullOrWhiteSpace(扩展框.Text))
        {
            消息文本.Text = type == 手动元件类型.按钮 ? "请填写触发地址。" : "请填写控制地址。";
            return;
        }

        if (type == 手动元件类型.指示灯 && string.IsNullOrWhiteSpace(状态框.Text))
        {
            消息文本.Text = "请填写监测地址。";
            return;
        }

        DialogResult = true;
    }

    private void 取消(object 发送者, RoutedEventArgs 事件参数) => DialogResult = false;
}
