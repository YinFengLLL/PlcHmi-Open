using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PlcHmi.Business;
using PlcHmi.Business.Configuration;
using PlcHmi.Business.Services;
using PlcHmi.Communication.Modbus;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;

namespace PlcHmi.Tests;

/// <summary>
/// 现场实测向量回归测试（2026-09-12 物理联调）。
///
/// <para><b>背景</b>：真机上 HMI 位置显示 3.765e26，初步怀疑 Float32 字序反了。
/// PLC 监控实测：D100=-26228(0x999C)、D101=17043(0x4293)，监控浮点显示 73.79896 ——
/// 按 CDAB（低字在前）拼装 0x4293999C ≈ 73.77，与 PLC 显示吻合（轴在运动，
/// 监控表各字段刷新有零点几的时差，故低字略有出入）。若按 ABCD 反序解释则为
/// 0x999C4293 ≈ -4.7e-8，量级天差地别。</para>
///
/// <para><b>结论</b>：PLC 字序确为 CDAB，与 <c>tags.json</c> 的 <c>wordOrder: LittleEndian</c>
/// 一致；经逐行复核，三条读路径（轮询采集 / 示教点表 / 一致性校验）的解码实现全部正确
/// ——现场天文数字是 PLC D 区当时的残留数据（该点从未被 PLC 程序写入有效位置），
/// 不是解码缺陷。</para>
///
/// <para><b>这组测试锁的是什么</b>：把现场向量固化。今后任何人改字序默认值、
/// 解码调用或分块切片，测试立刻红，而不是等现场再爆一次天文数字。</para>
/// </summary>
public class FieldVectorDecodeTest
{
    [Fact]
    public void FieldVector_CDABShouldResolveAndPlcMonitorControlConsistentValue()
    {
        // 现场监控：D100=-26228(0x999C=39308，低字)、D101=17043(0x4293，高字)
        // → 位模式 0x4293999C = 73.8000183，与 PLC 监控浮点显示 73.79896 仅差 0.001
        //   （轴在微动，监控表字值与浮点值刷新存在瞬时差）——CDAB 判定成立
        var decoded = (float)RegisterCodec.Decode(
            TagDataType.Float32, new ushort[] { 0x999C, 0x4293 }, WordOrder.LittleEndian);

        Assert.Equal(73.800f, decoded, precision: 3);
    }

    [Fact]
    public void FieldVector_Float73Point79896CDABEncodeShouldRoundTripConsistent()
    {
        // PLC 监控浮点 73.79896f = 0x4293_9911 → CDAB：低字 0x9911(39185) 在 D100、高字 0x4293 在 D101
        var registerList = RegisterCodec.Encode(TagDataType.Float32, 73.79896f, WordOrder.LittleEndian);

        Assert.Equal(0x9911, registerList[0]);
        Assert.Equal(0x4293, registerList[1]);

        var decoded = (float)RegisterCodec.Decode(TagDataType.Float32, registerList, WordOrder.LittleEndian);
        Assert.Equal(73.79896f, decoded, precision: 4);
    }

    [Fact]
    public void ReverseWordOrderWillGetToCompleteAllDifferentValue_ProveExplicitFieldSymptomStateAndWordOrderNoneClose()
    {
        // 同样两个字按 ABCD（高字在前）解释 → 0x999C4293 ≈ -4.7e-8，与 73.8 天差地别。
        // 现场实际显示 3.765e26，与正反两个字序都不符 → 症状出在 D 区当时的数据本身，不是解码。
        var hasWrong = (float)RegisterCodec.Decode(
            TagDataType.Float32, new ushort[] { 0x999C, 0x4293 }, WordOrder.BigEndian);

        Assert.True(MathF.Abs(hasWrong) < 1e-5f, $"反字序应得到极小值，实际 {hasWrong}");
    }
}

/// <summary>
/// 现场向量·集成级：验证「模拟 PLC → 轮询采集 → 解码 → 发布」全链的 Float32 字序。
///
/// <para>用模拟器的轴运动收敛值 120.0 做精确断言：120.0 = 0x42F00000，
/// CDAB 编码为 [0x0000, 0x42F0]；若链路字序错了，会解出 0x000042F0 ≈ 1.7e-5，
/// 与 120 相差 7 个数量级，区分度足够。</para>
/// </summary>
public class FieldVectorPollingTest
{
    private static ServiceProvider BuildRuntime()
    {
        var Settings = AppSettingsLoader.Load(out var error);
        Assert.Null(error);

        // 仓库配置处于真实 PLC 模式（现场部署）；测试强制模拟器，保持密封
        Settings.PlcConnection.UseSimulator = true;
        Settings.Polling.FastIntervalMilliseconds = 50;
        Settings.Polling.SlowIntervalMilliseconds = 100;
        Settings.Polling.EventIntervalMilliseconds = 100;

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging();
        serviceCollection.AddPlcHmiService(Settings.PlcConnection, Settings.Polling, RecipeConfigLoader.Load(out _));

        // 本组用例不验证权限边界（那由 PermissionGuardTests 覆盖），以受信任身份运行
        serviceCollection.AddSingleton<IPermissionGuard, AllowAllPermissionGuard>();

        return serviceCollection.BuildServiceProvider(validateScopes: true);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string Description, int TimeoutMilliseconds = 8000)
    {
        var truncateStopTime = DateTime.UtcNow.AddMilliseconds(TimeoutMilliseconds);

        while (DateTime.UtcNow < truncateStopTime)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"等待超时：{Description}");
    }

    [Fact]
    public async Task PollingAllChainShouldSimulatorFloat32PositionByCDABResolveOutPreciseConfirmValue()
    {
        await using var provider = BuildRuntime();
        var hosting = provider.GetRequiredService<IHostedService>();

        await hosting.StartAsync(CancellationToken.None);

        try
        {
            var Connect = provider.GetRequiredService<IConnectManager>();
            var Device = provider.GetRequiredService<IDeviceService>();
            var ValueList = provider.GetRequiredService<ITagValueService>();

            await WaitUntilAsync(() => Connect.State == ConnectionState.Connected, "连接虚拟 PLC");

            // 启动后模拟器把 X 轴位置收敛到 D200 的目标值 120.0（Float32 CDAB 写 D100-D101）
            await Device.StartAsync();

            await WaitUntilAsync(
                () => ValueList.GetNewValue("AxisXPosition").IsValid
                    && Math.Abs(ValueList.GetNewValue("AxisXPosition").scaleAfterNewValue - 120.0) < 0.01,
                "X 轴位置收敛到 120.0");

            // 精确值断言（禁用宽范围断言——上次现场问题就是被 InRange 放过的）
            Assert.Equal(120.0, ValueList.GetNewValue("AxisXPosition").scaleAfterNewValue, precision: 2);

            // Y 轴模拟器恒写 0：若块内偏移或字序错位，这里会拿到相邻轴的脏数据
            Assert.Equal(0.0, ValueList.GetNewValue("AxisYPosition").scaleAfterNewValue, precision: 2);
        }
        finally
        {
            await hosting.StopAsync(CancellationToken.None);
        }
    }
}
