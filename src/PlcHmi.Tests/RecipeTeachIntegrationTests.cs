using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PlcHmi.Business;
using PlcHmi.Business.Configuration;
using PlcHmi.Business.Services;
using PlcHmi.Communication.Modbus;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Tests;

/// <summary>
/// 机种（配方）与示教功能的端到端集成测试。
///
/// <para>跑的是<b>真实配置文件</b>（<c>config/recipe-config.json</c>、<c>config/recipes.json</c>）
/// 加虚拟 PLC，覆盖：机种切换 → 参数下发 → 示教写入 → 读回校验 → 面启用拦截。</para>
///
/// <para>这些测试守住的是"地址算对了但落盘落错地方"这类问题 ——
/// 单元测试测不出，只有真跑一遍才会暴露。</para>
///
/// <para><b>本文件只放夹具与「机种 / 参数」侧用例</b>；示教点表侧用例在
/// <c>RecipeTeachIntegrationTests.TeachPoint.cs</c>（同类 partial part）。
/// 拆开是因为合并后单个文件超过了 400 行审查阈值，而 400 行以上很难在审查时维持完整上下文。</para>
/// </summary>
public partial class RecipeTeachIntegrationTest
{
    // 轴参数区（2026-09-17 按 PLC 地址分配表）：轴 N 基址 = 1200 + 100×(N-1)，定位速度在偏移 2
    private const int parameterXBaseAddress = 1400;
    private const int parameterYBaseAddress = 1302;
    private const int parameterZBaseAddress = 1402;
    private const int parameterZZBaseAddress = 1502;
    private const int parameterGlueXBaseAddress = 1602;

    /// <summary>用与生产完全同一套编解码解出 Float32（CDAB，低字在前）。</summary>
    private static float DecodeFloat(ushort[] registerList, int OffsetValue) =>
        (float)RegisterCodec.Decode(
            TagDataType.Float32, registerList.AsSpan(OffsetValue, 2), WordOrder.LittleEndian);

    private static ServiceProvider BuildRuntime()
    {
        var Settings = AppSettingsLoader.Load(out var SettingsError);
        var RecipeConfig = RecipeConfigLoader.Load(out var recipeError);

        Assert.Null(SettingsError);
        Assert.Null(recipeError);

        // 仓库配置已切到真实 PLC 模式（现场部署），但测试必须保持密封：
        // 与 AlarmScanIntegrationTests 同款做法——无论仓库配置处于哪种模式，
        // 这里一律强制模拟器，集成测试永远不可能碰到真实 PLC。
        Settings.PlcConnection.UseSimulator = true;

        Settings.Polling.FastIntervalMilliseconds = 50;
        Settings.Polling.SlowIntervalMilliseconds = 100;
        Settings.Polling.EventIntervalMilliseconds = 100;

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging();
        serviceCollection.AddPlcHmiService(Settings.PlcConnection, Settings.Polling, RecipeConfig);

        // 本组用例不验证权限边界（那由 PermissionGuardTests 覆盖），以受信任身份运行
        serviceCollection.AddSingleton<IPermissionGuard, AllowAllPermissionGuard>();

        return serviceCollection.BuildServiceProvider(validateScopes: true);
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

            await Task.Delay(50);
        }

        Assert.Fail($"等待超时：{Description}");
    }

    /// <summary>读一个 Float32 寄存器对。</summary>
    private static async Task<float> ReadFloatAsync(IModbusClient Client, int addressValue)
    {
        var registerList = await Client.ReadHoldingRegisterAsync(addressValue, 2);
        return (float)RegisterCodec.Decode(TagDataType.Float32, registerList, WordOrder.LittleEndian)!;
    }

    [Fact]
    public async Task FourAxisCurrentPositionShouldWithFloatSampling()
    {
        await using var provider = BuildRuntime();
        var hosting = provider.GetRequiredService<IHostedService>();
        await hosting.StartAsync(CancellationToken.None);

        try
        {
            var ValueList = provider.GetRequiredService<ITagValueService>();

            await WaitUntilAsync(() => ValueList.GetNewValue("AxisXPosition").IsValid, "采到 X 轴位置");

            // 待机状态下四轴都在 0；关键是能正确解码（若字序错了这里会得到荒谬值）
            var coordinateX = ValueList.GetNewValue("AxisXPosition").scaleAfterNewValue;
            var coordinateZ = ValueList.GetNewValue("AxisZPosition").scaleAfterNewValue;

            Assert.InRange(coordinateX, -999.99, 9999.99);
            Assert.InRange(coordinateZ, -999.99, 9999.99);
        }
        finally
        {
            await hosting.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task InitializeShouldReadToPlcMachineModelNumberAndLoadInPairShouldParameter()
    {
        await using var provider = BuildRuntime();
        var hosting = provider.GetRequiredService<IHostedService>();
        await hosting.StartAsync(CancellationToken.None);

        try
        {
            var recipeItem = provider.GetRequiredService<IRecipeService>();

            // 虚拟 PLC 初始机种号为 1
            Assert.Equal(1, recipeItem.CurrentRecipeNumber);

            // 参数项来自配置：六轴各一项定位速度（*.*ManualSpeed 已废弃，改由点动档位寄存器决定）
            Assert.Equal(6, recipeItem.ParameterSpec.Count);
            Assert.Equal(6, recipeItem.CurrentParameter.Count);

            // 每项都应有值（缺失时用配置默认值补齐）
            foreach (var specItem in recipeItem.ParameterSpec)
            {
                Assert.True(recipeItem.CurrentParameter.ContainsKey(specItem.Key), $"缺少参数 {specItem.Key}");
            }
        }
        finally
        {
            await hosting.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task SwitchMachineModelShouldSendTheMachineModelSectionSnapshotAndWriteMachineModelNumber()
    {
        await using var provider = BuildRuntime();
        var hosting = provider.GetRequiredService<IHostedService>();
        await hosting.StartAsync(CancellationToken.None);

        try
        {
            var Client = provider.GetRequiredService<IModbusClient>();
            var recipeItem = provider.GetRequiredService<IRecipeService>();

            // 1. 模拟现场：先在 PLC 的机种区段写入一组点位
            //    锁附第 1 点 X = 300.0（Float32 字序 CDAB，低字在前）+ 点胶指令码 5
            await Client.WriteMultiRegisterAsync(2500, [(ushort)0x0000, (ushort)0x4396]);
            await Client.WriteMultiRegisterAsync(3400, [(ushort)5]);

            // 2. 「保存机种」：把 PLC 区段读回来存成机种 3
            await recipeItem.SaveMachineModelAsync(3);

            var Snapshot = recipeItem.SectionSnapshot(3);
            Assert.Equal(0x4396, Snapshot[1]);              // D2501
            Assert.Equal(5, Snapshot[900]);                 // D3400（3400 - 2500）

            // 3. 清掉 PLC 区段，再切到机种 3 —— 切换应把快照整段写回 PLC
            await Client.WriteMultiRegisterAsync(2500, [(ushort)0, (ushort)0]);
            await Client.WriteMultiRegisterAsync(3400, [(ushort)0]);

            await recipeItem.SwitchAsync(3);

            Assert.Equal(3, recipeItem.CurrentRecipeNumber);

            // 字序红线：写反了这里会读成 0
            var pointX = await Client.ReadHoldingRegisterAsync(2500, 2);
            Assert.Equal((ushort)0x0000, pointX[0]);   // 低字
            Assert.Equal((ushort)0x4396, pointX[1]);   // 高字 → 300.00
            Assert.Equal(300f, DecodeFloat(pointX, 0));

            var instructionCode = await Client.ReadHoldingRegisterAsync(3400, 1);
            Assert.Equal((ushort)5, instructionCode[0]);

            // 机种号也应写进 PLC（D3330 落在机种区段内，切换末尾以目标机种号覆盖）
            var recipeNumberRegister = await Client.ReadHoldingRegisterAsync(3330, 1);
            Assert.Equal((ushort)3, recipeNumberRegister[0]);
        }
        finally
        {
            await hosting.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task SwitchMachineModelShouldNotChangeMoveGlobalParameter()
    {
        await using var provider = BuildRuntime();
        var hosting = provider.GetRequiredService<IHostedService>();
        await hosting.StartAsync(CancellationToken.None);

        try
        {
            var Client = provider.GetRequiredService<IModbusClient>();
            var recipeItem = provider.GetRequiredService<IRecipeService>();

            // 先把全局参数下发到 PLC，并记下参数区实际内容
            recipeItem.SetParameter("X.LocateSpeed", 321);
            await recipeItem.WriteParameterAsync();

            var switchBefore = await Client.ReadHoldingRegisterAsync(parameterXBaseAddress, 2);
            var originalNumber = recipeItem.CurrentRecipeNumber;

            // 切机种（该机种没有区段数据 → 允许切换、区段清零，但绝不该动参数区）
            await recipeItem.SwitchAsync(5);
            Assert.Equal(5, recipeItem.CurrentRecipeNumber);

            var switchAfter = await Client.ReadHoldingRegisterAsync(parameterXBaseAddress, 2);

            Assert.Equal(switchBefore, switchAfter);
            Assert.Equal(321f, DecodeFloat(switchAfter, 0));

            // 还原当前机种号，避免影响同集合里的其它用例
            await recipeItem.SwitchAsync(originalNumber);
        }
        finally
        {
            await hosting.StopAsync(CancellationToken.None);
        }
    }
}
