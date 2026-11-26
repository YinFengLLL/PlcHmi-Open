using Microsoft.Extensions.Logging.Abstractions;
using PlcHmi.Business.Services;
using PlcHmi.Communication.Inovance;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;
using PlcHmi.Tests.Fake;

namespace PlcHmi.Tests;

/// <summary>
/// 主页面左下工位状态条（锁附 / 点胶 / 加工完成 + 已锁附颗数）的数据汇聚回归测试。
///
/// <para><b>2026-10-16 现场要求：</b>三盏灯 <c>0</c> = 未完成、<c>1</c> = 完成；
/// 颗数读一个 <b>INT</b> 型地址，地址均由管理员在运行页「编辑地址」里改
/// （元件表与校验见 <see cref="RunMonitorAddressTest"/>）。</para>
///
/// <para>这里钉住 <see cref="DeviceService"/> 的汇聚行为：事件只在值变化时推、
/// 无效值保持上次显示（不能闪成"未完成"）、脏值按"非 0 即完成"归一。</para>
/// </summary>
public class StationStateItemTest
{
    private static (DeviceService Device, TagValueService ValueList) NewService(FakeModbusClient Client)
    {
        var Parser = new InovanceAddressParser(PlcSeries.SmallPlc);
        var ValueList = new TagValueService(NullLogger<TagValueService>.Instance);

        var Writer = new TagWriter(
            Client, FakeTagRepository.RunPageDefaultPoint(), Parser, NullLogger<TagWriter>.Instance);

        return (new DeviceService(ValueList, Writer, new AllowAllPermissionGuard(),
            NullLogger<DeviceService>.Instance), ValueList);
    }

    /// <summary>三个完成标志是 M 区<b>位</b>：点位值是 bool（true = 完成）。</summary>
    private static TagNewValue MarkLog(string nameValue, bool Value) =>
        new(nameValue, Value, Value ? 1 : 0, DateTime.Now, true);

    // ── 显示文案 ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, "完成")]
    [InlineData(false, "未完成")]
    public void CompleteStateShow_CaptionShouldAndFieldPortRadiusConsistent(bool complete, string expectedText)
    {
        var (Text, ColorKey) = DeviceService.CompleteStateShow(complete);

        Assert.Equal(expectedText, Text);
        Assert.False(string.IsNullOrWhiteSpace(ColorKey));
    }

    [Fact]
    public void CompleteStateShow_IncompleteMustNotUseAlarmColor()
    {
        // 未完成是正常的中间态：用红色/橙色会把机器待机时的状态条染成一片黄，
        // 反而让真正的报警失色。抖动颜色必须先红在"未完成变红"这条上。
        Assert.Equal("SuccessBrush", DeviceService.CompleteStateShow(true).ColorKey);
        Assert.Equal("MutedBrush", DeviceService.CompleteStateShow(false).ColorKey);
    }

    // ── 汇聚行为 ────────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessState_InitialShouldAsIncompleteAndPieceCountAsZero()
    {
        await using var Client = new FakeModbusClient();
        var (Device, _) = NewService(Client);

        Assert.False(Device.ScrewComplete);
        Assert.False(Device.DispenseComplete);
        Assert.False(Device.ProcessComplete);
        Assert.Equal(0, Device.ScrewedPieceCount);
    }

    [Fact]
    public async Task ProcessState_PointSetOneShouldReportCompleteAndOnlyAtChangeWhenPushEvent()
    {
        await using var Client = new FakeModbusClient();
        var (Device, ValueList) = NewService(Client);

        var screwCount = 0;
        var dispenseCount = 0;
        var processCount = 0;
        Device.screwStateChanged += (_, _) => screwCount++;
        Device.dispenseStateChanged += (_, _) => dispenseCount++;
        Device.processStateChanged += (_, _) => processCount++;

        ValueList.RaiseLayout(MarkLog("ScrewDone", true));
        ValueList.RaiseLayout(MarkLog("GlueDone", true));
        ValueList.RaiseLayout(MarkLog("ProcessDone", true));

        Assert.True(Device.ScrewComplete);
        Assert.True(Device.DispenseComplete);
        Assert.True(Device.ProcessComplete);
        Assert.Equal((1, 1, 1), (screwCount, dispenseCount, processCount));

        // 值没变：不得再推事件（界面每周期刷新会闪；与 标签值服务 的变化检测同口径）
        ValueList.RaiseLayout(MarkLog("ScrewDone", true));
        Assert.Equal(1, screwCount);

        // 回到未完成：推事件，界面跟着变回灰色"未完成"
        ValueList.RaiseLayout(MarkLog("ScrewDone", false));
        Assert.False(Device.ScrewComplete);
        Assert.Equal(2, screwCount);
    }

    [Fact]
    public async Task ProcessState_PointIfBeDefinitionAsNumericValueAmountAlsoShouldBeCorrectNormalize()
    {
        await using var Client = new FakeModbusClient();
        var (Device, ValueList) = NewService(Client);

        // 出厂定义是 M 区位（bool），但现场也可能把该点位改定义成 D 区数值量：
        // 一律按"非 0 即完成"归一，不能把"完成度 2"透传给界面
        ValueList.RaiseLayout(new TagNewValue("ScrewDone", (ushort)2, 2, DateTime.Now, true));
        Assert.True(Device.ScrewComplete);

        ValueList.RaiseLayout(new TagNewValue("GlueDone", (ushort)0, 0, DateTime.Now, true));
        Assert.False(Device.DispenseComplete);
    }

    [Fact]
    public async Task ProcessState_InvalidValueShouldKeepLastShowInsteadOfFlickerAsIncomplete()
    {
        await using var Client = new FakeModbusClient();
        var (Device, ValueList) = NewService(Client);

        ValueList.RaiseLayout(MarkLog("ProcessDone", true));
        Assert.True(Device.ProcessComplete);

        // 断线 / 解析失败：保持"完成"，等下一轮有效值；不能因为读不到就显示"未完成"误导操作员
        ValueList.RaiseLayout(TagNewValue.Invalid("ProcessDone"));
        Assert.True(Device.ProcessComplete);
    }

    [Fact]
    public async Task ScrewedPieceCount_ShouldByPointValueUpdateAndSupportNegativeNumberPerRealShow()
    {
        await using var Client = new FakeModbusClient();
        var (Device, ValueList) = NewService(Client);

        var EventTimes = 0;
        long mostRecentValue = -1;
        Device.screwedPieceCountChanged += (_, pieceCount) =>
        {
            EventTimes++;
            mostRecentValue = pieceCount;
        };

        ValueList.RaiseLayout(new TagNewValue("ScrewCount", (short)1234, 1234, DateTime.Now, true));
        Assert.Equal(1234, Device.ScrewedPieceCount);
        Assert.Equal(1234, mostRecentValue);

        // Int16 有符号：PLC 侧回绕成负数时照实显示（显示成 0 会掩盖现场问题）
        ValueList.RaiseLayout(new TagNewValue("ScrewCount", (short)-3, -3, DateTime.Now, true));
        Assert.Equal(-3, Device.ScrewedPieceCount);

        Assert.Equal(2, EventTimes);
    }
}
