using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PlcHmi.Business;
using PlcHmi.Business.Configuration;
using PlcHmi.Business.Services;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Models;

namespace PlcHmi.Tests;

/// <summary>
/// 手动使能信号（M666）测试。
///
/// <para><b>现场口径（2026-10-15）：</b>手动使能开关打开时<b>持续</b>把 M666 置位，
/// 关闭时<b>持续</b>复位。所以这里不只断言"切换时写一次"，还断言
/// <b>PLC 程序把该位复位后会被自动补回</b>——只写一次的写法在这种场景下会与界面状态分叉：
/// 界面显示"已使能"、PLC 侧却认为"未使能"，操作员按点动没反应却查不出原因。</para>
/// </summary>
public class ManualEnableSignalTest
{
    private static ServiceProvider BuildRuntime()
    {
        var Settings = AppSettingsLoader.Load(out _);
        var RecipeConfig = RecipeConfigLoader.Load(out _);

        Settings.PlcConnection.UseSimulator = true;
        Settings.Polling.FastIntervalMilliseconds = 50;
        Settings.Polling.SlowIntervalMilliseconds = 100;
        Settings.Polling.EventIntervalMilliseconds = 100;

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging();
        serviceCollection.AddPlcHmiService(Settings.PlcConnection, Settings.Polling, RecipeConfig);
        serviceCollection.AddSingleton<IPermissionGuard>(new AllowAllPermissionGuard());

        return serviceCollection.BuildServiceProvider(validateScopes: true);
    }

    /// <summary>启动全部宿主服务（与真实程序一致：连接、轮询、报警扫描、使能保持……都跑起来）。</summary>
    private static async Task<IReadOnlyList<IHostedService>> StartAllHostService(ServiceProvider provider)
    {
        var serviceList = provider.GetServices<IHostedService>().ToList();

        foreach (var serviceItem in serviceList)
        {
            await serviceItem.StartAsync(CancellationToken.None);
        }

        return serviceList;
    }

    private static async Task WaitUntil(Func<Task<bool>> condition, string Description, int TimeoutMilliseconds = 4000)
    {
        var truncateStopTime = Environment.TickCount64 + TimeoutMilliseconds;

        while (Environment.TickCount64 < truncateStopTime)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"等待超时：{Description}");
    }

    [Fact]
    public async Task EnableSwitchSwitchWhenShouldWriteM666()
    {
        await using var provider = BuildRuntime();
        var hostService = await StartAllHostService(provider);

        try
        {
            var Client = provider.GetRequiredService<IModbusClient>();
            var Manual = provider.GetRequiredService<IManualWidgetService>();

            await WaitUntil(() => Task.FromResult(Client.IsConnected), "PLC 连接建立");

            // 打开 → 置位
            Manual.SetEnabled(true);
            await Manual.SyncEnabledAsync();

            var openStart = await Client.ReadCoilAsync(ManualEnableRegister.Coil, 1);
            Assert.True(openStart[0], $"手动使能打开后 M{ManualEnableRegister.Coil} 应为 ON");

            // 关闭 → 复位
            Manual.SetEnabled(false);
            await Manual.SyncEnabledAsync();

            var Close = await Client.ReadCoilAsync(ManualEnableRegister.Coil, 1);
            Assert.False(Close[0], $"手动使能关闭后 M{ManualEnableRegister.Coil} 应为 OFF");
        }
        finally
        {
            foreach (var serviceItem in hostService)
            {
                await serviceItem.StopAsync(CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task HoldContinueHoldShouldPlcSideResetManualEnableFillBack()
    {
        await using var provider = BuildRuntime();
        var hostService = await StartAllHostService(provider);

        try
        {
            var Client = provider.GetRequiredService<IModbusClient>();
            var Manual = provider.GetRequiredService<IManualWidgetService>();

            await WaitUntil(() => Task.FromResult(Client.IsConnected), "PLC 连接建立");

            // 打开使能，但**不**主动同步：只有常驻保持服务在周期写
            Manual.SetEnabled(true);

            await WaitUntil(
                async () => (await Client.ReadCoilAsync(ManualEnableRegister.Coil, 1))[0],
                "使能保持服务把 M666 置位");

            // 模拟 PLC 程序自己复位了该位（急停 / 模式切换 / 上电初始化）
            await Client.WriteSingleCoilAsync(ManualEnableRegister.Coil, false);

            await WaitUntil(
                async () => (await Client.ReadCoilAsync(ManualEnableRegister.Coil, 1))[0],
                "PLC 侧复位后由保持服务补回置位");

            // 关闭使能后同样要保持复位
            Manual.SetEnabled(false);

            await WaitUntil(
                async () => !(await Client.ReadCoilAsync(ManualEnableRegister.Coil, 1))[0],
                "关闭使能后 M666 保持复位");
        }
        finally
        {
            foreach (var serviceItem in hostService)
            {
                await serviceItem.StopAsync(CancellationToken.None);
            }
        }
    }
}
