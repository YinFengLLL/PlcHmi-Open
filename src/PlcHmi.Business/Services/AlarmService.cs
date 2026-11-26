using Microsoft.Extensions.Logging;
using PlcHmi.Business.Configuration;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// 报警服务。管理活动报警与历史报警。
/// <para>报警来源：PLC 的报警字位（本阶段先提供 Raise/Clear 接口，
/// 待点位表确定后接入报警字解析）。</para>
/// </summary>
public sealed class AlarmService : IAlarmService
{
    /// <summary>历史报警保留上限。</summary>
    private const int maxHistoryCount = 1000;

    private readonly TagWriter _writer;
    private readonly ILogger<AlarmService> _log;
    private readonly AlarmHistoryStore? _historyStore;
    private readonly object _sync = new();

    private readonly List<AlarmEntry> _active = new();
    private readonly List<AlarmEntry> _history = new();

    /// <param name="writer">PLC 写入器（用于复位命令）。</param>
    /// <param name="logger">日志。</param>
    /// <param name="historyStore">
    /// 历史落盘存储。<c>null</c> 表示只保留在内存（单元测试用）——
    /// 生产环境由 DI 注入，报警历史跨重启保留。
    /// </param>
    public AlarmService(
        TagWriter Writer, ILogger<AlarmService> Log, AlarmHistoryStore? HistoryStore = null)
    {
        _writer = Writer;
        _log = Log;
        _historyStore = HistoryStore;

        // 先把上一班的历史读回来：报警历史必须跨重启存活（硬件 HMI 面板本来就会存）
        if (_historyStore is not null)
        {
            _history.AddRange(_historyStore.Load().Take(maxHistoryCount));
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<AlarmEntry> ActiveAlarm
    {
        get
        {
            lock (_sync)
            {
                return _active.ToList();
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<AlarmEntry> History
    {
        get
        {
            lock (_sync)
            {
                return _history.ToList();
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<AlarmEntry>? alarmTriggered;

    /// <inheritdoc />
    public event EventHandler<AlarmEntry>? alarmCleared;

    /// <summary>触发一条报警。相同代码的活动报警不会重复触发。</summary>
    public void Trigger(string CodeRawValue, string Message, AlarmLevel Level)
    {
        AlarmEntry Entry;

        lock (_sync)
        {
            var currentHas = _active.FirstOrDefault(item => item.Code == CodeRawValue);
            if (currentHas is not null)
            {
                return;
            }

            Entry = new AlarmEntry
            {
                Code = CodeRawValue,
                Message = Message,
                Level = Level,
                Triggered = DateTime.Now,
            };

            _active.Add(Entry);
            _history.Insert(0, Entry.Clone());

            if (_history.Count > maxHistoryCount)
            {
                _history.RemoveRange(maxHistoryCount, _history.Count - maxHistoryCount);
            }
        }

        _log.LogInformation("报警触发 [{Level}] {Code} {Message}", Level, CodeRawValue, Message);
        PersistHistory();
        alarmTriggered?.Invoke(this, Entry);
    }

    /// <summary>消除一条报警。</summary>
    public void ClearEmpty(string CodeRawValue)
    {
        AlarmEntry? Entry;

        lock (_sync)
        {
            Entry = _active.FirstOrDefault(item => item.Code == CodeRawValue);
            if (Entry is null)
            {
                return;
            }

            Entry.Cleared = DateTime.Now;
            _active.Remove(Entry);

            var HistoryDataItem = _history.FirstOrDefault(historyEntry => historyEntry.Code == CodeRawValue && historyEntry.Cleared is null);
            if (HistoryDataItem is not null)
            {
                HistoryDataItem.Cleared = Entry.Cleared;
            }
        }

        _log.LogInformation("报警消除 {Code}", CodeRawValue);
        PersistHistory();
        alarmCleared?.Invoke(this, Entry);
    }

    /// <inheritdoc />
    public void Confirm(string CodeRawValue)
    {
        lock (_sync)
        {
            var Entry = _active.FirstOrDefault(item => item.Code == CodeRawValue);
            if (Entry is null)
            {
                return;
            }

            Entry.IsAlreadyConfirm = true;

            var HistoryDataItem = _history.FirstOrDefault(historyEntry => historyEntry.Code == CodeRawValue && historyEntry.Cleared is null);
            if (HistoryDataItem is not null)
            {
                HistoryDataItem.IsAlreadyConfirm = true;
            }
        }

        PersistHistory();
    }

    /// <inheritdoc />
    public void ConfirmAll()
    {
        lock (_sync)
        {
            foreach (var Entry in _active)
            {
                Entry.IsAlreadyConfirm = true;

                // 历史里的同一条也必须同步。
                // 此前只改 _active，导致"全部确认"之后历史列表仍显示未确认 ——
                // 同一件事在同一个界面上有两种说法，操作员只会信错的那个。
                var HistoryDataItem = _history.FirstOrDefault(
                    historyEntry => historyEntry.Code == Entry.Code && historyEntry.Cleared is null);

                if (HistoryDataItem is not null)
                {
                    HistoryDataItem.IsAlreadyConfirm = true;
                }
            }
        }

        PersistHistory();
    }

    /// <inheritdoc />
    public Task ResetAsync(CancellationToken cancellationToken = default) =>
        _writer.PulseAsync("CmdReset", TimeSpan.FromMilliseconds(200), cancellationToken);

    /// <inheritdoc />
    public void ClearEmptyHistory()
    {
        lock (_sync)
        {
            _history.Clear();
        }

        PersistHistory();
    }

    /// <summary>
    /// 把当前历史交给落盘存储（非阻塞，内部合并连续请求）。
    /// 取快照时已出锁，避免磁盘 I/O 与报警处理互相等待。
    /// </summary>
    private void PersistHistory()
    {
        if (_historyStore is null)
        {
            return;
        }

        List<AlarmEntry> Snapshot;

        lock (_sync)
        {
            Snapshot = _history.ToList();
        }

        _historyStore.RequestSave(Snapshot);
    }
}
