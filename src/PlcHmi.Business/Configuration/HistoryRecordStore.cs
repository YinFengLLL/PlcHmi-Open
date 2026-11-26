using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Configuration;

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
public sealed class HistoryRecordStore
{
    /// <summary>单日记录条数上限。超出后当天不再记录（防止触发位被频闪时写满磁盘）。</summary>
    public const int singleDayMaxRecordCount = 5000;

    private readonly ILogger<HistoryRecordStore> _log;
    private readonly IConfigHealth? _health;
    private readonly Func<DateTime> _clock;
    private readonly object _sync = new();
    private readonly List<HistoryRecordEntry> _writeList = new();

    private DateTime _writeDay = DateTime.MinValue;
    private bool _hintedUpperLimit;

    /// <summary>
    /// 最近一次初始化时的保留天数。跨天追加时用它顺手清理超期文件——
    /// 7×24 连续运行的设备若不重启、不改配置，清理再也不会被触发。
    /// </summary>
    private int _retainDays = 90;

    public HistoryRecordStore(
        ILogger<HistoryRecordStore> Log,
        IConfigHealth? Health = null,
        Func<DateTime>? clockProvider = null)
    {
        _log = Log;
        _health = Health;
        _clock = clockProvider ?? (() => DateTime.Now);
    }

    /// <summary>当前正在写入的日期（界面可用它判断"今天"是否已装载）。</summary>
    public DateTime WriteDay
    {
        get
        {
            lock (_sync)
            {
                return _writeDay;
            }
        }
    }

    /// <summary>
    /// 建立写入日并清理超期文件。
    /// <para><b>必须先装载再追加：</b>否则重启后写入当天第一条记录时，
    /// 会把早上已经记下的那批数据整个覆盖掉——历史数据"丢一半"往往就是这么来的。</para>
    /// </summary>
    public async Task InitializeAsync(DateTime Date, int RetainDays, CancellationToken cancellationToken = default)
    {
        var dateValue = Date.Date;

        _retainDays = RetainDays;
        await SwitchWriteDayAsync(dateValue, cancellationToken).ConfigureAwait(false);
        CleanBeyondPeriod(dateValue, RetainDays);
    }

    /// <summary>读取某一天的记录（按时间升序）。写入日直接取内存，避免刚记的数据读不到。</summary>
    public async Task<IReadOnlyList<HistoryRecordEntry>> ReadDayAsync(
        DateTime Date, CancellationToken cancellationToken = default)
    {
        var dateValue = Date.Date;

        lock (_sync)
        {
            if (dateValue == _writeDay)
            {
                return [.. _writeList.OrderBy(EntryItem => EntryItem.Timestamp)];
            }
        }

        return await ReadFileAsync(dateValue, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 追加一条记录并落盘。
    /// <para>写盘失败会向上抛（调用方记日志后继续）：记录已留在内存，下一条写入时一并落盘。</para>
    /// </summary>
    /// <returns>是否已接受该条记录（当天达到上限、或已是重复的空记录时返回 <c>false</c>）。</returns>
    public async Task<bool> AppendAsync(HistoryRecordEntry Entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(Entry);

        if (Entry.Timestamp == default)
        {
            Entry.Timestamp = _clock();
        }

        var dateValue = Entry.Timestamp.Date;

        if (dateValue != WriteDay)
        {
            await SwitchWriteDayAsync(dateValue, cancellationToken).ConfigureAwait(false);

            // 跨天顺手清理超期文件（一天一次，代价可忽略）：
            // 原逻辑只在初始化与保存配置时清理，7×24 连续运行时超期文件永远不清。
            CleanBeyondPeriod(dateValue, _retainDays);
        }

        List<HistoryRecordEntry> Snapshot;

        lock (_sync)
        {
            if (_writeList.Count >= singleDayMaxRecordCount)
            {
                if (!_hintedUpperLimit)
                {
                    _hintedUpperLimit = true;
                    _log.LogWarning(
                        "历史记录已达单日上限 {Limit} 条，本日不再记录"
                        + "（通常意味着触发位被高频置位，或它本来就是常亮位而非动作完成位）",
                        singleDayMaxRecordCount);
                }

                return false;
            }

            _writeList.Add(Entry);
            Snapshot = [.. _writeList];
        }

        await WriteFileAsync(dateValue, Snapshot, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>有记录的日期集合（升序）。</summary>
    public IReadOnlyList<DateTime> HasRecordDateCollection()
    {
        if (!Directory.Exists(ConfigPath.HistoryRecordDirectory))
        {
            return [];
        }

        var result = new List<DateTime>();

        foreach (var file in Directory.EnumerateFiles(ConfigPath.HistoryRecordDirectory, "*.json"))
        {
            if (DateTime.TryParseExact(
                    Path.GetFileNameWithoutExtension(file),
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var dateValue))
            {
                result.Add(dateValue);
            }
        }

        return result.Distinct().OrderBy(dateValue => dateValue).ToList();
    }

    /// <summary>清除全部历史记录文件，并清空内存中当天的记录。</summary>
    /// <returns>删除的文件数量。</returns>
    public int ClearRemoveAll()
    {
        var deleted = 0;

        if (Directory.Exists(ConfigPath.HistoryRecordDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(ConfigPath.HistoryRecordDirectory, "*.json"))
            {
                File.Delete(file);
                deleted++;
            }
        }

        lock (_sync)
        {
            _writeList.Clear();
            _hintedUpperLimit = false;
        }

        _log.LogWarning("已清除全部历史记录：{Count} 个文件（{Dir}）", deleted, ConfigPath.HistoryRecordDirectory);
        return deleted;
    }

    /// <summary>删除早于保留期的历史文件（含 <c>.bak</c>）。</summary>
    /// <returns>清理的日期数量。</returns>
    public int CleanBeyondPeriod(DateTime TodayDate, int RetainDays)
    {
        var daysNumber = Math.Clamp(RetainDays, HistoryRecordConfig.minRetainDaysNumber, HistoryRecordConfig.maxRetainDaysNumber);
        var lowerLimit = TodayDate.Date.AddDays(-(daysNumber - 1));
        var Cleaned = 0;

        foreach (var dateValue in HasRecordDateCollection())
        {
            if (dateValue >= lowerLimit)
            {
                continue;
            }

            DeleteFile(dateValue);
            Cleaned++;
        }

        if (Cleaned > 0)
        {
            _log.LogInformation("已清理 {Count} 个超期历史文件（保留 {Days} 天）", Cleaned, daysNumber);
        }

        return Cleaned;
    }

    // ── 内部实现 ────────────────────────────────────────────────────────

    /// <summary>切换写入日：把目标日期已有记录装载进内存（只读一次）。</summary>
    private async Task SwitchWriteDayAsync(DateTime dateValue, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (dateValue == _writeDay)
            {
                return;
            }
        }

        var alreadyHas = await ReadFileAsync(dateValue, cancellationToken).ConfigureAwait(false);

        lock (_sync)
        {
            if (dateValue == _writeDay)
            {
                return;
            }

            _writeDay = dateValue;
            _writeList.Clear();
            _writeList.AddRange(alreadyHas);
            _hintedUpperLimit = false;
        }

        _log.LogInformation("历史记录写入日切换为 {Date}（已有 {Count} 条）", dateValue, alreadyHas.Count);
    }

    private async Task<List<HistoryRecordEntry>> ReadFileAsync(DateTime dateValue, CancellationToken cancellationToken)
    {
        var filePath = ConfigPath.HistoryRecordFile(dateValue);

        var resultValue = await ConfigFileLoader
            .LoadJsonAsync<HistoryRecordFile>(filePath, JsonConfig.Read, _log, cancellationToken)
            .ConfigureAwait(false);

        // 损坏 / 已从备份恢复必须让操作员看见：历史记录是追溯依据，不能安静地变空
        _health?.Report($"历史记录（{dateValue:yyyy-MM-dd}）", resultValue);

        if (resultValue.Value?.EntryCollection is null)
        {
            return [];
        }

        if (resultValue.loadResult is LoadResultSplitCategory.Loaded)
        {
            _log.LogDebug("已加载 {Date} 的历史记录 {Count} 条", dateValue, resultValue.Value.EntryCollection.Count);
        }

        return resultValue.Value.EntryCollection
            .Where(EntryItem => EntryItem.NumericValueCollection is not null)
            .OrderBy(EntryItem => EntryItem.Timestamp)
            .ToList();
    }

    private async Task WriteFileAsync(
        DateTime dateValue, List<HistoryRecordEntry> Snapshot, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(ConfigPath.HistoryRecordDirectory);

        var jsonText = JsonSerializer.Serialize(
            new HistoryRecordFile { Date = dateValue, EntryCollection = Snapshot }, JsonConfig.Write);

        await AtomicFile
            .WriteAllTextAsync(ConfigPath.HistoryRecordFile(dateValue), jsonText, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>删除某日文件（含原子写留下的 <c>.bak</c>，否则备份会一直躺在目录里）。</summary>
    private static void DeleteFile(DateTime dateValue)
    {
        var filePath = ConfigPath.HistoryRecordFile(dateValue);

        foreach (var path in new[] { filePath, filePath + ".bak" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>与 <c>data/history-records/yyyy-MM-dd.json</c> 的顶层结构对应。</summary>
    private sealed class HistoryRecordFile
    {
        public DateTime Date { get; set; }

        public List<HistoryRecordEntry>? EntryCollection { get; set; }
    }
}
