using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.应用.视图模型;

/// <summary>
/// 页面分组过滤：配置里带 <c>分组</c> 的元件只在该分组的页面上出现。
/// <para>留空视为"全局元件"，任何页面都显示——这样公共按钮不必逐页复制。</para>
/// </summary>
internal static class 页面分组
{
    /// <summary>元件分组是否属于某个页面分组。</summary>
    public static bool 匹配(string 元件分组, string 页面分组) =>
        string.IsNullOrWhiteSpace(元件分组)
        || string.IsNullOrWhiteSpace(页面分组)
        || string.Equals(元件分组, 页面分组, StringComparison.OrdinalIgnoreCase);
}

/// <summary>点表里的一个单元格（一点 × 一列）。编辑只改内存，写 PLC 由「坐标示教」或「参数写入」触发。</summary>
public sealed partial class 点值单元格视图模型 : ObservableObject
{
    private readonly 点表服务接口 _点表;
    private readonly 点表定义 _表;
    private readonly 点表列定义 _列;
    private bool _加载中;

    [ObservableProperty] private string _文本 = "--";

    public 点值单元格视图模型(点表定义 表, 点表列定义 列, 点表服务接口 点表)
    {
        _表 = 表;
        _列 = 列;
        _点表 = 点表;
    }

    /// <summary>列键。</summary>
    public string 列键 => _列.键;

    /// <summary>列定义（供界面读取单位等）。</summary>
    public 点表列定义 列 => _列;

    /// <summary>当前行序号。</summary>
    public int 序号 { get; private set; }

    /// <summary>
    /// 是否为指令码列。
    /// <para><b>2026-10-21：</b>指令码改由辅助地址区的下拉框选择（选中即写 PLC），
    /// 本列只负责<b>显示文字</b>（如「直线」），不再手填数字。</para>
    /// </summary>
    public bool 是指令码 => string.Equals(_列.键, 点表定义.指令码列键, StringComparison.OrdinalIgnoreCase);

    /// <summary>用点数据刷新显示。</summary>
    public void 加载(int 序号, 点值行 行)
    {
        _加载中 = true;
        this.序号 = 序号;

        var 空 = 行.是否空(_表);
        var 值 = 行.取(_列.键);
        文本 = 空 && !是指令码 ? "--" : 格式化(值);

        _加载中 = false;
    }

    partial void On文本Changed(string value) => 推送();

    private void 推送()
    {
        // 指令码列显示的是文字（「直线」），解析不成数字，也不该由本列回写：
        // 写入只走下拉框（点表服务.异步写入指令码）
        if (_加载中 || 是指令码)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(文本) || 文本 == "--")
        {
            _点表.设置点值(_表.键, 序号, _列.键, 0);
            return;
        }

        if (坐标辅助.尝试解析(文本, out var 值))
        {
            _点表.设置点值(_表.键, 序号, _列.键, 值);
        }
    }

    private string 格式化(double 值) =>
        是指令码 ? 指令码定义.显示文本(值) : 坐标辅助.格式化(值);
}

/// <summary>点表里的一行（一个点）。</summary>
public sealed partial class 点值行视图模型 : ObservableObject
{
    private readonly 点表定义 _表;
    private readonly Func<int, Task>? _选中回调;

    [ObservableProperty] private bool _是否当前;
    [ObservableProperty] private bool _有值;

    public 点值行视图模型(点表定义 表, int 序号值, 点表服务接口 点表, Func<int, Task>? 选中回调 = null)
    {
        _表 = 表;
        序号 = 序号值;
        _选中回调 = 选中回调;

        foreach (var 列项 in 表.列)
        {
            单元格集合.Add(new 点值单元格视图模型(表, 列项, 点表));
        }
    }

    /// <summary>绝对序号（0 基，固定不变）。</summary>
    public int 序号 { get; }

    /// <summary>界面显示的点号（1 基，两位）。</summary>
    public string 标签 => (序号 + 1).ToString("00");

    /// <summary>单元格集合（与表列一一对应）。</summary>
    public System.Collections.ObjectModel.ObservableCollection<点值单元格视图模型> 单元格集合 { get; } = new();

    /// <summary>用点数据刷新整行。</summary>
    public void 加载(点值行 行)
    {
        foreach (var 单元项 in 单元格集合)
        {
            单元项.加载(序号, 行);
        }

        有值 = !行.是否空(_表);
    }

    /// <summary>点击该行空白处：把这一行设为当前点。</summary>
    [RelayCommand]
    private Task 选中() =>
        是否当前 || _选中回调 is null ? Task.CompletedTask : _选中回调(序号);
}

/// <summary>示教页的一个点动轴（配置驱动：正/反转线圈 + 位置标签）。</summary>
public sealed partial class 示教轴视图模型 : ObservableObject
{
    [ObservableProperty] private bool _是否选中;
    [ObservableProperty] private string _位置文本 = "--.--";

    public 示教轴视图模型(点动按钮项 项)
    {
        轴键 = 项.轴;
        名称 = 项.显示名称;
        位置标签 = 项.位置标签 ?? string.Empty;
    }

    /// <summary>轴标识（配置键：X / Y / Z / ZZ / GlueX / GlueZ）。</summary>
    public string 轴键 { get; }

    /// <summary>界面显示名称。</summary>
    public string 名称 { get; }

    /// <summary>当前位置点位名（可空）。</summary>
    public string 位置标签 { get; }

    /// <summary>回零按钮文案。</summary>
    public string 回零文案 => $"{名称} 回零";
}
