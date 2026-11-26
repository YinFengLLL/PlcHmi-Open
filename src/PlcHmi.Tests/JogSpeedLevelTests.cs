using Microsoft.Extensions.Logging.Abstractions;
using PlcHmi.Business.Configuration;
using PlcHmi.Business.Services;
using PlcHmi.Communication.Inovance;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;
using PlcHmi.Tests.Fake;

namespace PlcHmi.Tests;

/// <summary>
/// 点动速度档位写入测试。
///
/// <para><b>约定（2026-09-12 需求方确认）</b>：每轴一个 D 区寄存器，写 1 低速 / 2 中速 / 3 高速，
/// 由 PLC 侧据此选择点动速度；地址可改、随选中轴切换。</para>
///
/// <para><b>为什么不需要手动使能</b>：档位是"参数设定"而不是动作机构——
/// 与参数页的延时 / 按钮表一致；真正的动作仍受使能联锁保护。</para>
/// </summary>
public class JogSpeedLevelTest
{
    private static ManualWidgetService NewService(RecipeConfig configPairObject, FakeModbusClient Client)
    {
        var Parser = new InovanceAddressParser(PlcSeries.SmallPlc);

        return new ManualWidgetService(
            new TagWriter(
                Client,
                new TagRepository(NullLogger<TagRepository>.Instance),
                Parser,
                NullLogger<TagWriter>.Instance),
            Client,
            Parser,
            configPairObject,
            new AllowAllPermissionGuard(),   // 本组用例不覆盖权限分支，显式声明
            NullLogger<ManualWidgetService>.Instance);
    }

    [Theory]
    // 2026-10-13 现场要求：六轴共用同一个速度档位地址（切档一次，六轴同时生效）；
    // 2026-10-21 出厂配置换为现场版本，档位地址是 D15 —— 本用例逐一钉死"每轴都写同一个寄存器"。
    [InlineData("X", 15)]
    [InlineData("Y", 15)]
    [InlineData("Z", 15)]
    [InlineData("ZZ", 15)]
    [InlineData("GlueX", 15)]
    [InlineData("GlueZ", 15)]
    public async Task GearShouldWriteTheAxisPairShouldRegister(string axisNumber, int expectedAddress)
    {
        await using var Client = new FakeModbusClient();
        var serviceInstance = NewService(RecipeConfigLoader.Load(out _), Client);

        await serviceInstance.SetJogSpeedLevelAsync(axisNumber, 2);

        var Write = Assert.Single(Client.RegisterWrite);

        // D 区基址为 0，因此 Modbus 地址 == D 序号
        Assert.Equal(expectedAddress, Write.Address);
        Assert.Equal((ushort)2, Write.Value);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(-1)]
    public async Task BeyondBoundaryGearValueShouldBeRejectedAndNotWritePlc(int Level)
    {
        await using var Client = new FakeModbusClient();
        var serviceInstance = NewService(RecipeConfigLoader.Load(out _), Client);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => serviceInstance.SetJogSpeedLevelAsync("X", Level));

        Assert.Empty(Client.RegisterWrite);
    }

    [Fact]
    public async Task NotConfigGearAddressAxisShouldBeRejected()
    {
        await using var Client = new FakeModbusClient();
        var configPairObject = RecipeConfigLoader.Load(out _);
        configPairObject.JogSpeedLevel.Clear();

        var serviceInstance = NewService(configPairObject, Client);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => serviceInstance.SetJogSpeedLevelAsync("X", JogSpeedLevelAddress.MinLevel));
    }

    [Fact]
    public async Task UnsupportedAxisShouldBeRejected()
    {
        await using var Client = new FakeModbusClient();
        var serviceInstance = NewService(RecipeConfigLoader.Load(out _), Client);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => serviceInstance.SetJogSpeedLevelAsync("Q", 1));
    }
}
