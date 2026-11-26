using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using 上位机.业务.配置;
using 上位机.核心.抽象;

namespace 上位机.业务.服务;

/// <summary>小时采样点：某个整点时刻的累计产量计数器读数。</summary>
public sealed class 产量小时样本
{
    /// <summary>整点时刻（如 2026-09-24 10:00）。</summary>
    public DateTime 小时起点 { get; set; }

    /// <summary>该时刻的累计产量读数（PLC 计数器原值，清零前单调递增）。</summary>
    public long 累计产量 { get; set; }
}

/// <summary>
/// 产量统计服务。详见 <see cref="产量统计接口"/>。
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
public sealed class 产量统计服务 : IHostedService, IAsyncDisposable, 产量统计接口
{
    /// <summary>历史保留天数：近 7 天图表 + 1 天缓冲。</summary>
    public const int 保留天数 = 8;

    private static readonly TimeSpan 采样间隔 = TimeSpan.FromSeconds(15);

    private static readonly string[] 星期名称 = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];

    private readonly ILogger<产量统计服务> _日志;
    private readonly 配置健康接口? _健康;
    private readonly object _同步 = new();
    private readonly List<产量小时样本> _样本 = [];

    private Func<long> _产量提供器;
    private Func<DateTime> _时钟;
    private CancellationTokenSource? _取消令牌源;
    private Task? _工作线程;
    private double _上次实时小时产量 = -1;

    /// <summary>统计快照更新（整点采样或实时值变化）。</summary>
    public event EventHandler? 统计已变更;

    public 产量统计服务(
        ILogger<产量统计服务> 日志,
        配置健康接口? 健康 = null,
        Func<long>? 产量提供器 = null,
        Func<DateTime>? 时钟提供器 = null)
    {
        _日志 = 日志;
        _健康 = 健康;
        _产量提供器 = 产量提供器 ?? (() => 0);
        _时钟 = 时钟提供器 ?? (() => DateTime.Now);
    }

    /// <inheritdoc />
    public IReadOnlyList<double> 今日小时产量 => 取小时序列(0);

    /// <inheritdoc />
    public IReadOnlyList<double> 昨日小时产量 => 取小时序列(1);

    /// <inheritdoc />
    public IReadOnlyList<double> 周每日产量
    {
        get
        {
            var 今日值 = _时钟().Date;
            var 结果 = new double[7];

            for (var 天 = 0; 天 < 7; 天++)
            {
                var 日期值 = 今日值.AddDays(天 - 6);
                var 合计 = 0d;

                for (var 小时 = 0; 小时 < 24; 小时++)
                {
                    合计 += 小时产量(日期值.AddHours(小时));
                }

                结果[天] = 合计;
            }

            return 结果;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> 周日期标签
    {
        get
        {
            var 今日值 = _时钟().Date;

            // 2026-11-19 性能改造：日期标签是纯日期函数（不含实时产量），
            // 缓存后同一自然日内只算一次；日期变化自动重算。
            // 注意：产量序列【不缓存】——「当前小时」那一格取的是实时产量，
            // 缓存会让图表当前小时冻结到下一次采样，属功能退化。
            if (_日期标签缓存 is { } 已缓存 && 已缓存.日期 == 今日值)
            {
                return 已缓存.标签;
            }

            var 标签 = new string[7];

            for (var 天 = 0; 天 < 7; 天++)
            {
                var 日期值 = 今日值.AddDays(天 - 6);
                标签[天] = $"{日期值:MM-dd} {星期名称[(int)日期值.DayOfWeek]}";
            }

            _日期标签缓存 = (今日值, 标签);

            return 标签;
        }
    }

    /// <summary>周日期标签的按日缓存（纯日期函数，与产量样本无关）。</summary>
    private (DateTime 日期, string[] 标签)? _日期标签缓存;

    // ── 采样与持久化 ────────────────────────────────────────────────────

    /// <summary>
    /// 检查是否跨过整点并打采样点。<paramref name="指定时刻"/> 供测试注入固定时间。
    /// </summary>
    /// <returns>是否新增了采样点（调用方据此决定是否落盘）。</returns>
    public bool 检查采样(DateTime? 指定时刻 = null)
    {
        var 时刻值 = 指定时刻 ?? _时钟();
        var 当前小时 = new DateTime(时刻值.Year, 时刻值.Month, 时刻值.Day, 时刻值.Hour, 0, 0);

        lock (_同步)
        {
            if (_样本.Count > 0)
            {
                var 最后 = _样本[^1];

                // 时钟回拨或仍在同一小时：不打点
                if (当前小时 <= 最后.小时起点)
                {
                    return false;
                }

                // 补齐缺口小时（程序未运行期间）：沿用最后读数，这些小时产量记 0
                var 补点 = 最后.小时起点.AddHours(1);

                while (补点 < 当前小时)
                {
                    _样本.Add(new 产量小时样本 { 小时起点 = 补点, 累计产量 = 最后.累计产量 });
                    补点 = 补点.AddHours(1);
                }
            }

            _样本.Add(new 产量小时样本 { 小时起点 = 当前小时, 累计产量 = _产量提供器() });
            修剪();
        }

        _日志.LogInformation("产量小时采样：{Hour:yyyy-MM-dd HH:mm} 累计 {Count} 件", 当前小时, _样本[^1].累计产量);
        return true;
    }

    /// <summary>把采样历史原子落盘 <c>data/production-history.json</c>。</summary>
    public async Task 异步保存()
    {
        List<产量小时样本> 快照;

        lock (_同步)
        {
            快照 = [.. _样本];
        }

        try
        {
            Directory.CreateDirectory(配置路径.数据目录);
            var JSON文本 = JsonSerializer.Serialize(new 产量历史文件 { 样本集合 = 快照 }, JSON配置.写入);
            await 原子文件.异步写入全部文本(配置路径.产量历史, JSON文本).ConfigureAwait(false);
        }
        catch (Exception 异常)
        {
            _日志.LogError(异常, "产量统计历史落盘失败：{Path}", 配置路径.产量历史);
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken 取消令牌)
    {
        加载();

        // 启动即打一个基准样本：当前小时之后的实时产量从这里起算
        检查采样();

        _取消令牌源 = new CancellationTokenSource();
        _工作线程 = Task.Run(() => 异步运行(_取消令牌源.Token));
        统计已变更?.Invoke(this, EventArgs.Empty);

        _日志.LogInformation("产量统计服务已启动（15s 检查 / 整点采样）");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken 取消令牌)
    {
        if (_取消令牌源 is null)
        {
            return;
        }

        await _取消令牌源.CancelAsync().ConfigureAwait(false);

        if (_工作线程 is not null)
        {
            try
            {
                await _工作线程.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 退出时循环未及时结束：落盘由下面兜底
            }
        }

        await 异步保存().ConfigureAwait(false);
        _日志.LogInformation("产量统计服务已停止（{Count} 个小时样本）", _样本.Count);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_取消令牌源 is not null)
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            _取消令牌源.Dispose();
            _取消令牌源 = null;
        }
    }

    private async Task 异步运行(CancellationToken 取消令牌)
    {
        while (!取消令牌.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(采样间隔, 取消令牌).ConfigureAwait(false);

                if (检查采样())
                {
                    await 异步保存().ConfigureAwait(false);
                    通知更新();
                    continue;
                }

                // 当前小时实时值变化（产量在涨）也要推给图表
                var 实时值 = 当前小时实时产量();

                if (Math.Abs(实时值 - _上次实时小时产量) > 0.001)
                {
                    _上次实时小时产量 = 实时值;
                    通知更新();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception 异常)
            {
                _日志.LogError(异常, "产量统计循环异常，继续下一轮");
            }
        }
    }

    private void 通知更新() => 统计已变更?.Invoke(this, EventArgs.Empty);

    private void 加载()
    {
        lock (_同步)
        {
            _样本.Clear();

            var 结果值 = 配置文件加载器.加载JSON<产量历史文件>(配置路径.产量历史, JSON配置.读取, _日志);
            _健康?.上报("产量统计历史", 结果值);

            if (结果值.值?.样本集合 is not null)
            {
                _样本.AddRange(结果值.值.样本集合
                    .Where(条目项 => 条目项.小时起点 != default)
                    .OrderBy(条目项 => 条目项.小时起点));
            }

            if (结果值.加载结果 == 加载结果分类.已加载)
            {
                _日志.LogInformation("已加载 {Count} 个产量小时样本，来自 {Path}",
                    _样本.Count, 配置路径.产量历史);
            }
        }
    }

    private void 修剪()
    {
        var 下限 = _时钟().Date.AddDays(-(保留天数 - 1));
        _样本.RemoveAll(条目项 => 条目项.小时起点 < 下限);
    }

    private IReadOnlyList<double> 取小时序列(int 天偏移)
    {
        var 起始日期 = _时钟().Date.AddDays(-天偏移);
        var 结果 = new double[24];

        for (var 小时 = 0; 小时 < 24; 小时++)
        {
            结果[小时] = 小时产量(起始日期.AddHours(小时));
        }

        return 结果;
    }

    /// <summary>
    /// 某个小时的产量 = 下一整点快照 − 本整点快照。
    /// <para>差值为负说明计数器被清零（数据清除），产量取下一快照值；
    /// 没有下一快照时：今天的当前小时用实时值，历史末端缺口记 0。</para>
    /// </summary>
    private double 小时产量(DateTime 小时起点值)
    {
        lock (_同步)
        {
            var 本点 = 查找样本(小时起点值);

            if (本点 is null)
            {
                return 0;
            }

            var 下点 = 查找样本(小时起点值.AddHours(1));

            if (下点 is not null)
            {
                var 差值 = 下点.累计产量 - 本点.累计产量;
                return 差值 >= 0 ? 差值 : 下点.累计产量;
            }

            var 当前小时 = new DateTime(_时钟().Year, _时钟().Month, _时钟().Day, _时钟().Hour, 0, 0);

            if (小时起点值 == 当前小时)
            {
                var 当前累计 = _产量提供器();
                var 实时差 = 当前累计 - 本点.累计产量;
                return 实时差 >= 0 ? 实时差 : 当前累计;
            }

            return 0;
        }
    }

    private double 当前小时实时产量()
    {
        var 时刻值 = _时钟();
        return 小时产量(new DateTime(时刻值.Year, 时刻值.Month, 时刻值.Day, 时刻值.Hour, 0, 0));
    }

    private 产量小时样本? 查找样本(DateTime 小时起点值)
    {
        // 列表按时间有序且无重复：二分比 FirstOrDefault 稳。
        // 2026-11-19 性能改造：比较器提为静态字段 —— 原先每次调用都
        // Comparer<T>.Create 分配一个委托对象，而图表刷新一次就要扇出上百次查找。
        var 序号 = _样本.BinarySearch(
            new 产量小时样本 { 小时起点 = 小时起点值 }, 样本比较器);

        return 序号 >= 0 ? _样本[序号] : null;
    }

    /// <summary>按「小时起点」比较两个产量样本（静态复用，避免每次二分都新建委托）。</summary>
    private static readonly Comparer<产量小时样本> 样本比较器 =
        Comparer<产量小时样本>.Create((甲, 乙) => 甲.小时起点.CompareTo(乙.小时起点));

    /// <summary>与 <c>data/production-history.json</c> 的顶层结构对应。</summary>
    private sealed class 产量历史文件
    {
        public List<产量小时样本>? 样本集合 { get; set; }
    }
}
