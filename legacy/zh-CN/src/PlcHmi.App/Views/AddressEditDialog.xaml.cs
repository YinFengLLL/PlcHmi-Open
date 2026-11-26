using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using 上位机.核心.模型;

namespace 上位机.应用.视图;

/// <summary>
/// 页面地址编辑器（仅管理员入口）。
///
/// <para>把页面上所有与 PLC 交互的元件地址集中成一张表：点表列基址、辅助地址、
/// 动作按钮、点动与回零线圈。辅助地址可由管理员新增 / 删除，其余元件地址可改不可删。</para>
///
/// <para>只负责收集输入；权限守卫与地址格式校验在服务层。</para>
/// </summary>
public partial class 地址编辑对话框 : Window
{
    /// <summary>可选数据类型（辅助地址用）。</summary>
    private static readonly string[] 全类型选项 =
        ["Float32", "UInt16", "Int16", "Int32", "UInt32", "布尔"];

    /// <summary>一行的可编辑状态。</summary>
    public sealed partial class 地址行 : ObservableObject
    {
        private readonly 地址编辑项 _项;

        [ObservableProperty] private string _名称 = string.Empty;
        [ObservableProperty] private string _地址文本 = string.Empty;
        [ObservableProperty] private string _数据类型文本 = "Float32";
        [ObservableProperty] private bool _结构体成员;

        public 地址行(地址编辑项 项)
        {
            _项 = 项;
            名称 = 项.名称;
            地址文本 = 项.地址文本;
            数据类型文本 = string.IsNullOrWhiteSpace(项.数据类型文本) ? "Float32" : 项.数据类型文本;
            结构体成员 = 项.结构体成员;
        }

        /// <summary>类别（分组表头）。</summary>
        public string 类别 => _项.类别;

        /// <summary>底层条目（回写用）。</summary>
        public 地址编辑项 项 => _项;

        /// <summary>是否允许删除。</summary>
        public bool 可删除 => _项.可删除;

        /// <summary>是否允许修改数据类型（只有辅助地址是任意 D 区数值；动作按钮是 M 区线圈）。</summary>
        public bool 可改类型 => string.Equals(_项.类别, "辅助地址", StringComparison.Ordinal);

        /// <summary>是否显示"结构体成员"勾选框（仅点胶页的辅助地址）。</summary>
        public bool 可设结构体成员 => _项.可设结构体成员;

        /// <summary>成员地址由结构体布局算出，编辑器里不能再手改。</summary>
        public bool 地址可编辑 => !结构体成员;

        partial void On结构体成员Changed(bool value) => OnPropertyChanged(nameof(地址可编辑));

        /// <summary>数据类型下拉项。</summary>
        public IReadOnlyList<string> 类型选项 => 全类型选项;
    }

    private readonly 地址编辑请求 _请求;
    private readonly List<地址行> _已删除 = new();

    private 地址编辑对话框(地址编辑请求 请求)
    {
        InitializeComponent();

        _请求 = 请求;
        标题控件.Text = $"地址编辑 · {请求.标题}";

        foreach (var 项 in 请求.项集合)
        {
            行集合.Add(new 地址行(项));
        }

        DataContext = this;
    }

    /// <summary>可编辑行集合（供分组视图绑定）。</summary>
    public ObservableCollection<地址行> 行集合 { get; } = new();

    /// <summary>弹出编辑器。返回是否保存（保存时已就地写回请求对象）。</summary>
    public static bool 显示(地址编辑请求 请求)
    {
        var 对话框 = new 地址编辑对话框(请求)
        {
            Owner = Application.Current.MainWindow,
        };

        return 对话框.ShowDialog() == true;
    }

    /// <summary>仅供自检使用：实例化但不显示。</summary>
    internal static Window 创建验证() => new 地址编辑对话框(new 地址编辑请求
    {
        标题 = "自检",
        默认分组 = "自检",
        项集合 =
        [
            new 地址编辑项 { 类别 = "点表列", 键 = "X", 名称 = "X坐标基址", 地址文本 = "D2500" },
        ],
    });

    private void 新增辅助地址(object 发送者, RoutedEventArgs 事件参数)
    {
        // 上限按「本页分组」统计：分组匹配口径与服务层一致（空分组视为全局元件）。
        var 本页分组 = _请求.默认分组;
        var 本页数量 = 行集合.Count(行 =>
            行.类别 == "辅助地址" && 分组匹配(行.项.分组, 本页分组));

        if (本页数量 >= 辅助地址项.每组上限)
        {
            消息文本.Text = $"「{本页分组}」分组的辅助地址已达 {辅助地址项.每组上限} 个上限，不能再新增。";
            return;
        }

        var 序号 = 下一个未占用键("辅助地址");

        行集合.Add(new 地址行(new 地址编辑项
        {
            类别 = "辅助地址",
            键 = $"辅助地址{序号}",
            名称 = $"新辅助地址{序号}",
            地址文本 = "D0",
            数据类型文本 = "Float32",
            分组 = _请求.默认分组,
            可删除 = true,
            新增 = true,

            // 新条目跟随本页口径：点胶页的辅助地址才有"结构体成员"这个概念
            可设结构体成员 = 行集合.Any(行 => 行.可设结构体成员),
        }));
    }

    private void 新增动作按钮(object 发送者, RoutedEventArgs 事件参数)
    {
        var 序号 = 下一个未占用键("动作");

        行集合.Add(new 地址行(new 地址编辑项
        {
            类别 = "动作按钮",
            键 = $"动作{序号}",
            名称 = $"新动作按钮{序号}",

            // 新增按钮默认按"脉冲 M 区线圈"处理
            地址文本 = "M0",
            数据类型文本 = "布尔",
            分组 = _请求.默认分组,
            可删除 = true,
            新增 = true,
        }));
    }

    /// <summary>
    /// 取下一个未占用的编号（从 1 起递增，返回第一个空位）。
    /// <para>旧逻辑用「行数 + 1」生成键，遇到配置里已有的<b>不连续编号键</b>
    /// （如 辅助地址7/8/9/11）会撞键；服务层一旦全局按键就地更新，撞键即覆盖旧项。
    /// 这里改为扫描全部已知键取第一个空位。</para>
    /// </summary>
    private int 下一个未占用键(string 前缀)
    {
        // 已知占用键 = 当前表格里的行 + 请求原始项 + 本次会话已删除的行。
        // 已删除项在保存前仍留在服务层配置里，不能复用它的键，否则又撞上残留项。
        var 占用键 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var 行 in 行集合)
        {
            占用键.Add(行.项.键);
        }

        foreach (var 项 in _请求.项集合)
        {
            占用键.Add(项.键);
        }

        foreach (var 行 in _已删除)
        {
            占用键.Add(行.项.键);
        }

        // 占用键最多 Count 个，检查前 Count+1 个编号必有空位（鸽巢原理），循环必定终止。
        for (var 序号 = 1; 序号 <= 占用键.Count + 1; 序号++)
        {
            if (!占用键.Contains($"{前缀}{序号}"))
            {
                return 序号;
            }
        }

        throw new InvalidOperationException($"前缀「{前缀}」的键已用尽。");
    }

    /// <summary>分组匹配（空分组视为全局元件，与页面地址服务同口径）。</summary>
    private static bool 分组匹配(string 元件分组, string 页面分组) =>
        string.IsNullOrWhiteSpace(元件分组)
        || string.IsNullOrWhiteSpace(页面分组)
        || string.Equals(元件分组, 页面分组, StringComparison.OrdinalIgnoreCase);

    private void 删除行(object 发送者, RoutedEventArgs 事件参数)
    {
        if (发送者 is not FrameworkElement 元素 || 元素.DataContext is not 地址行 行)
        {
            return;
        }

        行集合.Remove(行);

        if (!行.项.新增)
        {
            _已删除.Add(行);
        }
    }

    private void 确认(object 发送者, RoutedEventArgs 事件参数)
    {
        foreach (var 行 in 行集合)
        {
            if (string.IsNullOrWhiteSpace(行.地址文本))
            {
                消息文本.Text = $"「{行.名称}」的地址不能为空。";
                return;
            }

            行.项.名称 = 行.名称.Trim();
            行.项.地址文本 = 行.地址文本.Trim();
            行.项.数据类型文本 = 行.数据类型文本;
            行.项.结构体成员 = 行.结构体成员;
        }

        _请求.项集合.Clear();
        _请求.项集合.AddRange(行集合.Select(行 => 行.项));

        _请求.已删除项.Clear();
        _请求.已删除项.AddRange(_已删除.Select(行 => 行.项));

        DialogResult = true;
    }

    private void 取消(object 发送者, RoutedEventArgs 事件参数) => DialogResult = false;
}
