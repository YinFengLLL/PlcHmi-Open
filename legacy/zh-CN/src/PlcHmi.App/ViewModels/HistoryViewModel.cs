using System.Windows.Threading;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using 上位机.应用.服务;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.应用.视图模型;

/// <summary>历史记录单元格：显示文本 + 该列颜色键（做成小对象，避免 XAML 逐格反查配置）。</summary>
public sealed class 历史记录单元格视图模型
{
    public 历史记录单元格视图模型(string 文本值, string 颜色键值)
    {
        文本 = 文本值;
        颜色键 = 颜色键值;
    }

    /// <summary>单元格文本（已应用小数位或 ASCII 转换）。</summary>
    public string 文本 { get; }

    /// <summary>文字画刷资源键（主题语义色；"无"为正文色）。</summary>
    public string 颜色键 { get; }
}

/// <summary>历史记录表的一行：列配置驱动，显示值装载时算一次（避免滚动时逐格重算）。</summary>
public sealed class 历史记录行视图模型
{
    public 历史记录行视图模型(
        历史记录条目 条目, IReadOnlyList<历史数据项> 数据项集合, int 序号, 字顺序 字序)
    {
        ArgumentNullException.ThrowIfNull(条目);
        ArgumentNullException.ThrowIfNull(数据项集合);

        模型 = 条目;
        序号文本 = 序号.ToString(CultureInfo.InvariantCulture);

        var 显示值 = new List<历史记录单元格视图模型>(数据项集合.Count);

        for (var 列序号 = 0; 列序号 < 数据项集合.Count; 列序号++)
        {
            var 数据项 = 数据项集合[列序号];

            // 记录条数少于当前配置（列被增删过）时留占位符，不补 0——
            // 补 0 会让"没采到"看起来像"真的是 0"
            显示值.Add(列序号 < 条目.数值集合.Count
                ? new 历史记录单元格视图模型(数据项.显示文本(条目.数值集合[列序号], 字序), 数据项.颜色键)
                : new 历史记录单元格视图模型("--", "TextDisabledBrush"));
        }

        显示值集合 = 显示值;
    }

    /// <summary>原始记录。</summary>
    public 历史记录条目 模型 { get; }

    /// <summary>该日第几条（1 起，按时间顺序）。</summary>
    public string 序号文本 { get; }

    /// <summary>工单号：写下记录时的上次扫码结果（不落盘，重载后为空列）。</summary>
    public string 工单号文本 => 模型.工单号;

    /// <summary>触发时刻（只显示时间，日期由页面标题承载）。</summary>
    public string 时间文本 => 模型.时刻.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>与当前列一一对应的显示值（含该列颜色）。</summary>
    public IReadOnlyList<历史记录单元格视图模型> 显示值集合 { get; }
}

/// <summary>历史记录页 ViewModel。</summary>
public sealed partial class 历史记录视图模型 : 视图模型基类
{
    private static readonly string[] 星期名称 = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];

    /// <summary>单列宽度（与 XAML 中的列宽保持一致，用于计算横向滚动的最小宽度）。</summary>
    private const double 列宽 = 118;

    /// <summary>工单号列宽（与 XAML 保持一致）。</summary>
    private const double 工单号列宽 = 140;

    /// <summary>固定列（工单号 + 序号 + 时间）占用的宽度。</summary>
    private const double 固定列宽 = 工单号列宽 + 56 + 96;

    private readonly 历史记录服务接口 _历史;
    private readonly 用户服务接口 _用户项;
    private readonly 对话框服务接口 _对话框;
    private readonly 文件对话框服务接口 _文件对话框;
    private readonly ILogger<历史记录视图模型> _日志;

    private readonly Queue<历史记录条目> _待追加 = new();  // 200ms 合批，避免逐条 Insert
    private readonly DispatcherTimer _追加合批 = new() { Interval = TimeSpan.FromMilliseconds(200) };

    [ObservableProperty] private DateTime _当前日期 = DateTime.Today;
    [ObservableProperty] private string _日期文本 = string.Empty;
    [ObservableProperty] private string _数据体名称 = string.Empty;
    [ObservableProperty] private string _采集状态文本 = string.Empty;
    [ObservableProperty] private string _采集状态颜色键 = "MutedBrush";
    [ObservableProperty] private double _表格宽度 = 720;
    [ObservableProperty] private int _记录条数;
    [ObservableProperty] private bool _可编辑历史;

    public 历史记录视图模型(
        历史记录服务接口 历史记录,
        用户服务接口 用户,
        对话框服务接口 对话框集合,
        文件对话框服务接口 文件对话框,
        ILogger<历史记录视图模型> 日志)
    {
        _历史 = 历史记录;
        _用户项 = 用户;
        _对话框 = 对话框集合;
        _文件对话框 = 文件对话框;
        _日志 = 日志;

        标题 = "历史记录";
        副标题 = "按触发位采集的数据体，可按天翻页查看、导出 CSV";

        _追加合批.Tick += (_, _) => { _追加合批.Stop(); 冲刷待追加(); };

        _历史.配置已变更 += (_, _) => 在界面线程执行(() =>
        {
            重建列();
            // 列变了，已显示的行必须按新列重建（否则表头与数据错位）
            异步安全调用.即发即忘(() => 异步加载日(当前日期), _日志, "刷新历史记录");
        });

        _历史.记录已新增 += (_, 条目) => 在界面线程执行(() => 追加行(条目));

        _用户项.用户已变更 += (_, _) => 在界面线程执行(刷新权限);

        重建列();
        刷新权限();
    }

    /// <summary>表格列（配置里的数据项，按显示顺序）。</summary>
    public 批量可观察集合<历史数据项> 列集合 { get; } = new();

    /// <summary>当前日期的记录（最新的在最上面，新记录不必滚动就能看见）。</summary>
    /// <remarks>
    /// 用 <see cref="批量可观察集合{T}"/>：翻页装载是"整表替换"，
    /// 逐条 Add 会对每一行各发一次通知，几千行就是几千次布局。
    /// </remarks>
    public 批量可观察集合<历史记录行视图模型> 记录集合 { get; } = new();

    /// <summary>当前日期的记录条数文本。</summary>
    public string 记录条数文本 => $"共 {记录条数} 条";

    /// <inheritdoc />
    public override Task 异步初始化()
    {
        重建列();
        刷新权限();
        return 异步加载日(当前日期);
    }

    partial void On记录条数Changed(int value) => OnPropertyChanged(nameof(记录条数文本));

    // ── 翻页 ────────────────────────────────────────────────────────────

    /// <summary>前一天（跳到最近有记录的前一天，停机几天后翻页不必连点十几下）。</summary>
    [RelayCommand]
    private Task 异步前一天() => 异步翻页(-1);

    /// <summary>后一天（同上，跳到最近有记录的后一天）。</summary>
    [RelayCommand]
    private Task 异步后一天() => 异步翻页(1);

    /// <summary>回到今天。</summary>
    [RelayCommand]
    private Task 异步回到今天() => 异步加载日(DateTime.Today);

    private async Task 异步翻页(int 方向)
    {
        var 日期列表 = _历史.有记录的日期集合();

        // 【2026-11-12 修复】这里原来写的是 Where(...).MaxBy/MinBy，
        // 而 MaxBy/MinBy 在**空序列 + 不可空值类型**（DateTime）上不返回 null，
        // 而是抛 InvalidOperationException("Sequence contains no elements")。
        // 于是"前一天没有数据"时友好提示永远走不到，异常直接冒到全局处理器，
        // 顶栏挂红条「程序异常（已记录日志，可继续操作）：InvalidOperationException…」。
        // 现在先筛成列表再判空，空列表就是"没有更多记录"。
        var 候选列表 = 日期列表
            .Where(日期值 => 方向 < 0 ? 日期值 < 当前日期 : 日期值 > 当前日期)
            .ToList();

        if (候选列表.Count == 0)
        {
            _对话框.提示("没有更多记录",
                方向 < 0
                    ? $"没有早于 {当前日期:yyyy-MM-dd} 的历史记录。"
                    : $"没有晚于 {当前日期:yyyy-MM-dd} 的历史记录。");
            return;
        }

        var 候选 = 方向 < 0 ? 候选列表.Max() : 候选列表.Min();

        await 异步加载日(候选).ConfigureAwait(true);
    }

    // ── 导出 / 清除 / 配置 ──────────────────────────────────────────────

    [RelayCommand]
    private async Task 异步导出CSV()
    {
        var 默认名称 = $"历史记录-{当前日期:yyyy-MM-dd}.csv";

        var 文件路径 = _文件对话框.选择保存文件("导出历史记录", 文件对话框服务.CSV筛选, 默认名称);

        if (文件路径 is null)
        {
            return;
        }

        var 成功 = await 界面命令
            .异步执行(_日志, _对话框, "导出", () => _历史.异步导出CSV到文件(文件路径, 当前日期), 默认名称)
            .ConfigureAwait(true);

        if (成功)
        {
            _对话框.提示("导出完成", $"{当前日期:yyyy-MM-dd} 的 {记录条数} 条记录已导出：\n{文件路径}");
        }
    }

    [RelayCommand]
    private async Task 异步清除历史()
    {
        if (!可编辑历史)
        {
            _对话框.提示("权限不足", "只有管理员可以清除历史记录。");
            return;
        }

        var 天数 = _历史.有记录的日期集合().Count;

        if (!_对话框.确认("清除历史数据",
                $"确认清除全部历史记录？共 {天数} 天的数据。",
                视图.确认等级.危险, "清除",
                "历史记录将永久删除，不可撤销；追溯设备动作与工艺参数时将无据可查。"))
        {
            return;
        }

        var 成功 = await 界面命令
            .异步执行(_日志, _对话框, "清除", () => _历史.异步清除全部(), "全部历史记录")
            .ConfigureAwait(true);

        if (!成功)
        {
            return;
        }

        await 异步加载日(DateTime.Today).ConfigureAwait(true);
        _对话框.提示("已清除", "历史记录已全部清除。");
    }

    [RelayCommand]
    private async Task 异步编辑配置()
    {
        if (!可编辑历史)
        {
            _对话框.提示("权限不足", "只有管理员可以修改历史记录配置。");
            return;
        }

        // 编辑的是克隆副本：取消时运行中的配置必须原封不动
        var 副本 = _历史.配置.克隆();

        if (!_对话框.编辑历史记录配置(副本))
        {
            return;
        }

        var 校验项 = _历史.校验(副本);

        if (!校验项.是否有效)
        {
            _对话框.错误信息("校验失败", 校验项.摘要);
            return;
        }

        var 成功 = await 界面命令
            .异步执行(_日志, _对话框, "保存配置", () => _历史.异步保存配置(副本), "历史记录配置")
            .ConfigureAwait(true);

        if (!成功)
        {
            return;
        }

        重建列();
        await 异步加载日(当前日期).ConfigureAwait(true);

        _对话框.提示("已保存",
            $"历史记录配置已保存：触发位 {副本.触发地址}，{副本.数据项集合.Count} 个数据项。");
    }

    // ── 数据加载与刷新 ──────────────────────────────────────────────────

    private Task 异步加载日(DateTime 日期) =>
        界面命令.异步执行(_日志, _对话框, "加载历史记录", async () =>
        {
            当前日期 = 日期.Date;

            var 条目列表 = await _历史.异步读取日(当前日期).ConfigureAwait(true);

            _待追加.Clear();
            _追加合批.Stop();
            重建行(条目列表);
        }, 日期.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    private void 重建列()
    {
        var 配置 = _历史.配置;

        列集合.替换全部(配置.数据项集合);

        // 表格横向最小宽度：列多时出现横向滚动条，而不是把每列挤成看不清
        表格宽度 = Math.Max(720, 固定列宽 + 列集合.Count * 列宽);

        数据体名称 = 配置.标题;
        副标题 = $"{配置.标题} · 按天翻页查看，可导出 CSV";

        刷新采集状态();
        刷新日期文本();
    }

    /// <summary>按当前列重建全部行（最新的在最上面）。</summary>
    private void 重建行(IReadOnlyList<历史记录条目> 条目列表)
    {
        var 配置 = _历史.配置;
        var 行列表 = new List<历史记录行视图模型>(条目列表.Count);

        for (var 序号 = 条目列表.Count - 1; 序号 >= 0; 序号--)
        {
            行列表.Add(new 历史记录行视图模型(
                条目列表[序号], 配置.数据项集合, 序号 + 1, 配置.字顺序));
        }

        // 整表替换只发一次 Reset 通知（逐条 Add 会触发 N 次布局）
        记录集合.替换全部(行列表);

        记录条数 = 条目列表.Count;
        刷新日期文本();
    }

    /// <summary>采集线程记下一条：只入队，200ms 内多条合并成一次整表替换，最新的仍排在最上面。</summary>
    private void 追加行(历史记录条目 条目)
    {
        if (条目.时刻.Date != 当前日期) return;   // 翻看别的日期：不打扰当前视图
        _待追加.Enqueue(条目);
        _追加合批.Stop();
        _追加合批.Start();
    }

    private void 冲刷待追加()
    {
        var 配置 = _历史.配置;
        var 全部 = 记录集合.ToList();   // 替换全部() 会清空自身，先快照旧行；Insert(0) 保持最新在最上
        while (_待追加.Count > 0)
        {
            记录条数++;
            全部.Insert(0, new 历史记录行视图模型(
                _待追加.Dequeue(), 配置.数据项集合, 记录条数, 配置.字顺序));
        }
        记录集合.替换全部(全部);
    }

    private void 刷新日期文本()
    {
        var 今天 = DateTime.Today;
        var 后缀 = 当前日期 == 今天
            ? "（今天）"
            : 当前日期 == 今天.AddDays(-1) ? "（昨天）" : string.Empty;

        日期文本 = $"{当前日期:yyyy-MM-dd} {星期名称[(int)当前日期.DayOfWeek]}{后缀}";
    }

    private void 刷新采集状态()
    {
        var 配置 = _历史.配置;

        var 握手说明 = 配置.读取后复位触发位 ? "读取后自动复位触发位" : "触发位由 PLC 复位";

        采集状态文本 = 配置.启用
            ? $"采集中：触发位 {配置.触发地址}（上升沿记一条 · {握手说明}）· {配置.数据项集合.Count} 个数据项"
            : $"已停用采集：触发位 {配置.触发地址} · {配置.数据项集合.Count} 个数据项";

        采集状态颜色键 = 配置.启用 ? "SuccessBrush" : "MutedBrush";
    }

    private void 刷新权限() => 可编辑历史 = _用户项.有(权限.编辑历史记录);
}
