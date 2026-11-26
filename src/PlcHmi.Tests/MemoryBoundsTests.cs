using Microsoft.Extensions.Logging.Abstractions;
using PlcHmi.Business.Services;
using PlcHmi.Communication.Inovance;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;
using PlcHmi.Tests.Fake;

namespace PlcHmi.Tests;

/// <summary>
/// 长时运行的内存有界性断言。
///
/// <para><b>为什么补这一组：</b>上位机在现场是"开机不关"的，任何只增不减的集合
/// 最终都会把内存吃干。报警历史与活动列表是最容易失控的两处，
/// 用断言把它们的有界性固定下来，防止后续改动无意中拆掉裁剪逻辑。</para>
/// </summary>
public class MemoryBoundaryTest
{
    private static AlarmService NewAlarmService(FakeModbusClient Client) =>
        new(
            new TagWriter(
                Client,
                new TagRepository(NullLogger<TagRepository>.Instance),
                new InovanceAddressParser(PlcSeries.SmallPlc),
                NullLogger<TagWriter>.Instance),
            NullLogger<AlarmService>.Instance);

    [Fact]
    public async Task AlarmHistory_ExceedBeyondUpperLimitShouldTrimToOneThousandItem()
    {
        await using var Client = new FakeModbusClient();
        var alarmList = NewAlarmService(Client);

        for (var Entry = 0; Entry < 1200; Entry++)
        {
            var CodeRawValue = $"T{Entry:0000}";
            alarmList.Trigger(CodeRawValue, "测试报警", AlarmLevel.Warning);
            alarmList.ClearEmpty(CodeRawValue);   // 立即消除，避免活动列表无界增长掩盖"历史裁剪"这一被测点
        }

        Assert.Empty(alarmList.ActiveAlarm);
        Assert.Equal(1000, alarmList.History.Count);
    }

    [Fact]
    public async Task ActiveAlarm_SameNumberShouldNotDuplicateInColumn()
    {
        await using var Client = new FakeModbusClient();
        var alarmList = NewAlarmService(Client);

        alarmList.Trigger("A001", "第一次", AlarmLevel.Warning);
        alarmList.Trigger("A001", "第二次", AlarmLevel.Critical);

        // 重复触发不等于新增：活动列表与历史各只应有一条
        Assert.Single(alarmList.ActiveAlarm);
        Assert.Single(alarmList.History);
    }

    [Fact]
    public void PointValueNotChangeWhenShouldNotPushEvent()
    {
        var ValueList = new TagValueService(NullLogger<TagValueService>.Instance);
        var Triggered = 0;
        ValueList.NewValueChanged += (_, _) => Triggered++;

        var currentTimestamp = DateTime.Now;

        ValueList.RaiseLayout(new TagNewValue("Speed", 1.0, 1.0, currentTimestamp, true));
        Assert.Equal(1, Triggered);

        // 同为 1.0：不应再推送（否则 UI 会被无变化的刷新淹没）
        ValueList.RaiseLayout(new TagNewValue("Speed", 1.0, 1.0, currentTimestamp.AddSeconds(1), true));
        Assert.Equal(1, Triggered);

        ValueList.RaiseLayout(new TagNewValue("Speed", 2.0, 2.0, currentTimestamp.AddSeconds(2), true));
        Assert.Equal(2, Triggered);
    }

    [Fact]
    public void PointValue_MarkFailEffectAfterShouldGetNotToValue()
    {
        var ValueList = new TagValueService(NullLogger<TagValueService>.Instance);
        ValueList.RaiseLayout(new TagNewValue("Speed", 1.0, 1.0, DateTime.Now, true));

        Assert.True(ValueList.TryGetDouble("Speed", out var Prior));
        Assert.Equal(1.0, Prior);
        Assert.True(ValueList.GetNewValue("Speed").IsValid);

        ValueList.FailEffectAll();

        Assert.False(ValueList.GetNewValue("Speed").IsValid);
        Assert.False(ValueList.TryGetDouble("Speed", out _));
        Assert.False(ValueList.TryGetBoolean("Speed", out _));
    }
}
