using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PlcHmi.Business.Configuration;
using PlcHmi.Core.Abstractions;

namespace PlcHmi.Business.Services;

/// <summary>小时采样点：某个整点时刻的累计产量计数器读数。</summary>
public sealed class OutputHourSample
{
    /// <summary>整点时刻（如 2026-09-24 10:00）。</summary>
    public DateTime HourStartPoint { get; set; }

    /// <summary>该时刻的累计产量读数（PLC 计数器原值，清零前单调递增）。</summary>
    public long AccumulatedOutput { get; set; }
}

/// <summary>
/// 产量统计服务。详见 <see cref="IOutputStatistics"/>。
///
/// <para><b>采样策略</b>：后台循环每 15 秒检查一次，跨过整点即打一个采样点；
/// 相邻采样点差值 = 该小时产量。程序未运行期间的小时没有真实读数，
/// 沿用缺口前的最后读数补点——缺口小时的产量记 0，
/// 停机期间的少量产出会体现在紧邻下一个真实采样之前的那个小时段
/// （差值 = 下一真实快照 − 缺口前最后读数）。</para>
///
/// <para><b>可测试性</b>：产量来源与时钟都是委托（生产环境注入
/// <c>设备服务接口.输出数量</c> 与 <c>DateTime.Now</c>），
/// 单元测试用固定时刻与可控计数器直接验证统计口径。</para>
/// </summary>
public sealed class OutputStatisticsService : IHostedService, IAsyncDisposable, IOutputStatistics
{
    /// <summary>历史保留天数：近 7 天图表 + 1 天缓冲。</summary>
    public const int RetainDays = 8;

    private static readonly TimeSpan samplingInterval = TimeSpan.FromSeconds(15);

    private static readonly string[] starPeriodName = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];

    private readonly ILogger<OutputStatisticsService> _log;
    private readonly IConfigHealth? _health;
    private readonly object _sync = new();
    private readonly List<OutputHourSample> _sample = [];

    private Func<long> _outputProvider;
    private Func<DateTime> _clock;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _workerThread;
    private double _lastRealtimeHourlyOutput = -1;

    /// <summary>统计快照更新（整点采样或实时值变化）。</summary>
    public event EventHandler? statisticsChanged;

    public OutputStatisticsService(
        ILogger<OutputStatisticsService> Log,
        IConfigHealth? Health = null,
        Func<long>? OutputProvider = null,
        Func<DateTime>? clockProvider = null)
    {
        _log = Log;
        _health = Health;
        _outputProvider = OutputProvider ?? (() => 0);
        _clock = clockProvider ?? (() => DateTime.Now);
    }

    /// <inheritdoc />
    public IReadOnlyList<double> TodayHourlyOutput => GetHourIndexColumn(0);

    /// <inheritdoc />
    public IReadOnlyList<double> YesterdayDayHourlyOutput => GetHourIndexColumn(1);

    /// <inheritdoc />
    public IReadOnlyList<double> WeekPerDayOutput
    {
        get
        {
            var todayValue = _clock().Date;
            var result = new double[7];

            for (var dayCount = 0; dayCount < 7; dayCount++)
            {
                var dateValue = todayValue.AddDays(dayCount - 6);
                var total = 0d;

                for (var hour = 0; hour < 24; hour++)
                {
                    total += HourlyOutput(dateValue.AddHours(hour));
                }

                result[dayCount] = total;
            }

            return result;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> WeekDateTag
    {
        get
        {
            var todayValue = _clock().Date;

            // 2026-11-19 性能改造：日期标签是纯日期函数（不含实时产量），
            // 缓存后同一自然日内只算一次；日期变化自动重算。
            // 注意：产量序列【不缓存】——「当前小时」那一格取的是实时产量，
            // 缓存会让图表当前小时冻结到下一次采样，属功能退化。
            if (_dateTagBufferStore is { } cached && cached.Date == todayValue)
            {
                return cached.Tag;
            }

            var Tag = new string[7];

            for (var dayCount = 0; dayCount < 7; dayCount++)
            {
                var dateValue = todayValue.AddDays(dayCount - 6);
                Tag[dayCount] = $"{dateValue:MM-dd} {starPeriodName[(int)dateValue.DayOfWeek]}";
            }

            _dateTagBufferStore = (todayValue, Tag);

            return Tag;
        }
    }

    /// <summary>周日期标签的按日缓存（纯日期函数，与产量样本无关）。</summary>
    private (DateTime Date, string[] Tag)? _dateTagBufferStore;

    // ── 采样与持久化 ────────────────────────────────────────────────────

    /// <summary>
    /// 检查是否跨过整点并打采样点。<paramref name="specifiedTimestamp"/> 供测试注入固定时间。
    /// </summary>
    /// <returns>是否新增了采样点（调用方据此决定是否落盘）。</returns>
    public bool CheckSampling(DateTime? specifiedTimestamp = null)
    {
        var timestampValue = specifiedTimestamp ?? _clock();
        var currentHour = new DateTime(timestampValue.Year, timestampValue.Month, timestampValue.Day, timestampValue.Hour, 0, 0);

        lock (_sync)
        {
            if (_sample.Count > 0)
            {
                var Last = _sample[^1];

                // 时钟回拨或仍在同一小时：不打点
                if (currentHour <= Last.HourStartPoint)
                {
                    return false;
                }

                // 补齐缺口小时（程序未运行期间）：沿用最后读数，这些小时产量记 0
                var fillPoint = Last.HourStartPoint.AddHours(1);

                while (fillPoint < currentHour)
                {
                    _sample.Add(new OutputHourSample { HourStartPoint = fillPoint, AccumulatedOutput = Last.AccumulatedOutput });
                    fillPoint = fillPoint.AddHours(1);
                }
            }

            _sample.Add(new OutputHourSample { HourStartPoint = currentHour, AccumulatedOutput = _outputProvider() });
            Trim();
        }

        _log.LogInformation("产量小时采样：{Hour:yyyy-MM-dd HH:mm} 累计 {Count} 件", currentHour, _sample[^1].AccumulatedOutput);
        return true;
    }

    /// <summary>把采样历史原子落盘 <c>data/production-history.json</c>。</summary>
    public async Task SaveAsync()
    {
        List<OutputHourSample> Snapshot;

        lock (_sync)
        {
            Snapshot = [.. _sample];
        }

        try
        {
            Directory.CreateDirectory(ConfigPath.DataDirectory);
            var jsonText = JsonSerializer.Serialize(new OutputHistoryFile { SampleCollection = Snapshot }, JsonConfig.Write);
            await AtomicFile.WriteAllTextAsync(ConfigPath.OutputHistory, jsonText).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "产量统计历史落盘失败：{Path}", ConfigPath.OutputHistory);
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Load();

        // 启动即打一个基准样本：当前小时之后的实时产量从这里起算
        CheckSampling();

        _cancellationTokenSource = new CancellationTokenSource();
        _workerThread = Task.Run(() => RunAsync(_cancellationTokenSource.Token));
        statisticsChanged?.Invoke(this, EventArgs.Empty);

        _log.LogInformation("产量统计服务已启动（15s 检查 / 整点采样）");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cancellationTokenSource is null)
        {
            return;
        }

        await _cancellationTokenSource.CancelAsync().ConfigureAwait(false);

        if (_workerThread is not null)
        {
            try
            {
                await _workerThread.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 退出时循环未及时结束：落盘由下面兜底
            }
        }

        await SaveAsync().ConfigureAwait(false);
        _log.LogInformation("产量统计服务已停止（{Count} 个小时样本）", _sample.Count);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_cancellationTokenSource is not null)
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = null;
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(samplingInterval, cancellationToken).ConfigureAwait(false);

                if (CheckSampling())
                {
                    await SaveAsync().ConfigureAwait(false);
                    NotifyUpdate();
                    continue;
                }

                // 当前小时实时值变化（产量在涨）也要推给图表
                var realtimeValue = CurrentHourRealtimeOutput();

                if (Math.Abs(realtimeValue - _lastRealtimeHourlyOutput) > 0.001)
                {
                    _lastRealtimeHourlyOutput = realtimeValue;
                    NotifyUpdate();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                _log.LogError(exception, "产量统计循环异常，继续下一轮");
            }
        }
    }

    private void NotifyUpdate() => statisticsChanged?.Invoke(this, EventArgs.Empty);

    private void Load()
    {
        lock (_sync)
        {
            _sample.Clear();

            var resultValue = ConfigFileLoader.LoadJson<OutputHistoryFile>(ConfigPath.OutputHistory, JsonConfig.Read, _log);
            _health?.Report("产量统计历史", resultValue);

            if (resultValue.Value?.SampleCollection is not null)
            {
                _sample.AddRange(resultValue.Value.SampleCollection
                    .Where(EntryItem => EntryItem.HourStartPoint != default)
                    .OrderBy(EntryItem => EntryItem.HourStartPoint));
            }

            if (resultValue.loadResult == LoadResultSplitCategory.Loaded)
            {
                _log.LogInformation("已加载 {Count} 个产量小时样本，来自 {Path}",
                    _sample.Count, ConfigPath.OutputHistory);
            }
        }
    }

    private void Trim()
    {
        var lowerLimit = _clock().Date.AddDays(-(RetainDays - 1));
        _sample.RemoveAll(EntryItem => EntryItem.HourStartPoint < lowerLimit);
    }

    private IReadOnlyList<double> GetHourIndexColumn(int daysOffset)
    {
        var startDate = _clock().Date.AddDays(-daysOffset);
        var result = new double[24];

        for (var hour = 0; hour < 24; hour++)
        {
            result[hour] = HourlyOutput(startDate.AddHours(hour));
        }

        return result;
    }

    /// <summary>
    /// 某个小时的产量 = 下一整点快照 − 本整点快照。
    /// <para>差值为负说明计数器被清零（数据清除），产量取下一快照值；
    /// 没有下一快照时：今天的当前小时用实时值，历史末端缺口记 0。</para>
    /// </summary>
    private double HourlyOutput(DateTime hourStartPointValue)
    {
        lock (_sync)
        {
            var thisPoint = FindSample(hourStartPointValue);

            if (thisPoint is null)
            {
                return 0;
            }

            var downPoint = FindSample(hourStartPointValue.AddHours(1));

            if (downPoint is not null)
            {
                var delta = downPoint.AccumulatedOutput - thisPoint.AccumulatedOutput;
                return delta >= 0 ? delta : downPoint.AccumulatedOutput;
            }

            var currentHour = new DateTime(_clock().Year, _clock().Month, _clock().Day, _clock().Hour, 0, 0);

            if (hourStartPointValue == currentHour)
            {
                var currentAccumulated = _outputProvider();
                var realtimeDifference = currentAccumulated - thisPoint.AccumulatedOutput;
                return realtimeDifference >= 0 ? realtimeDifference : currentAccumulated;
            }

            return 0;
        }
    }

    private double CurrentHourRealtimeOutput()
    {
        var timestampValue = _clock();
        return HourlyOutput(new DateTime(timestampValue.Year, timestampValue.Month, timestampValue.Day, timestampValue.Hour, 0, 0));
    }

    private OutputHourSample? FindSample(DateTime hourStartPointValue)
    {
        // 列表按时间有序且无重复：二分比 FirstOrDefault 稳。
        // 2026-11-19 性能改造：比较器提为静态字段 —— 原先每次调用都
        // Comparer<T>.Create 分配一个委托对象，而图表刷新一次就要扇出上百次查找。
        var Sequence = _sample.BinarySearch(
            new OutputHourSample { HourStartPoint = hourStartPointValue }, sampleComparer);

        return Sequence >= 0 ? _sample[Sequence] : null;
    }

    /// <summary>按「小时起点」比较两个产量样本（静态复用，避免每次二分都新建委托）。</summary>
    private static readonly Comparer<OutputHourSample> sampleComparer =
        Comparer<OutputHourSample>.Create((item, item2) => item.HourStartPoint.CompareTo(item2.HourStartPoint));

    /// <summary>与 <c>data/production-history.json</c> 的顶层结构对应。</summary>
    private sealed class OutputHistoryFile
    {
        public List<OutputHourSample>? SampleCollection { get; set; }
    }
}
