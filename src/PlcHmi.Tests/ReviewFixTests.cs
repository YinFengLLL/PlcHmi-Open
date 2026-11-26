using Microsoft.Extensions.Logging.Abstractions;
using PlcHmi.Business.Configuration;
using PlcHmi.Business.Services;
using PlcHmi.Communication.Inovance;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;
using PlcHmi.Tests.Fake;

namespace PlcHmi.Tests;

/// <summary>
/// 2026-09-29 代码审查整改的回归测试。
///
/// <para>每个用例钉住一条审查发现，防止修复被后续改动"改回去"：
/// 脉冲取消复位（H1）、浮点变化检测容差（H6）、失效推送（M9）、
/// 克隆漏字段（H11）、延时默认上限（M24）。</para>
/// </summary>
public class ReviewReworkRegressionTest
{
    private static ManualWidgetService NewManualService(FakeModbusClient Client)
    {
        var Parser = new InovanceAddressParser(PlcSeries.SmallPlc);

        // 权限分支由 权限守卫测试 覆盖，这里只钉行为
        return new ManualWidgetService(
            new TagWriter(
                Client,
                new TagRepository(NullLogger<TagRepository>.Instance),
                Parser,
                NullLogger<TagWriter>.Instance),
            Client,
            Parser,
            RecipeConfigLoader.Load(out _),
            new AllowAllPermissionGuard(),
            NullLogger<ManualWidgetService>.Instance);
    }

    // ── H1：脉冲取消后必须复位（安全出口）────────────────────────────────

    [Fact]
    public async Task PulseBeCancelWhenCoilMustStillReset()
    {
        await using var Client = new FakeModbusClient();
        await Client.ConnectAsync();

        var serviceInstance = NewManualService(Client);
        serviceInstance.SetEnabled(true);

        using var cancelSource = new CancellationTokenSource();
        var pulseTask = serviceInstance.PulseCoilAsync("M220", TimeSpan.FromSeconds(5), cancelSource.Token);

        await Task.Delay(50);
        await cancelSource.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pulseTask);

        // 取消发生在等待期间：置位已下发，复位必须无条件补上，否则线圈停在动作位
        Assert.Equal(2, Client.CoilWrite.Count);
        Assert.True(Client.CoilWrite[0].Value);
        Assert.False(Client.CoilWrite[^1].Value);
    }

    [Fact]
    public async Task PulseNormalCompleteWhenSetAndResetEachOnce()
    {
        await using var Client = new FakeModbusClient();
        await Client.ConnectAsync();

        var serviceInstance = NewManualService(Client);
        serviceInstance.SetEnabled(true);

        await serviceInstance.PulseCoilAsync("M220", TimeSpan.FromMilliseconds(10));

        Assert.Equal(2, Client.CoilWrite.Count);
        Assert.True(Client.CoilWrite[0].Value);
        Assert.False(Client.CoilWrite[^1].Value);
    }

    // ── H6：浮点变化检测容差（1e-9）──────────────────────────────────────

    private static TagNewValue FloatValue(string nameValue, float SourceValue, double scaleAfter) =>
        new(nameValue, SourceValue, scaleAfter, DateTime.Now, true);

    [Fact]
    public void FloatLastBitJitterMoveAtToleranceInnerShouldNotDuplicatePush()
    {
        var serviceInstance = new TagValueService(NullLogger<TagValueService>.Instance);
        var EventCount = 0;
        serviceInstance.NewValueChanged += (_, _) => EventCount++;

        serviceInstance.RaiseLayout(FloatValue("AxisXPosition", 1.0000000001f, 1.0000000001));
        serviceInstance.RaiseLayout(FloatValue("AxisXPosition", 1.0000000003f, 1.0000000003));

        // 首次必发；第二次差值 2e-10 < 1e-9，视为未变化（修复前原始值位级不等 → 每次轮询都误报）
        Assert.Equal(1, EventCount);
    }

    [Fact]
    public void FloatRealChangeShouldStillPushAndReadableBack()
    {
        var serviceInstance = new TagValueService(NullLogger<TagValueService>.Instance);
        var EventCount = 0;
        serviceInstance.NewValueChanged += (_, _) => EventCount++;

        serviceInstance.RaiseLayout(FloatValue("AxisXPosition", 1.0f, 1.0));
        serviceInstance.RaiseLayout(FloatValue("AxisXPosition", 1.0001f, 1.0001));

        Assert.Equal(2, EventCount);
        Assert.True(serviceInstance.TryGetDouble("AxisXPosition", out var Value));
        Assert.Equal(1.0001, Value, 6);
    }

    [Fact]
    public void BooleanValueChangeShouldPushAndSameValueNoDuplicatePush()
    {
        var serviceInstance = new TagValueService(NullLogger<TagValueService>.Instance);
        var EventCount = 0;
        serviceInstance.NewValueChanged += (_, _) => EventCount++;

        serviceInstance.RaiseLayout(new TagNewValue("Cylinder1", false, 0, DateTime.Now, true));
        serviceInstance.RaiseLayout(new TagNewValue("Cylinder1", false, 0, DateTime.Now, true));
        serviceInstance.RaiseLayout(new TagNewValue("Cylinder1", true, 1, DateTime.Now, true));

        Assert.Equal(2, EventCount);
    }

    // ── M9：失效全部要推送置灰事件 ───────────────────────────────────────

    [Fact]
    public void FailEffectAllShouldPushInvalidEventProvideUiSetGray()
    {
        var serviceInstance = new TagValueService(NullLogger<TagValueService>.Instance);
        var EventCount = 0;
        var lastOnceValid = true;
        serviceInstance.NewValueChanged += (_, Value) =>
        {
            EventCount++;
            lastOnceValid = Value.IsValid;
        };

        serviceInstance.RaiseLayout(FloatValue("AxisXPosition", 1.0f, 1.0));
        serviceInstance.FailEffectAll();

        // 有效 → 无效 是一次真实的状态变化，界面必须收到通知（修复前静默改字典）
        Assert.Equal(2, EventCount);
        Assert.False(lastOnceValid);
        Assert.False(serviceInstance.GetNewValue("AxisXPosition").IsValid);
    }

    // ── H11：克隆不得漏字段 ──────────────────────────────────────────────

    [Fact]
    public void HistoryRecordConfigCloneShouldRetainReadAfterResetTriggerBit()
    {
        var Config = new HistoryRecordConfig
        {
            Title = "锁螺丝记录",
            ReadAfterResetTriggerBit = false,   // PLC 程序自管触发位
            RetainDays = 30,
        };

        var copy = Config.Clone();

        Assert.False(copy.ReadAfterResetTriggerBit);
        Assert.Equal(Config.Title, copy.Title);
        Assert.Equal(Config.RetainDays, copy.RetainDays);
    }

    // ── M24：延时条目默认上限 ────────────────────────────────────────────

    [Fact]
    public void DelayEntryDefaultUpperLimitShouldAsDintMillisecondsUpperLimit()
    {
        // 2147483.647 秒 × 1000 = 2147483647 = Int32 上限（2026-10-16 现场要求延时按 DINT 存）。
        // 上限比实际换算窄会让合法值被拒；比它宽则下发时才报错——默认值必须与换算口径一致。
        Assert.Equal(2147483.647, new ParameterEntryDefinition().Max);
        Assert.Equal(2, ParameterEntryDefinition.delayOccupyWordCount);
    }
}
