using Microsoft.Extensions.Logging.Abstractions;
using PlcHmi.Business.Configuration;
using PlcHmi.Business.Services;
using PlcHmi.Communication.Inovance;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;
using PlcHmi.Tests.Fake;

namespace PlcHmi.Tests;

/// <summary>
/// 点动停止的安全性测试。
///
/// <para><b>为什么单独一组</b>：2026-09-12 起点动改为"按住才动、松开即停"，界面上不再有
/// 独立的「停止」按钮。这意味着<b>停止路径成了唯一的安全出口</b>——
/// 一旦它因为"手动使能没开"之类的理由被拒绝，轴就会一直动下去。</para>
///
/// <para><b>2026-09-17 起</b>：点动方向按 PLC 地址分配表改为 D 区字内位
/// （X 轴 D1210.2 / D1210.3），写命令是"读字 → 改位 → 写回"，
/// 因此停止路径体现在<b>寄存器写</b>上（写完目标位为 0），不再是线圈写。</para>
/// </summary>
public class JogStopSafetyTest
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

    [Fact]
    public async Task DisabledWhenJogShouldBeRejected()
    {
        await using var Client = new FakeModbusClient();
        var serviceInstance = NewService(RecipeConfigLoader.Load(out _), Client);

        Assert.False(serviceInstance.IsEnabled);

        await Assert.ThrowsAsync<InvalidOperationException>(() => serviceInstance.JogAsync("X", 1));

        Assert.Empty(Client.CoilWrite);
        Assert.Empty(Client.RegisterWrite);
    }

    [Fact]
    public async Task DisabledWhenStopMustStillGenerateEffect()
    {
        await using var Client = new FakeModbusClient();
        var serviceInstance = NewService(RecipeConfigLoader.Load(out _), Client);

        Assert.False(serviceInstance.IsEnabled);

        // 停止动作必须无条件生效：使能断开时拒绝停止，就等于让轴一直动下去
        await serviceInstance.StopJogAsync("X");

        // 正转 D1210.2 / 反转 D1210.3 同在一字上：两次读-改-写，写回的字都清掉了目标位
        Assert.Equal(2, Client.RegisterWrite.Count);
        Assert.All(Client.RegisterWrite, Write => Assert.Equal(0, Write.Value & 0x0C));

        Assert.Equal(new[] { 1210, 1210 }, Client.RegisterWrite.Select(ComponentItem => ComponentItem.Address).ToArray());
    }

    [Theory]
    [InlineData("X", 1210)]
    [InlineData("Z", 1410)]
    [InlineData("R", 1510)]
    public async Task StopShouldResetTheAxisCommandWordPositiveReverseTwoBit(string axisNumber, int commandWordAddress)
    {
        await using var Client = new FakeModbusClient();
        var serviceInstance = NewService(RecipeConfigLoader.Load(out _), Client);

        await serviceInstance.StopJogAsync(axisNumber);

        // 命令字偏移 10：位 2 正转、位 3 反转；停止后两位都应为 0
        Assert.Equal(2, Client.RegisterWrite.Count);
        Assert.All(Client.RegisterWrite, Write =>
        {
            Assert.Equal(commandWordAddress, Write.Address);
            Assert.Equal(0, Write.Value & 0x0C);
        });
    }
}
