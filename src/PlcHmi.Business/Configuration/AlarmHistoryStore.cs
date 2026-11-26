using System.Text.Json;
using Microsoft.Extensions.Logging;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Configuration;

/// <summary>
/// 报警历史的落盘存储。
///
/// <para><b>为什么必须落盘：</b>报警历史此前只存在内存 <c>List</c> 里，程序一重启就全没了。
/// 对一台替代触控屏的上位机，这意味着交接班时查不到上一班发生过什么故障 ——
/// 而硬件 HMI 面板本来就会把报警历史存在非易失存储器里。
/// 断电、重启、换班，都是这条数据必须活过去的场景。</para>
///
/// <para><b>写入策略：合并 + 串行。</b>报警可能成串到来（一个故障引发十几条），
/// 每条都立刻落盘既浪费又可能互相踩。这里只保留"最新快照"，
/// 由单条后台循环取走写入 —— 写入期间到来的请求会被合并进下一次，不会排队堆积。</para>
/// </summary>
public sealed class AlarmHistoryStore : IDisposable
{
    /// <summary>落盘的历史条数上限，与内存环形缓冲一致。</summary>
    public const int maxRecord = 1000;

    private readonly ILogger<AlarmHistoryStore> _log;
    private readonly IConfigHealth? _health;

    /// <summary>保护"待写快照 + 是否已有写入循环在跑"这两项状态。</summary>
    private readonly object _sync = new();

    /// <summary>已排队但尚未落盘的最新快照；<c>null</c> 表示无待写内容。</summary>
    private List<AlarmEntry>? _pendingProcess;

    /// <summary>当前这次落盘循环。用它判断"是否已有循环在跑"，也是退出前等待的对象。</summary>
    private Task _flushLoopRing = Task.CompletedTask;

    private bool _disposed;

    public AlarmHistoryStore(ILogger<AlarmHistoryStore> Log, IConfigHealth? Health = null)
    {
        _log = Log;
        _health = Health;
    }

    /// <summary>
    /// 从磁盘读取历史。失败或首次运行时返回空列表（不阻断启动）。
    /// </summary>
    public IReadOnlyList<AlarmEntry> Load()
    {
        var resultValue = ConfigFileLoader.LoadJson<AlarmHistoryFile>(
            ConfigPath.AlarmHistory, JsonConfig.Read, _log);

        _health.Report("报警历史", resultValue);

        if (resultValue.Value?.RecordCollection is null)
        {
            return [];
        }

        if (resultValue.loadResult is LoadResultSplitCategory.Loaded)
        {
            _log.LogInformation("已加载 {Count} 条报警历史", resultValue.Value.RecordCollection.Count);
        }

        return resultValue.Value.RecordCollection;
    }

    /// <summary>
    /// 请求把当前历史写回磁盘。<b>非阻塞</b>：报警产生路径不应等待磁盘。
    /// 连续调用会自动合并成一次写入。
    /// </summary>
    public void RequestSave(IEnumerable<AlarmEntry> recordList)
    {
        if (_disposed)
        {
            return;
        }

        lock (_sync)
        {
            // 只保留最新快照：上一次还没写就来了新的，直接替换（历史是全量快照，不是增量日志）
            _pendingProcess = recordList.Take(maxRecord).Select(coordinateR => coordinateR.Clone()).ToList();

            if (!_flushLoopRing.IsCompleted)
            {
                // 已有循环在跑，它会顺手把这份最新快照写掉
                return;
            }

            // 用 Task.Run 起：不在持锁状态下跑序列化（那是 CPU 工作，会把锁拖长）
            _flushLoopRing = Task.Run(RunFlushLoopRingAsync);
        }
    }

    /// <summary>
    /// 退出前等待落盘完成，最多等 <paramref name="timeout"/>。
    /// <para>必须等**当前那次循环**，而不是另起一次 —— 另起的那次会看到
    /// <see cref="_pendingProcess"/> 已被取空而立即返回，等于没等。</para>
    /// </summary>
    public void ExitBeforeFlush(TimeSpan timeout)
    {
        Task LoopRing;

        lock (_sync)
        {
            LoopRing = _flushLoopRing;
        }

        try
        {
            LoopRing.Wait(timeout);
        }
        catch (Exception exception) when (exception is AggregateException or TimeoutException)
        {
            _log.LogWarning("退出前写入报警历史超时，可能丢失最后几条记录");
        }
    }

    /// <summary>把待写快照写盘；写入期间到来的新快照会被合并进同一循环的下一轮。</summary>
    private async Task RunFlushLoopRingAsync()
    {
        while (true)
        {
            List<AlarmEntry>? Snapshot;

            lock (_sync)
            {
                Snapshot = _pendingProcess;
                _pendingProcess = null;
            }

            if (Snapshot is null)
            {
                return;
            }

            try
            {
                var jsonText = JsonSerializer.Serialize(
                    new AlarmHistoryFile { RecordCollection = Snapshot }, JsonConfig.Write);

                await AtomicFile
                    .WriteAllTextAsync(ConfigPath.AlarmHistory, jsonText)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // 落盘失败不能反过来影响报警处理本身：记日志后继续
                _log.LogError(exception, "报警历史落盘失败：{Path}", ConfigPath.AlarmHistory);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        ExitBeforeFlush(TimeSpan.FromSeconds(2));
        _disposed = true;
    }

    /// <summary>与 <c>data/alarm-history.json</c> 的顶层结构对应。</summary>
    private sealed class AlarmHistoryFile
    {
        public List<AlarmEntry>? RecordCollection { get; set; }
    }
}
