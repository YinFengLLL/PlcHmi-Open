using Microsoft.Extensions.Logging.Abstractions;
using PlcHmi.Business.Services;

namespace PlcHmi.Tests;

/// <summary>
/// 产量统计服务的统计口径回归测试。
///
/// <para>覆盖：整点采样与小时产量、计数器回退（数据清除）、缺口小时补 0、
/// 近 7 天日汇总与日期标签、时钟回拨忽略。</para>
/// </summary>
public class OutputStatisticsTest
{
    /// <summary>可控的产量计数器与时钟。</summary>
    private sealed class TestEnvironment
    {
        public long output;
        public DateTime Timestamp;

        public readonly OutputStatisticsService Service;

        public TestEnvironment(DateTime initialTimestamp)
        {
            Timestamp = initialTimestamp;
            Service = new OutputStatisticsService(
                NullLogger<OutputStatisticsService>.Instance,
                null,
                () => output,
                () => Timestamp);
        }
    }

    private static readonly DateTime Today = new(2026, 9, 24, 10, 0, 0);   // 周四

    [Fact]
    public void HourSamplingAfter_HourlyOutputShouldAsMutualAdjacentSnapshotDelta_CurrentHourGetRealtimeValue()
    {
        var environment = new TestEnvironment(Today);
        var Service = environment.Service;

        // 10:00 打点（累计 100）→ 11:00 打点（累计 160）→ 11:30 实时 200
        environment.output = 100;
        Assert.True(Service.CheckSampling(Today));

        environment.output = 160;
        environment.Timestamp = Today.AddHours(1);
        Assert.True(Service.CheckSampling(environment.Timestamp));

        environment.output = 200;
        environment.Timestamp = Today.AddMinutes(90);
        Assert.False(Service.CheckSampling(environment.Timestamp));   // 同一小时不打点

        var indexColumn = Service.TodayHourlyOutput;

        Assert.Equal(0, indexColumn[9]);
        Assert.Equal(60, indexColumn[10]);   // 160 − 100
        Assert.Equal(40, indexColumn[11]);   // 实时：200 − 160
    }

    [Fact]
    public void CounterFallback_DataClearRemoveAfter_HourlyOutputShouldGetClearZeroAfterCounterAndNonNegativeNumber()
    {
        var environment = new TestEnvironment(Today);
        var Service = environment.Service;

        environment.output = 500;
        Service.CheckSampling(Today);

        // 数据清除：计数器从 500 清回 20
        environment.output = 20;
        environment.Timestamp = Today.AddHours(1);
        Service.CheckSampling(environment.Timestamp);

        var indexColumn = Service.TodayHourlyOutput;

        Assert.Equal(20, indexColumn[10]);   // 20 − 500 < 0 → 取下一快照值 20，绝不出负数
        Assert.Equal(0, indexColumn[11]);    // 当前小时实时：20 − 20
    }

    [Fact]
    public void MissingPortHour_ProcessIndexNotRuntimeBetweenOutputRecordZero_RestartAfterNoOneSectionCountInDelta()
    {
        var environment = new TestEnvironment(Today);
        var Service = environment.Service;

        environment.output = 100;
        Service.CheckSampling(Today);

        // 程序停了 3 小时（11:00–13:00 无读数），14:00 重启累计 400
        environment.output = 400;
        environment.Timestamp = Today.AddHours(4);
        Service.CheckSampling(environment.Timestamp);

        var indexColumn = Service.TodayHourlyOutput;

        Assert.Equal(0, indexColumn[10]);
        Assert.Equal(0, indexColumn[11]);    // 缺口小时记 0
        Assert.Equal(0, indexColumn[12]);
        Assert.Equal(300, indexColumn[13]);  // 400 − 100 归入重启后第一段
        Assert.Equal(0, indexColumn[14]);    // 当前小时实时
    }

    [Fact]
    public void LastSevenDaysDailySummary_ShouldPerItemDayAccumulatedHourlyOutput_TodayHasRealtimeValue()
    {
        var environment = new TestEnvironment(Today);
        var Service = environment.Service;

        // 昨天（09-23）：8 点 100 → 9 点 180（此后停机，计数器不动）
        environment.output = 100;
        environment.Timestamp = Today.AddDays(-1).AddHours(8);
        Service.CheckSampling(environment.Timestamp);

        environment.output = 180;
        environment.Timestamp = Today.AddDays(-1).AddHours(9);
        Service.CheckSampling(environment.Timestamp);

        // 今天（09-24）：10 点仍是 180（停机中）→ 11 点 260（复工生产 80 件）
        environment.output = 180;
        environment.Timestamp = Today;
        Service.CheckSampling(environment.Timestamp);

        environment.output = 260;
        environment.Timestamp = Today.AddHours(1);
        Service.CheckSampling(environment.Timestamp);

        var weeklyOutput = Service.WeekPerDayOutput;
        var Tag = Service.WeekDateTag;

        Assert.Equal(7, weeklyOutput.Count);
        Assert.Equal(7, Tag.Count);

        // 6 天前到 3 天前都没有样本 → 0
        Assert.Equal(0, weeklyOutput[0]);
        Assert.Equal(0, weeklyOutput[3]);

        // 昨天：8 点段 80（100→180）；9 点段 = 下一快照 180 − 180 = 0
        Assert.Equal(80, weeklyOutput[5]);
        Assert.StartsWith("09-23", Tag[5]);
        Assert.Contains("周三", Tag[5]);

        // 今天：9 点段（停机缺口最后一段）= 180 − 180 = 0；
        // 10 点段 = 260 − 180 = 80；11 点实时 = 260 − 260 = 0
        Assert.Equal(80, weeklyOutput[6]);
        Assert.StartsWith("09-24", Tag[6]);
        Assert.Contains("周四", Tag[6]);
    }

    [Fact]
    public void ClockBackTilt_ShouldNotProduceSamplingPoint()
    {
        var environment = new TestEnvironment(Today);
        var Service = environment.Service;

        environment.output = 100;
        Assert.True(Service.CheckSampling(Today));

        environment.Timestamp = Today.AddHours(1);
        Assert.True(Service.CheckSampling(environment.Timestamp));

        // 回拨到 10 点：拒绝打点，样本不增不减
        Assert.False(Service.CheckSampling(Today));

        var indexColumn = Service.TodayHourlyOutput;

        // 10 点段 = 11 点快照 − 10 点快照 = 100 − 100 = 0（计数器没动）
        Assert.Equal(0, indexColumn[10]);
        Assert.Equal(0, indexColumn[11]);   // 当前小时实时：100 − 100
    }
}
