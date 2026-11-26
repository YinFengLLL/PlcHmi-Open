using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;

namespace PlcHmi.Tests.Fake;

/// <summary>
/// 采集引擎的测试替身：只记录"被要求重建了几次"，不真的起循环。
///
/// <para><b>为什么需要它</b>：改完地址必须重建采集计划，否则新地址要等重启才生效。
/// 这条链路是本次改造的关键一环，但真起轮询会带来定时器与线程，测试会变得又慢又不稳。
/// 用替身就能直接断言"保存地址后确实要求重建了"。</para>
/// </summary>
public sealed class FakePollingEngine : IPollingEngine
{
    private int _rebuildCount;

    /// <summary>被要求重建采集计划的次数。</summary>
    public int RebuildCount => _rebuildCount;

    /// <inheritdoc />
    public IReadOnlyDictionary<TagGroup, double> WeekPeriodMillisecondsGroup { get; } =
        new Dictionary<TagGroup, double>();

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task RebuildPlanListAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _rebuildCount);
        return Task.CompletedTask;
    }
}
