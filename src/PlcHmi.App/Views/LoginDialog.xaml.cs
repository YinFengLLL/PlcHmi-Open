using System.Windows;
using System.Windows.Input;
using PlcHmi.Core.Abstractions;

namespace PlcHmi.App.Views;

/// <summary>
/// 登录对话框。
///
/// <para>取消 / 跳过 = 以操作员身份进入（最低权限），不阻断使用。</para>
/// <para>按需求方确认的策略：<b>密码登录，不超时回落</b>——适合无人看守的触摸屏。</para>
/// </summary>
public partial class LoginDialog : Window
{
    private readonly IUserService _user;

    private LoginDialog(IUserService User)
    {
        InitializeComponent();

        _user = User;

        AccountNumberFrame.ItemsSource = User.AccountNumberCollection.Select(item => item.Name).ToList();
        AccountNumberFrame.SelectionChanged += (_, _) => UpdateDefaultPasswordHint();

        if (AccountNumberFrame.Items.Count > 0)
        {
            AccountNumberFrame.SelectedIndex = 0;
        }

        Loaded += (_, _) => PasswordFrame.Focus();
    }

    /// <summary>
    /// 只有当所选账号仍使用初始密码时才提示尽快改密——
    /// 不再把初始密码明文印在公共屏幕上，也不给未改密的账号留"看起来安全"的假象。
    /// </summary>
    private void UpdateDefaultPasswordHint()
    {
        var nameValue = AccountNumberFrame.SelectedItem as string;
        var accountNumberItem = _user.AccountNumberCollection.FirstOrDefault(item => item.Name == nameValue);
        DefaultPasswordHint.Visibility = accountNumberItem?.IsDefaultPassword == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>弹出登录框。返回是否登录成功。</summary>
    public static bool Show(IUserService User)
    {
        var Dialog = new LoginDialog(User)
        {
            Owner = Application.Current.MainWindow,
        };

        return Dialog.ShowDialog() == true;
    }

    /// <summary>
    /// 仅供自检使用：实例化但不显示。
    ///
    /// <para>XAML 里的 <c>{StaticResource}</c> 在加载时就会求值——写错键名会当场抛异常。
    /// 自检时 new 一次即可把这类"只在打开对话框时才炸"的 bug 提前暴露。</para>
    /// </summary>
    internal static Window CreateVerify(IUserService User) => new LoginDialog(User);

    private async void Confirm(object sender, RoutedEventArgs eventArgs) => await TryLoginAsync();

    private async void PasswordKeyPress(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Enter)
        {
            await TryLoginAsync();
        }
    }

    /// <summary>跳登录，以操作员身份进入。</summary>
    private void Skip(object sender, RoutedEventArgs eventArgs) => DialogResult = false;

    /// <summary>
    /// 尝试登录。
    ///
    /// <para><b>必须自己兜住异常</b>：这是 <c>async void</c> 事件链的一环，
    /// 异常直接抛到 SynchronizationContext 会让整个程序崩掉，
    /// 而不是仅仅提示"登录失败"。</para>
    /// </summary>
    private async Task TryLoginAsync()
    {
        try
        {
            var nameValue = AccountNumberFrame.SelectedItem as string ?? string.Empty;
            var password = PasswordFrame.Password;

            if (string.IsNullOrWhiteSpace(nameValue))
            {
                MessageText.Text = "请选择账号。";
                return;
            }

            MessageText.Text = "正在验证……";

            if (!await _user.LoginAsync(nameValue, password))
            {
                MessageText.Text = "账号或密码不正确。";
                PasswordFrame.Clear();
                PasswordFrame.Focus();
                return;
            }

            DialogResult = true;
        }
        catch (Exception exception)
        {
            MessageText.Text = $"登录失败：{exception.Message}";
        }
    }
}
