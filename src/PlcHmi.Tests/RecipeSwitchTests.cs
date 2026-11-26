using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PlcHmi.Business;
using PlcHmi.Business.Configuration;
using PlcHmi.Business.Services;
using PlcHmi.Communication.Modbus;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Models;

namespace PlcHmi.Tests;

/// <summary>
/// 机种切换回归（2026-10-21 现场报障）。
///
/// <para><b>现场现象：</b>点「切换」到一个刚建、还没保存过数据的机种，弹"机种 N 还没有数据。
/// 请先在示教页调好点位…"，<b>永远切不过去</b>。空机种本来就是新建机种的常态，
/// 切换时必须能落地：清空机种区段 + 写机种号，然后去示教。</para>
///
/// <para>手动「下发机种」仍然拒绝空机种——那等于一声不响把区段清空，必须走「切换」这条确认路径。</para>
/// </summary>
[Collection("config-files")]
public class MachineModelSwitchTest
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
    public async Task SwitchToNoHasDataMachineModelShouldSucceedAndClearEmptyMachineModelSection()
    {
        await using var provider = BuildRuntime();
        var hosting = provider.GetRequiredService<IHostedService>();
        await hosting.StartAsync(CancellationToken.None);

        var Client = provider.GetRequiredService<IModbusClient>();
        var Recipe = provider.GetRequiredService<IRecipeService>();

        var originalNumber = Recipe.CurrentRecipeNumber;
        var targetNumber = originalNumber == 20 ? 19 : 20;

        try
        {
            // 目标机种确保是"没有数据"的空机种
            await Recipe.WriteMachineModelSnapshotAsync(targetNumber, new Dictionary<int, int>());
            Assert.Empty(Recipe.SectionSnapshot(targetNumber));

            // PLC 里先放旧机种的残留数据（机种区段内）
            await Client.WriteMultiRegisterAsync(2500, [1, 2, 3]);

            await Recipe.SwitchAsync(targetNumber);

            // 切换生效：内存与 PLC 的机种号都变了
            Assert.Equal(targetNumber, Recipe.CurrentRecipeNumber);
            Assert.Equal(targetNumber, (await Client.ReadHoldingRegisterAsync(3330, 1))[0]);

            // 空机种下发 = 区段整段清零（不留上一机种的点位）
            var sectionFirstWord = await Client.ReadHoldingRegisterAsync(2500, 3);
            Assert.All(sectionFirstWord, Value => Assert.Equal(0, Value));
        }
        finally
        {
            // 还原当前机种号，避免影响同集合里的其它用例
            if (Recipe.CurrentRecipeNumber != originalNumber)
            {
                await Recipe.SwitchAsync(originalNumber);
            }

            await hosting.StopAsync(CancellationToken.None);
        }
    }
}
