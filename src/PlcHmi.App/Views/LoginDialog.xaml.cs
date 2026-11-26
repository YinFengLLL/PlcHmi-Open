using System.Windows;
using System.Windows.Input;
using 上位机.核心.抽象;

namespace 上位机.应用.视图;

/// <summary>
/// 登录对话框。
///
/// <para>取消 / 跳过 = 以操作员身份进入（最低权限），不阻断使用。</para>
/// <para>按需求方确认的策略：<b>密码登录，不超时回落</b>——适合无人看守的触摸屏。</para>
/// </summary>
public partial class 登录对话框 : Window
{
    private readonly 用户服务接口 _用户;

    private 登录对话框(用户服务接口 用户)
    {
        InitializeComponent();

        _用户 = 用户;

        账号框.ItemsSource = 用户.账号集合.Select(甲 => 甲.名称).ToList();
        账号框.SelectionChanged += (_, _) => 更新默认密码提示();

        if (账号框.Items.Count > 0)
        {
            账号框.SelectedIndex = 0;
        }

        Loaded += (_, _) => 密码框.Focus();
    }

    /// <summary>
    /// 只有当所选账号仍使用初始密码时才提示尽快改密——
    /// 不再把初始密码明文印在公共屏幕上，也不给未改密的账号留"看起来安全"的假象。
    /// </summary>
    private void 更新默认密码提示()
    {
        var 名称值 = 账号框.SelectedItem as string;
        var 账号项 = _用户.账号集合.FirstOrDefault(甲 => 甲.名称 == 名称值);
        默认密码提示.Visibility = 账号项?.是否默认密码 == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>弹出登录框。返回是否登录成功。</summary>
    public static bool 显示(用户服务接口 用户)
    {
        var 对话框 = new 登录对话框(用户)
        {
            Owner = Application.Current.MainWindow,
        };

        return 对话框.ShowDialog() == true;
    }

    /// <summary>
    /// 仅供自检使用：实例化但不显示。
    ///
    /// <para>XAML 里的 <c>{StaticResource}</c> 在加载时就会求值——写错键名会当场抛异常。
    /// 自检时 new 一次即可把这类"只在打开对话框时才炸"的 bug 提前暴露。</para>
    /// </summary>
    internal static Window 创建验证(用户服务接口 用户) => new 登录对话框(用户);

    private async void 确认(object 发送者, RoutedEventArgs 事件参数) => await 异步尝试登录();

    private async void 密码键按下(object 发送者, KeyEventArgs 事件参数)
    {
        if (事件参数.Key == Key.Enter)
        {
            await 异步尝试登录();
        }
    }

    /// <summary>跳登录，以操作员身份进入。</summary>
    private void 跳过(object 发送者, RoutedEventArgs 事件参数) => DialogResult = false;

    /// <summary>
    /// 尝试登录。
    ///
    /// <para><b>必须自己兜住异常</b>：这是 <c>async void</c> 事件链的一环，
    /// 异常直接抛到 SynchronizationContext 会让整个程序崩掉，
    /// 而不是仅仅提示"登录失败"。</para>
    /// </summary>
    private async Task 异步尝试登录()
    {
        try
        {
            var 名称值 = 账号框.SelectedItem as string ?? string.Empty;
            var 密码 = 密码框.Password;

            if (string.IsNullOrWhiteSpace(名称值))
            {
                消息文本.Text = "请选择账号。";
                return;
            }

            消息文本.Text = "正在验证……";

            if (!await _用户.异步登录(名称值, 密码))
            {
                消息文本.Text = "账号或密码不正确。";
                密码框.Clear();
                密码框.Focus();
                return;
            }

            DialogResult = true;
        }
        catch (Exception 异常)
        {
            消息文本.Text = $"登录失败：{异常.Message}";
        }
    }
}
