using System.Globalization;
using System.Windows;
using 上位机.核心.模型;

namespace 上位机.应用.视图;

/// <summary>
/// 其它点（辅助点）编辑器。
///
/// <para>只负责收集界面输入并就地写回传入的 <see cref="辅助点定义"/>；
/// 地址合法性与权限守卫在服务层——界面提示只是辅助。</para>
///
/// <para><b>名称可改（2026-10-15 现场反馈）</b>：原先编辑时锁死名称（当时把名称当主键），
/// 现场改不了点位的叫法。现在名称只是标识，重命名由服务层按原名定位后整体替换；
/// 是否与其它点重名由服务层判定。</para>
/// </summary>
public partial class 辅助点编辑对话框 : Window
{
    private readonly 辅助点定义 _点项;

    private 辅助点编辑对话框(辅助点定义 点项, bool 是否新建)
    {
        InitializeComponent();

        _点项 = 点项;
        标题控件.Text = 是否新建 ? "新增其它点" : "编辑其它点";
        名称框.Text = 点项.名称;
        X框.Text = 点项.X基址.ToString(CultureInfo.InvariantCulture);
        Y框.Text = 点项.Y基址.ToString(CultureInfo.InvariantCulture);
        Z框.Text = 点项.Z基址.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>弹出编辑器。返回是否确定（确定时已就地修改传入对象）。</summary>
    public static bool 显示(辅助点定义 点项, bool 是否新建)
    {
        var 对话框 = new 辅助点编辑对话框(点项, 是否新建)
        {
            Owner = Application.Current.MainWindow,
        };

        return 对话框.ShowDialog() == true;
    }

    /// <summary>仅供自检使用：实例化但不显示。</summary>
    internal static Window 创建验证() => new 辅助点编辑对话框(new 辅助点定义 { 名称 = "自检点" }, true);

    private void 确认(object 发送者, RoutedEventArgs 事件参数)
    {
        var 名称值 = 名称框.Text?.Trim() ?? string.Empty;

        if (名称值.Length == 0)
        {
            消息文本.Text = "名称不能为空。";
            return;
        }

        if (!尝试取址(X框.Text, out var X) || !尝试取址(Y框.Text, out var Y) || !尝试取址(Z框.Text, out var Z))
        {
            消息文本.Text = "X / Y / Z 基址必须是正整数（D 区序号）。";
            return;
        }

        _点项.名称 = 名称值;
        _点项.X基址 = X;
        _点项.Y基址 = Y;
        _点项.Z基址 = Z;

        DialogResult = true;
    }

    private static bool 尝试取址(string? 文本, out int 值) =>
        int.TryParse((文本 ?? string.Empty).Trim().TrimStart('D', 'd', 'M', 'm'),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out 值) && 值 > 0;

    private void 取消(object 发送者, RoutedEventArgs 事件参数) => DialogResult = false;
}
