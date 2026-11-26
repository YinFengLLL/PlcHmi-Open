using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PlcHmi.Business;
using PlcHmi.Business.Configuration;
using PlcHmi.Business.Services;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Models;

namespace PlcHmi.Tests;

/// <summary>
/// 回零 / 点动安全与点表权限边界的测试。
///
/// <para>回零与点动都是真实机械动作：未使能时必须拒绝；停止点动则必须无条件生效
/// （否则轴会一直动下去）。点表示教属工程师及以上，越权必须被服务层拦下。</para>
/// </summary>
public class PointTableSafetyAndPermissionTest
{
    /// <summary>全部拒绝的权限守卫（仅测试用）。</summary>
    private sealed class RejectAllPermissionGuard : IPermissionGuard
    {
        public void WantRequire(Permission permissionItem) =>
            throw new UnauthorizedAccessException($"测试：缺少「{permissionItem}」权限。");
    }

    private static ServiceProvider BuildRuntime(IPermissionGuard guard)
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
        serviceCollection.AddSingleton(guard);

        return serviceCollection.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public async Task DisabledWhenHomeShouldBeRejected()
    {
        await using var provider = BuildRuntime(new AllowAllPermissionGuard());
        var hosting = provider.GetRequiredService<IHostedService>();
        await hosting.StartAsync(CancellationToken.None);

        try
        {
            var Manual = provider.GetRequiredService<IManualWidgetService>();

            // 默认未使能
            Manual.SetEnabled(false);

            await Assert.ThrowsAsync<InvalidOperationException>(() => Manual.HomeAsync("X"));
        }
        finally
        {
            await hosting.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task DisabledWhenJogShouldBeRejected()
    {
        await using var provider = BuildRuntime(new AllowAllPermissionGuard());
        var hosting = provider.GetRequiredService<IHostedService>();
        await hosting.StartAsync(CancellationToken.None);

        try
        {
            var Manual = provider.GetRequiredService<IManualWidgetService>();
            Manual.SetEnabled(false);

            await Assert.ThrowsAsync<InvalidOperationException>(() => Manual.JogAxisAsync("X", 1));
        }
        finally
        {
            await hosting.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StopJogShouldHaveNoConditionGenerateEffect()
    {
        await using var provider = BuildRuntime(new AllowAllPermissionGuard());
        var hosting = provider.GetRequiredService<IHostedService>();
        await hosting.StartAsync(CancellationToken.None);

        try
        {
            var Manual = provider.GetRequiredService<IManualWidgetService>();

            // 刻意不使能：松开即停是安全出口，不得因未使能而拒绝
            Manual.SetEnabled(false);

            await Manual.StopJogAxisAsync("X");
            await Manual.StopJogAxisAsync("GlueX");
        }
        finally
        {
            await hosting.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task NotConfigAxisHomeShouldBeRejected()
    {
        await using var provider = BuildRuntime(new AllowAllPermissionGuard());
        var hosting = provider.GetRequiredService<IHostedService>();
        await hosting.StartAsync(CancellationToken.None);

        try
        {
            var Manual = provider.GetRequiredService<IManualWidgetService>();
            Manual.SetEnabled(true);
            await Manual.SyncEnabledAsync();

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Manual.HomeAsync("无此轴"));
            Assert.Contains("回零", exception.Message);
        }
        finally
        {
            await hosting.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ActionCoilReadShouldAndWriteInverseAndNotAffectedByEnableLimit()
    {
        await using var provider = BuildRuntime(new AllowAllPermissionGuard());
        var hosting = provider.GetRequiredService<IHostedService>();
        await hosting.StartAsync(CancellationToken.None);

        try
        {
            var Manual = provider.GetRequiredService<IManualWidgetService>();
            var Client = provider.GetRequiredService<IModbusClient>();

            var truncateStop = Environment.TickCount64 + 5000;
            while (!Client.IsConnected && Environment.TickCount64 < truncateStop)
            {
                await Task.Delay(50);
            }

            Manual.SetEnabled(true);

            // 切换型动作按钮的"激活 / 取消激活"就是"写一次 + 回读一次"：
            // 写进去什么，读出来必须是什么（否则按钮状态会长期说反话）
            await Manual.WriteCoilAsync("M1100", true);
            Assert.True(await Manual.ReadCoilAsync("M1100"));

            await Manual.WriteCoilAsync("M1100", false);
            Assert.False(await Manual.ReadCoilAsync("M1100"));

            // 只读不受手动使能限制：示教页进入时要回读按钮真实状态，此时使能常常还没开
            Manual.SetEnabled(false);
            Assert.False(await Manual.ReadCoilAsync("M1100"));
        }
        finally
        {
            await hosting.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task PageContextShouldWriteD10AndTwoPageEachPointCode()
    {
        await using var provider = BuildRuntime(new AllowAllPermissionGuard());
        var hosting = provider.GetRequiredService<IHostedService>();
        await hosting.StartAsync(CancellationToken.None);

        try
        {
            var Client = provider.GetRequiredService<IModbusClient>();
            var context = provider.GetRequiredService<IPageContextService>();

            var truncateStop = Environment.TickCount64 + 5000;
            while (!Client.IsConnected && Environment.TickCount64 < truncateStop)
            {
                await Task.Delay(50);
            }

            // 页码 = 当前页面（主监控 1、按侧栏顺序递增）；点码 = 各页选中项（0 基）
            await context.WritePageNumberAsync(4);
            await context.WritePointNumberAsync(PageContextRegister.screwTeachPointCode, 7);
            await context.WritePointNumberAsync(PageContextRegister.dispenseTeachPointCode, 12);
            await context.WritePointNumberAsync(PageContextRegister.calibrationOtherPointPointCode, 3);

            var page = await Client.ReadHoldingRegisterAsync(PageContextRegister.pageNumber, 1);
            var screw = await Client.ReadHoldingRegisterAsync(PageContextRegister.screwTeachPointCode, 1);
            var dispense = await Client.ReadHoldingRegisterAsync(PageContextRegister.dispenseTeachPointCode, 1);
            var Calibration = await Client.ReadHoldingRegisterAsync(PageContextRegister.calibrationOtherPointPointCode, 1);

            Assert.Equal((ushort)4, page[0]);
            Assert.Equal((ushort)7, screw[0]);
            Assert.Equal((ushort)12, dispense[0]);
            Assert.Equal((ushort)3, Calibration[0]);

            // "最后值覆盖"语义：再选别的点应直接覆盖，不累加；且不得动其它页的字
            await context.WritePointNumberAsync(PageContextRegister.screwTeachPointCode, 0);

            var screw0 = await Client.ReadHoldingRegisterAsync(PageContextRegister.screwTeachPointCode, 1);
            var dispenseNotChange = await Client.ReadHoldingRegisterAsync(PageContextRegister.dispenseTeachPointCode, 1);
            var calibrationNotChange = await Client.ReadHoldingRegisterAsync(PageContextRegister.calibrationOtherPointPointCode, 1);

            Assert.Equal((ushort)0, screw0[0]);
            Assert.Equal((ushort)12, dispenseNotChange[0]);
            Assert.Equal((ushort)3, calibrationNotChange[0]);
        }
        finally
        {
            await hosting.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task MissingEditTeachPermissionWhenPointTableTeachShouldBeRejected()
    {
        await using var provider = BuildRuntime(new RejectAllPermissionGuard());
        var hosting = provider.GetRequiredService<IHostedService>();
        await hosting.StartAsync(CancellationToken.None);

        try
        {
            var PointTable = provider.GetRequiredService<IPointTableService>();

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => PointTable.TeachCurrentPositionAsync("锁附", 0));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => PointTable.WritePointTableAsync("锁附"));
        }
        finally
        {
            await hosting.StopAsync(CancellationToken.None);
        }
    }
}
