using Microsoft.Extensions.Logging.Abstractions;
using PlcHmi.Business.Services;
using PlcHmi.Core.Abstractions;
using PlcHmi.Tests.Fake;

namespace PlcHmi.Tests;

/// <summary>
/// IO 通信诊断测试：X / Y 段 × FC01 / FC02 的原始读数与结论。
///
/// <para><b>为什么值得单测：</b>2026-10-17 现场第二次报障"X 对不上"的真因是 X 被放在线圈表
/// （用 FC01 读输入区）——连接正常、无异常、无告警，只是读到了另一块内存。诊断服务把两个
/// 功能码的读数并排摆出来，这类错位才看得见；结论文案必须能直接指导现场改配置。</para>
/// </summary>
public class IoDiagnosticServiceTest
{
    private static async Task<IoDiagnosticReport> Diagnostic(FakeBitAreaClient Client) =>
        await new IoDiagnosticService(Client, NullLogger<IoDiagnosticService>.Instance)
            .DiagnosticAsync(FakeBitAreaClient.XBaseAddress, FakeBitAreaClient.YBaseAddress, 16);

    /// <summary>按给定位区行为跑一次诊断（替身由 using 释放，满足 CA2000）。</summary>
    private static async Task<IoDiagnosticReport> Diagnostic(
        BitAreaRowAs coilRowAs, BitAreaRowAs discreteRowAs, Action<FakeBitAreaClient>? setAction = null)
    {
        await using var Client = new FakeBitAreaClient();
        Client.XYCoilRowAs = coilRowAs;
        Client.XYDiscreteRowAs = discreteRowAs;
        setAction?.Invoke(Client);

        return await Diagnostic(Client);
    }

    private static IoDiagnosticItem GetRow(IoDiagnosticReport report, string Title) =>
        report.ItemCollection.Single(Item => Item.Title == Title);

    [Fact]
    public async Task Diagnostic_ShouldReadFourFunctionCodeAndAddressSectionGroupValid()
    {
        var report = await Diagnostic(BitAreaRowAs.ByPhysicalState, BitAreaRowAs.ByPhysicalState);

        Assert.Equal(4, report.ItemCollection.Count);
        Assert.Contains(report.ItemCollection, Item => Item.Title == "X 区 · 线圈" && Item.functionCode == "FC01");
        Assert.Contains(report.ItemCollection, Item => Item.Title == "X 区 · 离散输入" && Item.functionCode == "FC02");
        Assert.Contains(report.ItemCollection, Item => Item.Title == "Y 区 · 线圈" && Item.functionCode == "FC01");
        Assert.Contains(report.ItemCollection, Item => Item.Title == "Y 区 · 离散输入" && Item.functionCode == "FC02");

        // 成功的行要给 16 位位串（高位在左，长度与请求位数一致）
        foreach (var Item in report.ItemCollection.Where(Item => Item.Success))
        {
            Assert.Equal(16, Item.bitSerial.Length);
            Assert.Matches("^[01]{16}$", Item.bitSerial);
        }
    }

    [Fact]
    public async Task Diagnostic_XAreaOnlyHasFC02HasSignal_ConclusionShouldPointToDiscreteInput()
    {
        // 复刻现场：FC01 在线圈表读"成功"但恒 0；物理输入在 FC02
        var report = await Diagnostic(BitAreaRowAs.AlwaysReadZero, BitAreaRowAs.ByPhysicalState,
            Client => Client.SetPhysicalState(FakeBitAreaClient.XBaseAddress + 0, true));   // X0 上料到位

        Assert.Equal(0, GetRow(report, "X 区 · 线圈").setOneBitCount);
        Assert.Equal(1, GetRow(report, "X 区 · 离散输入").setOneBitCount);

        // 结论必须直接告诉现场下一步怎么配
        Assert.Contains("离散输入", report.Conclusion);
        Assert.Contains("X区域", report.Conclusion);
    }

    [Fact]
    public async Task Diagnostic_TwoFunctionCodeBothReadToSignal_ConclusionShouldWantRequireShowStyleConfig()
    {
        var report = await Diagnostic(BitAreaRowAs.ByPhysicalState, BitAreaRowAs.ByPhysicalState,
            Client => Client.SetPhysicalState(FakeBitAreaClient.XBaseAddress + 0, true));

        Assert.True(GetRow(report, "X 区 · 线圈").setOneBitCount > 0);
        Assert.True(GetRow(report, "X 区 · 离散输入").setOneBitCount > 0);
        Assert.Contains("无法自动判定", report.Conclusion);
    }

    [Fact]
    public async Task Diagnostic_TwoFunctionCodeBothReadNotTo_ConclusionShouldPointToConnectAndAddress()
    {
        var report = await Diagnostic(BitAreaRowAs.ThrowException, BitAreaRowAs.ThrowException);

        Assert.All(report.ItemCollection, Item =>
        {
            Assert.False(Item.Success);
            Assert.Equal("读不到", Item.ResultText);
            Assert.False(string.IsNullOrWhiteSpace(Item.errorMessage));
        });

        Assert.Contains("都读不到", report.Conclusion);
    }

    [Fact]
    public async Task Diagnostic_TwoFunctionCodeBothReadToAllZero_ConclusionShouldHintCheckPairAddress()
    {
        var report = await Diagnostic(BitAreaRowAs.AlwaysReadZero, BitAreaRowAs.AlwaysReadZero);

        Assert.All(report.ItemCollection, Item => Assert.Equal(0, Item.setOneBitCount));
        Assert.Contains("全 0", report.Conclusion);
        Assert.Contains("八进制", report.Conclusion);
    }
}
