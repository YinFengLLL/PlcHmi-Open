using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using 上位机.核心.模型;

namespace 上位机.业务.配置;

/// <summary>
/// 历史记录的落盘存储。<b>一天一个文件</b>：<c>data/history-records/yyyy-MM-dd.json</c>。
///
/// <para><b>为什么按天分文件：</b>界面按期翻页，读一天只加载一天；追加只重写当天文件，
/// 不随历史增长变慢；清除历史 = 删文件，不需要"读全部 → 过滤 → 写回"。</para>
///
/// <para><b>写入策略（与报警历史不同，这里刻意不用后台合并写）：</b>
/// 记录的来源只有一个（采集循环），频率是"一个生产节拍一条"，
/// 所以直接在该循环里 await 写盘 —— 失败会抛给调用方，调用方记日志后继续；
/// <b>记录已在内存里，下一条落盘时会一并写入</b>，一次磁盘抖动不会丢掉数据，
/// 也不需要额外的冲刷循环与退出等待。</para>
/// </summary>
public sealed class 历史记录存储
{
    /// <summary>单日记录条数上限。超出后当天不再记录（防止触发位被频闪时写满磁盘）。</summary>
    public const int 单日最多记录条数 = 5000;

    private readonly ILogger<历史记录存储> _日志;
    private readonly 配置健康接口? _健康;
    private readonly Func<DateTime> _时钟;
    private readonly object _同步 = new();
    private readonly List<历史记录条目> _写入列表 = new();

    private DateTime _写入日 = DateTime.MinValue;
    private bool _已提示上限;

    /// <summary>
    /// 最近一次初始化时的保留天数。跨天追加时用它顺手清理超期文件——
    /// 7×24 连续运行的设备若不重启、不改配置，清理再也不会被触发。
    /// </summary>
    private int _保留天数 = 90;

    public 历史记录存储(
        ILogger<历史记录存储> 日志,
        配置健康接口? 健康 = null,
        Func<DateTime>? 时钟提供器 = null)
    {
        _日志 = 日志;
        _健康 = 健康;
        _时钟 = 时钟提供器 ?? (() => DateTime.Now);
    }

    /// <summary>当前正在写入的日期（界面可用它判断"今天"是否已装载）。</summary>
    public DateTime 写入日
    {
        get
        {
            lock (_同步)
            {
                return _写入日;
            }
        }
    }

    /// <summary>
    /// 建立写入日并清理超期文件。
    /// <para><b>必须先装载再追加：</b>否则重启后写入当天第一条记录时，
    /// 会把早上已经记下的那批数据整个覆盖掉——历史数据"丢一半"往往就是这么来的。</para>
    /// </summary>
    public async Task 异步初始化(DateTime 日期, int 保留天数, CancellationToken 取消令牌 = default)
    {
        var 日期值 = 日期.Date;

        _保留天数 = 保留天数;
        await 异步切换写入日(日期值, 取消令牌).ConfigureAwait(false);
        清理过期(日期值, 保留天数);
    }

    /// <summary>读取某一天的记录（按时间升序）。写入日直接取内存，避免刚记的数据读不到。</summary>
    public async Task<IReadOnlyList<历史记录条目>> 异步读取日(
        DateTime 日期, CancellationToken 取消令牌 = default)
    {
        var 日期值 = 日期.Date;

        lock (_同步)
        {
            if (日期值 == _写入日)
            {
                return [.. _写入列表.OrderBy(条目项 => 条目项.时刻)];
            }
        }

        return await 异步读文件(日期值, 取消令牌).ConfigureAwait(false);
    }

    /// <summary>
    /// 追加一条记录并落盘。
    /// <para>写盘失败会向上抛（调用方记日志后继续）：记录已留在内存，下一条写入时一并落盘。</para>
    /// </summary>
    /// <returns>是否已接受该条记录（当天达到上限、或已是重复的空记录时返回 <c>false</c>）。</returns>
    public async Task<bool> 异步追加(历史记录条目 条目, CancellationToken 取消令牌 = default)
    {
        ArgumentNullException.ThrowIfNull(条目);

        if (条目.时刻 == default)
        {
            条目.时刻 = _时钟();
        }

        var 日期值 = 条目.时刻.Date;

        if (日期值 != 写入日)
        {
            await 异步切换写入日(日期值, 取消令牌).ConfigureAwait(false);

            // 跨天顺手清理超期文件（一天一次，代价可忽略）：
            // 原逻辑只在初始化与保存配置时清理，7×24 连续运行时超期文件永远不清。
            清理过期(日期值, _保留天数);
        }

        List<历史记录条目> 快照;

        lock (_同步)
        {
            if (_写入列表.Count >= 单日最多记录条数)
            {
                if (!_已提示上限)
                {
                    _已提示上限 = true;
                    _日志.LogWarning(
                        "历史记录已达单日上限 {Limit} 条，本日不再记录"
                        + "（通常意味着触发位被高频置位，或它本来就是常亮位而非动作完成位）",
                        单日最多记录条数);
                }

                return false;
            }

            _写入列表.Add(条目);
            快照 = [.. _写入列表];
        }

        await 异步写文件(日期值, 快照, 取消令牌).ConfigureAwait(false);
        return true;
    }

    /// <summary>有记录的日期集合（升序）。</summary>
    public IReadOnlyList<DateTime> 有记录的日期集合()
    {
        if (!Directory.Exists(配置路径.历史记录目录))
        {
            return [];
        }

        var 结果 = new List<DateTime>();

        foreach (var 文件 in Directory.EnumerateFiles(配置路径.历史记录目录, "*.json"))
        {
            if (DateTime.TryParseExact(
                    Path.GetFileNameWithoutExtension(文件),
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var 日期值))
            {
                结果.Add(日期值);
            }
        }

        return 结果.Distinct().OrderBy(日期值 => 日期值).ToList();
    }

    /// <summary>清除全部历史记录文件，并清空内存中当天的记录。</summary>
    /// <returns>删除的文件数量。</returns>
    public int 清除全部()
    {
        var 已删 = 0;

        if (Directory.Exists(配置路径.历史记录目录))
        {
            foreach (var 文件 in Directory.EnumerateFiles(配置路径.历史记录目录, "*.json"))
            {
                File.Delete(文件);
                已删++;
            }
        }

        lock (_同步)
        {
            _写入列表.Clear();
            _已提示上限 = false;
        }

        _日志.LogWarning("已清除全部历史记录：{Count} 个文件（{Dir}）", 已删, 配置路径.历史记录目录);
        return 已删;
    }

    /// <summary>删除早于保留期的历史文件（含 <c>.bak</c>）。</summary>
    /// <returns>清理的日期数量。</returns>
    public int 清理过期(DateTime 今天, int 保留天数)
    {
        var 天数 = Math.Clamp(保留天数, 历史记录配置.最小保留天数, 历史记录配置.最大保留天数);
        var 下限 = 今天.Date.AddDays(-(天数 - 1));
        var 已清理 = 0;

        foreach (var 日期值 in 有记录的日期集合())
        {
            if (日期值 >= 下限)
            {
                continue;
            }

            删除文件(日期值);
            已清理++;
        }

        if (已清理 > 0)
        {
            _日志.LogInformation("已清理 {Count} 个超期历史文件（保留 {Days} 天）", 已清理, 天数);
        }

        return 已清理;
    }

    // ── 内部实现 ────────────────────────────────────────────────────────

    /// <summary>切换写入日：把目标日期已有记录装载进内存（只读一次）。</summary>
    private async Task 异步切换写入日(DateTime 日期值, CancellationToken 取消令牌)
    {
        lock (_同步)
        {
            if (日期值 == _写入日)
            {
                return;
            }
        }

        var 已有 = await 异步读文件(日期值, 取消令牌).ConfigureAwait(false);

        lock (_同步)
        {
            if (日期值 == _写入日)
            {
                return;
            }

            _写入日 = 日期值;
            _写入列表.Clear();
            _写入列表.AddRange(已有);
            _已提示上限 = false;
        }

        _日志.LogInformation("历史记录写入日切换为 {Date}（已有 {Count} 条）", 日期值, 已有.Count);
    }

    private async Task<List<历史记录条目>> 异步读文件(DateTime 日期值, CancellationToken 取消令牌)
    {
        var 文件路径 = 配置路径.历史记录文件(日期值);

        var 结果值 = await 配置文件加载器
            .异步加载JSON<历史记录文件>(文件路径, JSON配置.读取, _日志, 取消令牌)
            .ConfigureAwait(false);

        // 损坏 / 已从备份恢复必须让操作员看见：历史记录是追溯依据，不能安静地变空
        _健康?.上报($"历史记录（{日期值:yyyy-MM-dd}）", 结果值);

        if (结果值.值?.条目集合 is null)
        {
            return [];
        }

        if (结果值.加载结果 is 加载结果分类.已加载)
        {
            _日志.LogDebug("已加载 {Date} 的历史记录 {Count} 条", 日期值, 结果值.值.条目集合.Count);
        }

        return 结果值.值.条目集合
            .Where(条目项 => 条目项.数值集合 is not null)
            .OrderBy(条目项 => 条目项.时刻)
            .ToList();
    }

    private async Task 异步写文件(
        DateTime 日期值, List<历史记录条目> 快照, CancellationToken 取消令牌)
    {
        Directory.CreateDirectory(配置路径.历史记录目录);

        var JSON文本 = JsonSerializer.Serialize(
            new 历史记录文件 { 日期 = 日期值, 条目集合 = 快照 }, JSON配置.写入);

        await 原子文件
            .异步写入全部文本(配置路径.历史记录文件(日期值), JSON文本, 取消令牌)
            .ConfigureAwait(false);
    }

    /// <summary>删除某日文件（含原子写留下的 <c>.bak</c>，否则备份会一直躺在目录里）。</summary>
    private static void 删除文件(DateTime 日期值)
    {
        var 文件路径 = 配置路径.历史记录文件(日期值);

        foreach (var 路径 in new[] { 文件路径, 文件路径 + ".bak" })
        {
            if (File.Exists(路径))
            {
                File.Delete(路径);
            }
        }
    }

    /// <summary>与 <c>data/history-records/yyyy-MM-dd.json</c> 的顶层结构对应。</summary>
    private sealed class 历史记录文件
    {
        public DateTime 日期 { get; set; }

        public List<历史记录条目>? 条目集合 { get; set; }
    }
}
