using System.Globalization;
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
/// 历史记录采集的端到端测试（虚拟 PLC）。
///
/// <para>验证「PLC 的触发位置位 → 读回配置里的各地址 → 按字序与小数位落成一条记录」这条完整链路。
/// 单元用例无法覆盖它：地址合并读取、上升沿判定、字序解码、按天落盘 都在真实服务里发生。</para>
///
/// <para>与 <c>config-files</c> 集合串行：本用例读的是真实 <c>config/history-record.json</c>，
/// 而 <see cref="HistoryRecordTest"/> 会临时改写它。</para>
/// </summary>
[Collection("config-files")]
public class HistoryRecordIntegrationTest
{
    [Fact]
    public async Task TriggerBitRisingEdgeShouldPersistOneItemRecordAndByDecimalBitShow()
    {
        var Settings = AppSettingsLoader.Load(out _);
        var RecipeConfig = RecipeConfigLoader.Load(out _);

        Settings.PlcConnection.UseSimulator = true;   // 虚拟 PLC：可写 D / M 后由采集服务读回

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging();
        serviceCollection.AddPlcHmiService(Settings.PlcConnection, Settings.Polling, RecipeConfig);
        serviceCollection.AddSingleton<IPermissionGuard, AllowAllPermissionGuard>();

        await using var provider = serviceCollection.BuildServiceProvider(validateScopes: true);
        var hosting = provider.GetServices<IHostedService>().ToList();

        foreach (var serviceInstance in hosting)
        {
            await serviceInstance.StartAsync(CancellationToken.None);
        }

        try
        {
            var History = provider.GetRequiredService<IHistoryRecordService>();
            var Client = provider.GetRequiredService<IModbusClient>();
            var TodayDate = DateTime.Today;

            Assert.True(History.Config.Enabled, "默认配置应启用采集");
            Assert.True(History.Config.DataItemCollection.Count > 0, "默认配置应有数据项");

            var previousCount = (await History.ReadDayAsync(TodayDate)).Count;

            // 地址一律取自配置（现场版：触发 M8、实际扭力 D218 INT16 小数位 3），
            // 避免"工厂配置换一次地址就红一片"。
            var TriggerAddress = History.Config.TriggerAddress.Trim();
            var triggerBit = int.Parse(TriggerAddress.AsSpan(1), CultureInfo.InvariantCulture);

            var torqueIndex = History.Config.DataItemCollection.FindIndex(Item => Item.Name == "实际扭力");
            Assert.True(torqueIndex >= 0, "出厂配置里应有「实际扭力」列");

            var torqueItem = History.Config.DataItemCollection[torqueIndex];
            var torqueAddress = int.Parse(torqueItem.Address.AsSpan(1), CultureInfo.InvariantCulture);

            // INT16 单字：600 × 10⁻³ = 0.600
            await Client.WriteSingleRegisterAsync(torqueAddress, 600);

            // 触发位上升沿：只有 0 → 1 的沿才记录
            await Client.WriteSingleCoilAsync(triggerBit, true);

            await WaitUntilAsync(
                async () => (await History.ReadDayAsync(TodayDate)).Count > previousCount,
                "触发位上升沿后应新增一条历史记录");

            var recordItem = (await History.ReadDayAsync(TodayDate))[^1];

            Assert.True(History.Config.ReadAfterResetTriggerBit, "默认应开启读取后复位握手");
            Assert.Equal(History.Config.DataItemCollection.Count, recordItem.NumericValueCollection.Count);
            Assert.Equal(600d, recordItem.NumericValueCollection[torqueIndex]);
            Assert.Equal("0.600", torqueItem.ShowText(recordItem.NumericValueCollection[torqueIndex]));

            // 握手闭环：读取完成后触发位应被上位机写回 0，
            // PLC 程序据此知道"数据已被取走"，可以开始下一个动作
            await WaitUntilAsync(
                async () =>
                {
                    var Coil = await Client.ReadCoilAsync(triggerBit, 1);
                    return Coil.Length > 0 && !Coil[0];
                },
                "读取完成后触发位应被复位为 0");
        }
        finally
        {
            foreach (var serviceInstance in hosting)
            {
                await serviceInstance.StopAsync(CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// 2026-10-24 现场要求：数据项支持 REAL（32 位浮点）。
    /// <para>把配置临时换成「一列 REAL + 一列 INT」，验证 PLC 侧两个字按配置字序解码后
    /// 直接得到工程量（3.75 就是 3.75，不再需要 /1000 之类的换算）。</para>
    /// </summary>
    [Fact]
    public async Task RealDataItemShouldBy32BitFloatReadToEngineeringValue()
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
        serviceCollection.AddSingleton<IPermissionGuard, AllowAllPermissionGuard>();

        var configBackup = await File.ReadAllTextAsync(ConfigPath.HistoryRecordConfig);

        try
        {
            await using var provider = serviceCollection.BuildServiceProvider(validateScopes: true);
            var hosting = provider.GetServices<IHostedService>().ToList();

            foreach (var serviceInstance in hosting)
            {
                await serviceInstance.StartAsync(CancellationToken.None);
            }

            try
            {
                var History = provider.GetRequiredService<IHistoryRecordService>();
                var Client = provider.GetRequiredService<IModbusClient>();
                var TodayDate = DateTime.Today;

                await History.SaveConfigAsync(new HistoryRecordConfig
                {
                    Title = "浮点记录",
                    TriggerAddress = "M8",
                    Enabled = true,
                    RetainDays = 90,
                    DataItemCollection =
                    [
                        new() { Name = "胶量", Address = "D1420", DataType = TagDataType.Float32, DecimalBit = 3, Unit = "mg" },
                        new() { Name = "颗数", Address = "D1422", DataType = TagDataType.Int16, DecimalBit = 0 },
                    ],
                });

                // 保存配置会重建"上次触发位"基线：先等一轮扫描把 M8=0 记成基线，
                // 再置位才有 0 → 1 的沿（否则第一轮看到的就已经是 1，不会记录）
                await Task.Delay(700);

                await Client.WriteMultiRegisterAsync(
                    1420, RegisterCodec.Encode(TagDataType.Float32, 3.75, WordOrder.LittleEndian));
                await Client.WriteSingleRegisterAsync(1422, 7);

                var previousCount = (await History.ReadDayAsync(TodayDate)).Count;

                await Client.WriteSingleCoilAsync(8, true);

                await WaitUntilAsync(
                    async () => (await History.ReadDayAsync(TodayDate)).Count > previousCount,
                    "浮点数据项的触发位上升沿应新增一条记录");

                var recordItem = (await History.ReadDayAsync(TodayDate))[^1];

                Assert.Equal(3.75, recordItem.NumericValueCollection[0], 3);
                Assert.Equal("3.750", History.Config.DataItemCollection[0].ShowText(recordItem.NumericValueCollection[0]));
                Assert.Equal(7d, recordItem.NumericValueCollection[1]);
            }
            finally
            {
                foreach (var serviceInstance in hosting)
                {
                    await serviceInstance.StopAsync(CancellationToken.None);
                }
            }
        }
        finally
        {
            await File.WriteAllTextAsync(ConfigPath.HistoryRecordConfig, configBackup);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string Description, int TimeoutMilliseconds = 8000)
    {
        var truncateStopTime = Environment.TickCount64 + TimeoutMilliseconds;

        while (Environment.TickCount64 < truncateStopTime)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"等待超时：{Description}");
    }
}
