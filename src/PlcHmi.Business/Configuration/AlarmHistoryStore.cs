using System.Text.Json;
using Microsoft.Extensions.Logging;
using 上位机.核心.模型;

namespace 上位机.业务.配置;

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
public sealed class 报警历史存储 : IDisposable
{
    /// <summary>落盘的历史条数上限，与内存环形缓冲一致。</summary>
    public const int 最大记录 = 1000;

    private readonly ILogger<报警历史存储> _日志;
    private readonly 配置健康接口? _健康;

    /// <summary>保护"待写快照 + 是否已有写入循环在跑"这两项状态。</summary>
    private readonly object _同步 = new();

    /// <summary>已排队但尚未落盘的最新快照；<c>null</c> 表示无待写内容。</summary>
    private List<报警条目>? _待处理;

    /// <summary>当前这次落盘循环。用它判断"是否已有循环在跑"，也是退出前等待的对象。</summary>
    private Task _冲刷循环 = Task.CompletedTask;

    private bool _已释放;

    public 报警历史存储(ILogger<报警历史存储> 日志, 配置健康接口? 健康 = null)
    {
        _日志 = 日志;
        _健康 = 健康;
    }

    /// <summary>
    /// 从磁盘读取历史。失败或首次运行时返回空列表（不阻断启动）。
    /// </summary>
    public IReadOnlyList<报警条目> 加载()
    {
        var 结果值 = 配置文件加载器.加载JSON<报警历史文件>(
            配置路径.报警历史, JSON配置.读取, _日志);

        _健康.上报("报警历史", 结果值);

        if (结果值.值?.记录集合 is null)
        {
            return [];
        }

        if (结果值.加载结果 is 加载结果分类.已加载)
        {
            _日志.LogInformation("已加载 {Count} 条报警历史", 结果值.值.记录集合.Count);
        }

        return 结果值.值.记录集合;
    }

    /// <summary>
    /// 请求把当前历史写回磁盘。<b>非阻塞</b>：报警产生路径不应等待磁盘。
    /// 连续调用会自动合并成一次写入。
    /// </summary>
    public void 请求保存(IEnumerable<报警条目> 记录列表)
    {
        if (_已释放)
        {
            return;
        }

        lock (_同步)
        {
            // 只保留最新快照：上一次还没写就来了新的，直接替换（历史是全量快照，不是增量日志）
            _待处理 = 记录列表.Take(最大记录).Select(坐标R => 坐标R.克隆()).ToList();

            if (!_冲刷循环.IsCompleted)
            {
                // 已有循环在跑，它会顺手把这份最新快照写掉
                return;
            }

            // 用 Task.Run 起：不在持锁状态下跑序列化（那是 CPU 工作，会把锁拖长）
            _冲刷循环 = Task.Run(异步运行冲刷循环);
        }
    }

    /// <summary>
    /// 退出前等待落盘完成，最多等 <paramref name="超时"/>。
    /// <para>必须等**当前那次循环**，而不是另起一次 —— 另起的那次会看到
    /// <see cref="_待处理"/> 已被取空而立即返回，等于没等。</para>
    /// </summary>
    public void 退出前冲刷(TimeSpan 超时)
    {
        Task 循环;

        lock (_同步)
        {
            循环 = _冲刷循环;
        }

        try
        {
            循环.Wait(超时);
        }
        catch (Exception 异常) when (异常 is AggregateException or TimeoutException)
        {
            _日志.LogWarning("退出前写入报警历史超时，可能丢失最后几条记录");
        }
    }

    /// <summary>把待写快照写盘；写入期间到来的新快照会被合并进同一循环的下一轮。</summary>
    private async Task 异步运行冲刷循环()
    {
        while (true)
        {
            List<报警条目>? 快照;

            lock (_同步)
            {
                快照 = _待处理;
                _待处理 = null;
            }

            if (快照 is null)
            {
                return;
            }

            try
            {
                var JSON文本 = JsonSerializer.Serialize(
                    new 报警历史文件 { 记录集合 = 快照 }, JSON配置.写入);

                await 原子文件
                    .异步写入全部文本(配置路径.报警历史, JSON文本)
                    .ConfigureAwait(false);
            }
            catch (Exception 异常)
            {
                // 落盘失败不能反过来影响报警处理本身：记日志后继续
                _日志.LogError(异常, "报警历史落盘失败：{Path}", 配置路径.报警历史);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_已释放)
        {
            return;
        }

        退出前冲刷(TimeSpan.FromSeconds(2));
        _已释放 = true;
    }

    /// <summary>与 <c>data/alarm-history.json</c> 的顶层结构对应。</summary>
    private sealed class 报警历史文件
    {
        public List<报警条目>? 记录集合 { get; set; }
    }
}
