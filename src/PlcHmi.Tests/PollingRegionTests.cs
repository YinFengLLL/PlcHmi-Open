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
/// 机种区段（D2500–D3999）不参与周期采集（2026-10-21 现场要求）。
///
/// <para><b>为什么要钉住：</b>点表 / 单点工艺参数 / 机种快照都落在这个区段里。
/// 若轮询仍把它们读进 <c>标签值服务</c>，示教页的数据会"跟着 PLC 实时跳"，
/// 与操作员正在编辑的值互相踩——现场看到的正是"我填的值自己变了"。</para>
/// </summary>
[Collection("config-files")]
public class MachineModelSectionPollingTest
{
    private static ServiceProvider BuildRuntime()
    {
        var Settings = AppSettingsLoader.Load(out var SettingsError);
        var RecipeConfig = RecipeConfigLoader.Load(out var recipeError);

        Assert.Null(SettingsError);
        Assert.Null(recipeError);

        Settings.PlcConnection.UseSimulator = true;
        Settings.Polling.FastIntervalMilliseconds = 50;
        Settings.Polling.SlowIntervalMilliseconds = 100;
        Settings.Polling.EventIntervalMilliseconds = 100;

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging();
        serviceCollection.AddPlcHmiService(Settings.PlcConnection, Settings.Polling, RecipeConfig);
        serviceCollection.AddSingleton<IPermissionGuard, AllowAllPermissionGuard>();

        return serviceCollection.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public async Task MachineModelSectionPointShouldNotBeWeekPeriodSampling()
    {
        await using var provider = BuildRuntime();
        var hosting = provider.GetRequiredService<IHostedService>();
        await hosting.StartAsync(CancellationToken.None);

        try
        {
            var Client = provider.GetRequiredService<IModbusClient>();
            var ValueList = provider.GetRequiredService<ITagValueService>();

            // 区段外：D1104 目标产量（慢分组；模拟器只在初始化时写一次，循环里不改它）
            // 区段内：D3331 机种总数（慢分组）
            await Client.WriteMultiRegisterAsync(1104, [(ushort)4321, (ushort)0]);
            await Client.WriteSingleRegisterAsync(3331, 7);

            await Task.Delay(600);

            // 区段外照常采集
            Assert.True(ValueList.TryGetDouble("TargetCount", out var targetOutput));
            Assert.Equal(4321, targetOutput);

            // 区段内一个都不读：标签值服务里根本没有这个点（不是"读到 0"）
            Assert.False(ValueList.TryGetDouble("RecipeCount", out _));
            Assert.False(ValueList.TryGetDouble("TeachIndex", out _));
            Assert.False(ValueList.TryGetDouble("RecipeNo", out _));
        }
        finally
        {
            await hosting.StopAsync(CancellationToken.None);
        }
    }
}
