using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.应用.视图;

/// <summary>
/// 数据体「数据项」的编辑行（表格里的一列）。
/// <para>类型与小数位用下拉而不是输入框：都是小范围枚举值，
/// 手打必然出现"INT "、"２"（全角）这类看不见的输入错误，
/// 而地址错一位、类型错一项都会让历史数据整体失真。</para>
/// </summary>
public sealed partial class 历史数据项编辑行 : ObservableObject
{
    // 2026-10-24：新增 REAL（32 位浮点）——PLC 直接给工程量时不必再放大成整数
    private static readonly string[] 类型选项 = ["INT（16 位）", "DINT（32 位）", "REAL（32 位浮点）"];

    private static readonly string[] 位数组选项 = ["0", "1", "2", "3", "4"];

    private static readonly string[] 颜色选项 = ["无", "红", "黄", "绿"];

    [ObservableProperty] private string _名称 = string.Empty;

    [ObservableProperty] private string _地址 = string.Empty;

    /// <summary>0 = INT，1 = DINT，2 = REAL（32 位浮点）。</summary>
    [ObservableProperty] private int _类型索引;

    /// <summary>
    /// 小数位（0–4）。
    /// <para>INT / DINT：显示值 = PLC 原始值 ÷ 10^小数位（<c>600</c> → <c>6.00</c>）。</para>
    /// <para>REAL：只表示显示保留几位（<c>12.3456</c> → <c>12.35</c>），不缩放。</para>
    /// </summary>
    [ObservableProperty] private int _小数位索引;

    [ObservableProperty] private string _单元 = string.Empty;

    /// <summary>是否按 ASCII 字符显示（仅 DINT 可勾选）。</summary>
    [ObservableProperty] private bool _显示为Ascii;

    /// <summary>0 = 无，1 = 红，2 = 黄，3 = 绿。</summary>
    [ObservableProperty] private int _颜色索引;

    /// <summary>类型下拉数据源。</summary>
    public string[] 数据类型选项 => 类型选项;

    /// <summary>小数位下拉数据源。</summary>
    public string[] 小数位选项 => 位数组选项;

    /// <summary>颜色下拉数据源。</summary>
    public string[] 颜色选项表 => 颜色选项;

    /// <summary>ASCII 显示是否可勾选（仅 DINT：32 位 = 4 字节 = 4 个字符）。</summary>
    public bool 可设Ascii => 类型索引 == 1;

    /// <summary>小数位是否可改（ASCII 显示时字符没有小数点，改了解释不清）。</summary>
    public bool 小数位可用 => !显示为Ascii;

    partial void On类型索引Changed(int value)
    {
        OnPropertyChanged(nameof(可设Ascii));

        if (value != 1 && 显示为Ascii)
        {
            // 切回 INT 时必须清掉 ASCII：否则"能存但显示不对"，比存不进去难查得多
            显示为Ascii = false;
        }
    }

    partial void On显示为AsciiChanged(bool value) => OnPropertyChanged(nameof(小数位可用));

    /// <summary>由配置项生成编辑行。</summary>
    public static 历史数据项编辑行 从模型(历史数据项 项) => new()
    {
        名称 = 项.名称,
        地址 = 项.地址,
        类型索引 = 项.数据类型 switch
        {
            标签数据类型.Int32 => 1,
            标签数据类型.Float32 => 2,
            _ => 0,
        },
        小数位索引 = Math.Clamp(项.小数位, 0, 历史记录配置.最大小数位),
        单元 = 项.单元 ?? string.Empty,
        显示为Ascii = 项.显示为Ascii && 项.可设Ascii,
        颜色索引 = (int)项.颜色,
    };

    /// <summary>转回配置模型。</summary>
    public 历史数据项 转模型() => new()
    {
        名称 = 名称.Trim(),
        地址 = 地址.Trim(),
        数据类型 = 类型索引 switch
        {
            1 => 标签数据类型.Int32,
            2 => 标签数据类型.Float32,
            _ => 标签数据类型.Int16,
        },
        小数位 = Math.Clamp(小数位索引, 0, 历史记录配置.最大小数位),
        单元 = string.IsNullOrWhiteSpace(单元) ? null : 单元.Trim(),
        // 双保险：界面已禁用非 DINT 的勾选，这里再挡一次（手改文件走的是同一条校验）
        显示为Ascii = 显示为Ascii && 类型索引 == 1,
        颜色 = (历史列颜色)Math.Clamp(颜色索引, 0, 3),
    };
}

/// <summary>
/// 历史记录配置编辑器：改触发位、数据体名称、保留天数，并增删 / 修改数据项。
///
/// <para>只负责收集界面输入；<b>地址区域、类型与重叠校验在服务层</b>
/// （同一套规则必须同时作用于"界面改的"和"手改文件的"配置）。</para>
/// </summary>
public partial class 历史配置编辑器对话框 : Window
{
    private readonly IReadOnlyList<变量表条目>? _变量列表;
    private readonly ObservableCollection<历史数据项编辑行> _行集合 = new();

    private 历史配置编辑器对话框(历史记录配置 配置项, IReadOnlyList<变量表条目>? 变量列表)
    {
        InitializeComponent();

        _变量列表 = 变量列表;

        名称框.Text = 配置项.标题;
        触发位框.Text = 配置项.触发地址;
        保留天数框.Text = 配置项.保留天数.ToString(CultureInfo.InvariantCulture);
        启用框.IsChecked = 配置项.启用;
        复位框.IsChecked = 配置项.读取后复位触发位;

        foreach (var 项 in 配置项.数据项集合)
        {
            _行集合.Add(历史数据项编辑行.从模型(项));
        }

        数据项列表.ItemsSource = _行集合;
        刷新数据项摘要();

        Loaded += (_, _) => 名称框.Focus();
    }

    /// <summary>弹出编辑器。确定时已就地修改 <paramref name="配置项"/>（含数据项集合）。</summary>
    public static bool 显示(历史记录配置 配置项, IReadOnlyList<变量表条目>? 变量列表 = null)
    {
        var 对话框 = new 历史配置编辑器对话框(配置项, 变量列表)
        {
            Owner = Application.Current.MainWindow,
        };

        if (对话框.ShowDialog() != true)
        {
            return false;
        }

        配置项.标题 = 对话框.名称框.Text.Trim();
        配置项.触发地址 = 对话框.触发位框.Text.Trim();
        配置项.启用 = 对话框.启用框.IsChecked ?? false;
        配置项.读取后复位触发位 = 对话框.复位框.IsChecked ?? false;
        配置项.保留天数 = int.TryParse(
            对话框.保留天数框.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var 天数)
            ? 天数
            : 配置项.保留天数;
        配置项.数据项集合 = 对话框._行集合.Select(行 => 行.转模型()).ToList();

        return true;
    }

    /// <summary>仅供自检使用：实例化但不显示（用于验证 XAML 资源能否解析）。</summary>
    internal static Window 创建验证(历史记录配置 配置项) => new 历史配置编辑器对话框(配置项, null);

    // ── 数据项增删 ──────────────────────────────────────────────────────

    private void 新增数据项(object 发送者, RoutedEventArgs 事件参数)
    {
        if (_行集合.Count >= 历史记录配置.最大数据项数量)
        {
            消息文本.Text = $"数据项最多 {历史记录配置.最大数据项数量} 个，请先删除不用的列。";
            return;
        }

        _行集合.Add(new 历史数据项编辑行 { 类型索引 = 0, 小数位索引 = 0 });
        刷新数据项摘要();
        消息文本.Text = string.Empty;
    }

    private void 删除数据项(object 发送者, RoutedEventArgs 事件参数)
    {
        if (发送者 is not FrameworkElement { DataContext: 历史数据项编辑行 行 })
        {
            return;
        }

        _行集合.Remove(行);
        刷新数据项摘要();
    }

    // ── 变量绑定 ────────────────────────────────────────────────────────

    private void 触发位选变量(object 发送者, RoutedEventArgs 事件参数)
    {
        if (变量选择对话框.显示(_变量列表 ?? [], "为触发位选择变量") is not { } 条目)
        {
            return;
        }

        触发位框.Text = 条目.软元件地址;
    }

    private void 数据项选变量(object 发送者, RoutedEventArgs 事件参数)
    {
        if (发送者 is not FrameworkElement { DataContext: 历史数据项编辑行 行 })
        {
            return;
        }

        var 标题文本 = string.IsNullOrWhiteSpace(行.名称) ? "为数据项选择变量" : $"为「{行.名称}」选择变量";

        if (变量选择对话框.显示(_变量列表 ?? [], 标题文本) is not { } 条目)
        {
            return;
        }

        行.地址 = 条目.软元件地址;

        // 变量自带类型时顺带对齐（INT / DINT / REAL 三种都能直接映射）
        行.类型索引 = 条目.数据类型 switch
        {
            标签数据类型.Int32 => 1,
            标签数据类型.Float32 => 2,
            标签数据类型.Int16 => 0,
            _ => 行.类型索引,   // 其它类型（如 Bool / Float64）保持管理员选择，由服务层校验拦截
        };
    }

    // ── 确定 / 取消 ────────────────────────────────────────────────────

    private void 确认(object 发送者, RoutedEventArgs 事件参数)
    {
        if (校验() is { } 错误)
        {
            消息文本.Text = 错误;
            return;
        }

        DialogResult = true;
    }

    private void 取消(object 发送者, RoutedEventArgs 事件参数) => DialogResult = false;

    private void 刷新数据项摘要() =>
        数据项摘要文本.Text =
            $"共 {_行集合.Count} 项（最多 {历史记录配置.最大数据项数量} 项）· " +
            "INT/DINT 的小数位用于缩放显示：填 2 时 PLC 的 600 显示为 6.00；" +
            "REAL（32 位浮点）直接按工程量显示，小数位只决定保留几位；" +
            "DINT 项可勾选 ASCII 按字符显示（4 字节 = 4 个字符）；每列可选红 / 黄 / 绿着色";

    /// <summary>界面侧的基本必填检查；返回第一条错误，全部通过返回 <c>null</c>。</summary>
    private string? 校验()
    {
        if (string.IsNullOrWhiteSpace(名称框.Text))
        {
            return "请填写数据体名称。";
        }

        if (string.IsNullOrWhiteSpace(触发位框.Text))
        {
            return "请填写触发位地址（M 区，如 M700）。";
        }

        if (_行集合.Count == 0)
        {
            return "至少需要一个数据项。";
        }

        for (var 序号 = 0; 序号 < _行集合.Count; 序号++)
        {
            var 行 = _行集合[序号];

            if (string.IsNullOrWhiteSpace(行.名称))
            {
                return $"第 {序号 + 1} 项：请填写名称。";
            }

            if (string.IsNullOrWhiteSpace(行.地址))
            {
                return $"第 {序号 + 1} 项「{行.名称}」：请填写读取地址（字元件，如 D2109）。";
            }
        }

        if (!int.TryParse(保留天数框.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var 天数) ||
            天数 is < 历史记录配置.最小保留天数 or > 历史记录配置.最大保留天数)
        {
            return $"保留天数请填 {历史记录配置.最小保留天数}–{历史记录配置.最大保留天数} 之间的整数。";
        }

        return null;
    }
}
