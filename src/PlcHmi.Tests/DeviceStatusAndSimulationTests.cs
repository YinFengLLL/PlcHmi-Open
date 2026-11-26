using Microsoft.Extensions.Logging.Abstractions;
using PlcHmi.Business.Services;
using PlcHmi.Communication.Simulation;
using PlcHmi.Core.Enums;

namespace PlcHmi.Tests;

/// <summary>
/// 设备状态字映射与模拟器软元件布局的回归测试。
///
/// <para>这两处曾经出过真实缺陷：状态字编号与 C# 枚举语义不一致导致“待机”显示成“未知”；
/// 模拟器把 X 点写进离散输入区，而轮询引擎按线圈区读取，导致常闭点状态错乱。</para>
/// </summary>
public class DeviceStateSimulationTest
{
    private const int xInputBaseAddress = 63488;   // X0（离散输入区 FC02）
    private const int registerD6RunState = 6;  // D6 设备状态字

    [Theory]
    [InlineData(0, DeviceState.Idle)]
    [InlineData(1, DeviceState.Running)]
    [InlineData(2, DeviceState.Standby)]
    [InlineData(3, DeviceState.Resetting)]
    [InlineData(4, DeviceState.Alarming)]
    [InlineData(5, DeviceState.Unknown)]
    [InlineData(99, DeviceState.Unknown)]
    public void StatusWordMap_ShouldByPlcAgreedFlipTranslateAsDeviceState(int SourceValue, DeviceState expected) =>
        Assert.Equal(expected, DeviceService.MapStatusWord(SourceValue));

    /// <summary>
    /// 主监控页 5 态显示文案回归（2026-10-16 现场要求：空闲中/运行中/待机中/复位中/报警中）。
    /// 界面文案是操作员要核对的行为，改错了必须在这里红。
    /// </summary>
    [Theory]
    [InlineData(DeviceState.Idle, "空闲中")]
    [InlineData(DeviceState.Running, "运行中")]
    [InlineData(DeviceState.Standby, "待机中")]
    [InlineData(DeviceState.Resetting, "复位中")]
    [InlineData(DeviceState.Alarming, "报警中")]
    [InlineData(DeviceState.Unknown, "未知")]
    public void StateShow_FiveKindStateShouldHasExplicitConfirmCaptionAndNotMutuallyDuplicate(DeviceState StateValue, string expectedText)
    {
        var (Text, ColorKey) = DeviceService.StateShow(StateValue);

        Assert.Equal(expectedText, Text);
        Assert.False(string.IsNullOrWhiteSpace(ColorKey));
    }

    [Fact]
    public void StateShow_FiveKindValidStateCaptionMustPairwiseDifferent()
    {
        // 图标转换器按文本选图标：两个状态共用一句文案，其中一种就会显示错误的图标
        var captionCollection = new[]
        {
            DeviceState.Idle, DeviceState.Running, DeviceState.Standby, DeviceState.Resetting, DeviceState.Alarming,
        }.Select(stateItem => DeviceService.StateShow(stateItem).Text).ToList();

        Assert.Equal(captionCollection.Count, captionCollection.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void StatusWordMap_ZeroValueShouldIsEmptyIdleAndNonUnknown()
    {
        // PLC 状态字 0 表示空闲中；枚举里的 未知=0 只是“未拿到有效数据”的哨兵，
        // 二者语义不同，必须解耦——这条断言就是防止有人再改回直接强转。
        Assert.Equal(DeviceState.Idle, DeviceService.MapStatusWord(0));
        Assert.NotEqual(DeviceState.Unknown, DeviceService.MapStatusWord(0));
    }

    [Fact]
    public async Task Simulator_InitialStatusWordShouldIsEmptyIdle()
    {
        await using var Device = new VirtualPlcDevice(NullLogger<VirtualPlcDevice>.Instance);

        Assert.Equal(0, Device.memory.UnsafeGetHoldingRegister(registerD6RunState));
        Assert.Equal(DeviceState.Idle, DeviceService.MapStatusWord(
            Device.memory.UnsafeGetHoldingRegister(registerD6RunState)));
    }

    [Fact]
    public async Task Simulator_XPointMustWriteAtDiscreteInputArea()
    {
        await using var Device = new VirtualPlcDevice(NullLogger<VirtualPlcDevice>.Instance);
        var memory = Device.memory;

        // X 是物理输入 → 离散输入区（FC02）；Y 是输出 → 线圈区（FC01）。
        // 混区会让模拟器自测通过、真机全错（2026-10-17 现场第二次报障的真因）。
        var block = memory.ReadDiscreteInput(xInputBaseAddress, 24);

        Assert.True(block[2], "X2 安全门为常闭触点，初始应闭合（原始位 1）");
        Assert.True(block[7], "X7 急停按钮未按下时应闭合（原始位 1）");

        // 线圈区同一地址段必须保持全 0：证明 X 没有同时写进线圈区（功能码分工是唯一的）
        var coilBlock = memory.ReadCoil(xInputBaseAddress, 24);
        Assert.DoesNotContain(true, coilBlock);
    }

    [Fact]
    public async Task Simulator_NormalCloseEmergencyStopPointShouldAndEmergencyStopStateInvert()
    {
        await using var Device = new VirtualPlcDevice(NullLogger<VirtualPlcDevice>.Instance);
        Device.Start();

        try
        {
            // 等模拟循环跑起来，让 Tick 写入一遍
            await Task.Delay(400);

            // 未急停：常闭触点闭合 → 原始位 1；经 io-points.json 的 inverted=true 后，
            // 逻辑值为 0（界面显示 OFF），表示“急停未触发”
            Assert.True(Device.memory.UnsafeGetDiscreteInput(xInputBaseAddress + 7));

            Device.memory.RunAloneOccupy(() => Device.memory.UnsafeSetCoil(204, true)); // M204 急停信号（电平保持）
            await Task.Delay(400);

            // 急停后：触点断开 → 原始位 0 → 逻辑值 1（界面显示 ON）
            Assert.False(Device.memory.UnsafeGetDiscreteInput(xInputBaseAddress + 7));
            Assert.Equal(4, Device.memory.UnsafeGetHoldingRegister(registerD6RunState));
        }
        finally
        {
            await Device.StopAsync();
        }
    }
}
