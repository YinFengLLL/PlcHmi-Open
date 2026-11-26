using 上位机.核心.抽象;
using 上位机.核心.枚举;

namespace 上位机.上位机测试.替身;

/// <summary>
/// 采集引擎的测试替身：只记录"被要求重建了几次"，不真的起循环。
///
/// <para><b>为什么需要它</b>：改完地址必须重建采集计划，否则新地址要等重启才生效。
/// 这条链路是本次改造的关键一环，但真起轮询会带来定时器与线程，测试会变得又慢又不稳。
/// 用替身就能直接断言"保存地址后确实要求重建了"。</para>
/// </summary>
public sealed class 替身轮询引擎 : 轮询引擎接口
{
    private int _重建次数;

    /// <summary>被要求重建采集计划的次数。</summary>
    public int 重建数量 => _重建次数;

    /// <inheritdoc />
    public IReadOnlyDictionary<标签分组, double> 周期毫秒分组 { get; } =
        new Dictionary<标签分组, double>();

    /// <inheritdoc />
    public Task 异步启动(CancellationToken 取消令牌 = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task 异步停止(CancellationToken 取消令牌 = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task 异步重建方案列表(CancellationToken 取消令牌 = default)
    {
        Interlocked.Increment(ref _重建次数);
        return Task.CompletedTask;
    }
}
