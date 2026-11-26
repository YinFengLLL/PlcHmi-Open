using System.Windows;
using System.Windows.Controls;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.应用.视图;

/// <summary>
/// 报警定义编辑器。
///
/// <para>只负责收集界面输入；<b>地址区域校验在服务层</b>（必须落在 D550–D554 或 M500–M600）。
/// 这里做实时预览和基本必填检查，方便管理员边填边看。</para>
/// </summary>
public partial class 报警编辑器对话框 : Window
{
    /// <summary>位号下拉：-1 表示不按位取（M 区用）。</summary>
    private static readonly object[] 位选项 =
        ["-1（整位／不按位取）", "0", "1", "2", "3", "4", "5", "6", "7",
         "8", "9", "10", "11", "12", "13", "14", "15"];

    private readonly 报警定义 _报警项;

    private 报警编辑器对话框(报警定义 报警项, bool 是否新建)
    {
        InitializeComponent();

        _报警项 = 报警项;

        Title = 是否新建 ? "新增报警定义" : "编辑报警定义";
        页眉标题文本.Text = 是否新建 ? "新增报警" : "编辑报警";

        等级框.ItemsSource = new[] { "提示 Info", "警告 Warning", "紧急 Critical" };
        等级框.SelectedIndex = (int)报警项.等级;

        位框.ItemsSource = 位选项;
        位框.SelectedIndex = 报警项.位 + 1;

        编号框.Text = 报警项.标识;
        消息框.Text = 报警项.消息;
        建议框.Text = 报警项.建议;
        地址框.Text = 报警项.地址;
        启用框.IsChecked = 报警项.启用;

        // 编号是 Update 按 Id 定位的主键：编辑时锁定，只允许新增时填写。
        // 否则管理员改了编号，Update 按新编号找不到原条目，保存必然失败。
        if (!是否新建)
        {
            编号框.IsEnabled = false;
            编号框.ToolTip = "编号是报警的唯一标识，编辑时不可修改；如需换编号，请删除后重新新增。";
            编号提示文本.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) =>
        {
            消息框.Focus();
            更新预览();
        };
    }

    /// <summary>弹出编辑器。确定时已就地修改 <paramref name="报警项"/>。</summary>
    /// <param name="isNew">新增传 <c>true</c>：编号可填写；编辑传 <c>false</c>：编号锁定。</param>
    public static bool 显示(报警定义 报警项, bool 是否新建)
    {
        var 对话框 = new 报警编辑器对话框(报警项, 是否新建)
        {
            Owner = Application.Current.MainWindow,
        };

        if (对话框.ShowDialog() != true)
        {
            return false;
        }

        报警项.标识 = 对话框.编号框.Text.Trim();
        报警项.等级 = (报警等级)Math.Max(0, 对话框.等级框.SelectedIndex);
        报警项.消息 = 对话框.消息框.Text.Trim();
        报警项.建议 = 对话框.建议框.Text.Trim();
        报警项.地址 = 对话框.地址框.Text.Trim();
        报警项.位 = 对话框.位框.SelectedIndex - 1;
        报警项.启用 = 对话框.启用框.IsChecked ?? false;

        return true;
    }

    /// <summary>仅供自检使用：实例化但不显示（用于验证 XAML 资源能否解析）。</summary>
    internal static Window 创建验证(报警定义 报警项, bool 是否新建 = true) =>
        new 报警编辑器对话框(报警项, 是否新建);

    /// <summary>地址或文本变动时更新预览，让管理员看到最终显示效果。</summary>
    private void 更新预览()
    {
        if (预览文本 is null)
        {
            return;
        }

        var 地址值 = 地址框.Text.Trim();
        var 位 = 位框.SelectedIndex - 1;

        var 来源 = 位 >= 0 ? $"{地址值}.{位}" : 地址值;
        var 消息 = string.IsNullOrWhiteSpace(消息框.Text) ? "（未填写报警文本）" : 消息框.Text.Trim();

        预览文本.Text = $"[{来源}] {消息}";
    }

    private void 地址已变更(object 发送者, TextChangedEventArgs 事件参数) => 更新预览();

    private void 位已变更(object 发送者, SelectionChangedEventArgs 事件参数) => 更新预览();

    private void 消息已变更(object 发送者, TextChangedEventArgs 事件参数) => 更新预览();

    private void 确认(object 发送者, RoutedEventArgs 事件参数)
    {
        if (string.IsNullOrWhiteSpace(编号框.Text))
        {
            消息文本.Text = "请填写报警编号。";
            return;
        }

        if (string.IsNullOrWhiteSpace(消息框.Text))
        {
            消息文本.Text = "请填写报警文本。";
            return;
        }

        if (string.IsNullOrWhiteSpace(地址框.Text))
        {
            消息文本.Text = "请填写读取地址。";
            return;
        }

        DialogResult = true;
    }

    private void 取消(object 发送者, RoutedEventArgs 事件参数) => DialogResult = false;
}
