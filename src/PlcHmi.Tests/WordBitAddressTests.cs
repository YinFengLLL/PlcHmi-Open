using Microsoft.Extensions.Logging.Abstractions;
using PlcHmi.Business.Services;
using PlcHmi.Communication.Simulation;
using PlcHmi.Communication.Inovance;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Tests;

/// <summary>
/// D/R 区「字内位」寻址测试（2026-09-17 按 PLC 地址分配表引入）。
///
/// <para><b>背景</b>：汇川位打包约定 BOOL 占 D 字的一位（可连续），
/// 如 X 轴命令字 D1210 的位 2/3 分别是正向/反向点动。Modbus 没有写字内位的功能码，
/// 因此写通道是「读整字 → 改位 → 写回」，读通道是「读整字 → 按位取值」。
/// 本组守住两件事：<b>改位不伤同字其它位</b>、<b>读回的位与实际位一致</b>。</para>
/// <para>读的是测试输出目录里的 <c>config/tags.json</c>，与写配置文件的用例（轴地址服务测试）
/// 归入同一集合，避免并行时读到半途的表。</para>
/// </summary>
[Collection("config-files")]
public class WordBitAddressingTest
{
    private static InovanceAddressParser Parser() => new(PlcSeries.SmallPlc);

    private static TagRepository Repository() => new(NullLogger<TagRepository>.Instance);

    // ── 解析 ────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_DAreaWordBit_ShouldGiveWordAddressAndBitNumber()
    {
        var addressValue = Parser().Parse("D1210.2");

        Assert.Equal(SoftElement.D, addressValue.ElementItem);
        Assert.Equal(1210, addressValue.LogicIndex);
        Assert.Equal(1210, addressValue.ModbusAddress);
        Assert.Equal(ModbusArea.HoldingRegister, addressValue.Area);
        Assert.Equal(2, addressValue.BitIndex);
        Assert.True(addressValue.IsIntraWordBit);
    }

    [Fact]
    public void Parse_RAreaWordBit_ShouldSameSampleSupportAndSizeWriteNotSensitiveSense()
    {
        var addressValue = Parser().Parse("r50.15");

        Assert.Equal(SoftElement.R, addressValue.ElementItem);
        Assert.Equal(15, addressValue.BitIndex);
        Assert.True(addressValue.IsIntraWordBit);
    }

    [Theory]
    [InlineData("D1210.16")]   // 位号只允许 0–15
    [InlineData("D1210.100")]
    [InlineData("D1210.")]
    [InlineData("M230.1")]     // 位元件不支持字内位写法
    public void Parse_IllegalWordBitWriteMethod_ShouldFail(string AddressText)
    {
        Assert.False(Parser().TryParse(AddressText, out _));
    }

    [Fact]
    public void Parse_WholeWordAddressShouldNotBeJudgeAsIntraWordBit()
    {
        var addressValue = Parser().Parse("D1210");

        Assert.False(addressValue.IsIntraWordBit);
        Assert.Equal(-1, addressValue.BitIndex);
    }

    // ── X / Y 的功能码分工（2026-10-17 现场第二次报障后修正）────────────

    /// <summary>
    /// X 是物理输入 → 离散输入区（FC02）；Y 是输出 → 线圈区（FC01）。
    /// <para><b>为什么单独立一个用例：</b>2026-10-17 之前 X 被配在线圈区，等于用 FC01 读输入区，
    /// 现场表现是"Y 一直正确、X（X0–X7 / X10–X17）一直对不上"，且两轮修复都没命中真因。
    /// 这条断言把"输入走 FC02、输出走 FC01"的分工钉死，改规格表前先看它红不红。</para>
    /// </summary>
    [Theory]
    [InlineData("X0", 63488)]
    [InlineData("X10", 63496)]    // 八进制：X10 = 序号 8
    [InlineData("X17", 63503)]    // 八进制：X17 = 序号 15
    public void Parse_XArea_ShouldByPhysicalInputAtToDiscreteInputArea(string AddressText, int expectedAddress)
    {
        var addressValue = Parser().Parse(AddressText);

        Assert.Equal(SoftElement.X, addressValue.ElementItem);
        Assert.Equal(expectedAddress, addressValue.ModbusAddress);
        Assert.Equal(ModbusArea.DiscreteInput, addressValue.Area);
    }

    [Theory]
    [InlineData("Y0", 64512)]
    [InlineData("Y10", 64520)]    // 八进制：Y10 = 序号 8
    public void Parse_YArea_ShouldByOutputAtToCoilArea(string AddressText, int expectedAddress)
    {
        var addressValue = Parser().Parse(AddressText);

        Assert.Equal(SoftElement.Y, addressValue.ElementItem);
        Assert.Equal(expectedAddress, addressValue.ModbusAddress);
        Assert.Equal(ModbusArea.Coil, addressValue.Area);
    }

    // ── 写通道：读-改-写 ────────────────────────────────────────────────

    [Fact]
    public async Task WriteDBit_ShouldOnlyChangeTargetBitAndRetainSameWordOtherBit()
    {
        await using var Device = new VirtualPlcDevice(NullLogger<VirtualPlcDevice>.Instance);
        await using var Client = new SimulationModbusClient(Device, NullLogger<SimulationModbusClient>.Instance);
        await Client.ConnectAsync();

        var Writer = new TagWriter(Client, Repository(), Parser(), NullLogger<TagWriter>.Instance);

        // 命令字 D1210 预置：位 0（轴使能）与位 2（正向点动）为 1
        Device.memory.UnsafeSetHoldingRegister(1210, 0b0000_0000_0000_0101);

        // 写 JogXForward（D1210.2）为 false：只清位 2，位 0 必须原样保留
        await Writer.WriteBooleanAsync("JogXForward", false);

        Assert.Equal(
            (ushort)0b0000_0000_0000_0001,
            Device.memory.UnsafeGetHoldingRegister(1210));

        // 再写回 true：位 0 仍保留
        await Writer.WriteBooleanAsync("JogXForward", true);

        Assert.Equal(
            (ushort)0b0000_0000_0000_0101,
            Device.memory.UnsafeGetHoldingRegister(1210));
    }

    [Fact]
    public async Task WriteDBit_SameWordTwoBitNotMutuallyInterfere()
    {
        await using var Device = new VirtualPlcDevice(NullLogger<VirtualPlcDevice>.Instance);
        await using var Client = new SimulationModbusClient(Device, NullLogger<SimulationModbusClient>.Instance);
        await Client.ConnectAsync();

        var Writer = new TagWriter(Client, Repository(), Parser(), NullLogger<TagWriter>.Instance);

        // 正转 D1210.2 → 反转 D1210.3（正反转互锁：置一转必须清另一）
        await Writer.WriteBooleanAsync("JogXForward", true);
        await Writer.WriteBooleanAsync("JogXReverse", false);

        Assert.Equal(
            (ushort)0b0000_0000_0000_0100,
            Device.memory.UnsafeGetHoldingRegister(1210));

        await Writer.WriteBooleanAsync("JogXForward", false);
        await Writer.WriteBooleanAsync("JogXReverse", true);

        Assert.Equal(
            (ushort)0b0000_0000_0000_1000,
            Device.memory.UnsafeGetHoldingRegister(1210));
    }

    // ── 读通道：轮询按位解码 ────────────────────────────────────────────

    [Fact]
    public async Task Polling_DBitPointShouldByBitDecodeSubscribeValue()
    {
        await using var Device = new VirtualPlcDevice(NullLogger<VirtualPlcDevice>.Instance);
        await using var Client = new SimulationModbusClient(Device, NullLogger<SimulationModbusClient>.Instance);

        var pointTable = Repository();
        var JogForward = pointTable.Find("JogXForward");
        Assert.NotNull(JogForward);

        // 点动点是「命令」组（只写不采集），这里改到快组以便轮询引擎把它编进采集计划
        JogForward!.Group = TagGroup.Fast;

        var ValueList = new TagValueService(NullLogger<TagValueService>.Instance);

        await using var engine = new PollingEngine(
            Client,
            pointTable,
            new IoPointService(new AllowAllPermissionGuard(), NullLogger<IoPointService>.Instance),
            Parser(),
            ValueList,
            new PollingOptions { FastIntervalMilliseconds = 50, SlowIntervalMilliseconds = 100, EventIntervalMilliseconds = 100 },
            new PlcConnectionOptions(),
            NullLogger<PollingEngine>.Instance);

        await Client.ConnectAsync();
        await engine.StartAsync();

        // 位 2 置 1 → 采集应发布 true（同字其它位为 0，不能误判成 true）
        Device.memory.UnsafeSetHoldingRegister(1210, 0b0000_0000_0000_0000);
        await WaitUntilAsync(() => ValueList.TryGetBoolean("JogXForward", out var Value) && !Value, "位为 0 时发布 false");

        Device.memory.UnsafeSetHoldingRegister(1210, 0b0000_0000_0000_0100);
        await WaitUntilAsync(() => ValueList.TryGetBoolean("JogXForward", out var Value) && Value, "位 2 置 1 时发布 true");

        // 位 0 为 1、位 2 为 0：不能因为整字非零就判 true
        Device.memory.UnsafeSetHoldingRegister(1210, 0b0000_0000_0000_0001);
        await WaitUntilAsync(() => ValueList.TryGetBoolean("JogXForward", out var Value) && !Value, "只有其它位为 1 时应发布 false");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string Description, int TimeoutMilliseconds = 8000)
    {
        var truncateStopTime = Environment.TickCount64 + TimeoutMilliseconds;

        while (Environment.TickCount64 < truncateStopTime)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"等待超时：{Description}");
    }
}
