using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using PlcHmi.Business;
using PlcHmi.Business.Configuration;
using PlcHmi.Business.Services;
using PlcHmi.Communication.Modbus;
using PlcHmi.Communication.Inovance;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;
using PlcHmi.Tests.Fake;

namespace PlcHmi.Tests;

/// <summary>
/// 锁附页「当前浮高」（2026-10-24 现场要求）的回归。
///
/// <para><b>守的是什么：</b>浮高来自一个独立的 REAL 地址（缺省 D240），既不在标签表里、
/// 也不参与轮询。它是手动区显示项与「保存坐标点」的共同来源，因此：<br/>
/// ① 读值按 Float32 两字解码；② 未配置地址 / 读不到时返回 <c>null</c>，绝不返回 0 冒充读数；<br/>
/// ③ 地址可由管理员在「编辑地址」里改，保存后立即生效。</para>
/// </summary>
[Collection("config-files")]
public class CurrentFloatHeightTest
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

    private static PageAddressService NewPageAddressService(IModbusClient Connect)
    {
        var Parser = new InovanceAddressParser(PlcSeries.SmallPlc);

        return new PageAddressService(
            Connect,
            new TagWriter(Connect, new TagRepository(NullLogger<TagRepository>.Instance), Parser,
                NullLogger<TagWriter>.Instance),
            RecipeConfigLoader.Load(out _),
            new AllowAllPermissionGuard(),
            NullLogger<PageAddressService>.Instance);
    }

    [Fact]
    public async Task ShouldBeAbleToReadToRealKindCurrentFloatHeight()
    {
        await using var provider = BuildRuntime();
        var hosting = provider.GetRequiredService<IHostedService>();
        await hosting.StartAsync(CancellationToken.None);

        try
        {
            var Client = provider.GetRequiredService<IModbusClient>();
            var PointTable = provider.GetRequiredService<IPointTableService>();

            await Client.WriteMultiRegisterAsync(
                240, RegisterCodec.Encode(TagDataType.Float32, 3.25, WordOrder.LittleEndian));

            var floatHeight = await PointTable.ReadCurrentFloatHeightAsync("锁附");
            Assert.NotNull(floatHeight);
            Assert.Equal(3.25, floatHeight!.Value, 3);

            // 点胶表没有浮高地址 → null（而不是 0）
            Assert.Null(await PointTable.ReadCurrentFloatHeightAsync("点胶"));
        }
        finally
        {
            await hosting.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// 2026-10-24 现场口径：浮高只属于锁附示教。
    /// <b>点胶示教的「保存坐标点」仍是写当前 X / Y / Z 轴位置</b>，既不读 D240，也不写浮高。
    /// </summary>
    [Fact]
    public async Task DispenseSaveCoordinatePointStillWriteXYZAndNotTouchFloatHeight()
    {
        await using var provider = BuildRuntime();
        var hosting = provider.GetRequiredService<IHostedService>();
        await hosting.StartAsync(CancellationToken.None);

        try
        {
            var Client = provider.GetRequiredService<IModbusClient>();
            var PointTable = provider.GetRequiredService<IPointTableService>();

            // 点胶表没有浮高地址
            Assert.Equal(0, PointTable.Table("点胶").CurrentFloatHeightAddress);
            Assert.Null(PointTable.Table("点胶").FindColumn("浮高"));

            // PLC 里放一个浮高值：点胶示教不应去读它、更不该把它写进点胶点表
            await Client.WriteMultiRegisterAsync(
                240, RegisterCodec.Encode(TagDataType.Float32, 9.5, WordOrder.LittleEndian));

            PointTable.SetPointValue("点胶", 0, "指令码", 5);
            await PointTable.TeachCurrentPositionAsync("点胶", 0);

            // X / Y / Z 三个坐标列照常按当前位置写入（模拟器各轴 0）
            Assert.Equal(0f, await ReadFloatAsync(Client, 2700), 2);    // X
            Assert.Equal(0f, await ReadFloatAsync(Client, 2800), 2);    // Y
            Assert.Equal(0f, await ReadFloatAsync(Client, 2900), 2);    // Z

            // 指令码保留，D240 原封不动
            Assert.Equal(5d, PointTable.CurrentPointTable("点胶")[0].Take("指令码"));
            Assert.Equal(9.5f, await ReadFloatAsync(Client, 240), 2);
        }
        finally
        {
            await hosting.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<float> ReadFloatAsync(IModbusClient Client, int addressValue)
    {
        var registerList = await Client.ReadHoldingRegisterAsync(addressValue, 2);
        return (float)RegisterCodec.Decode(
            TagDataType.Float32, registerList.AsSpan(0, 2), WordOrder.LittleEndian)!;
    }

    /// <summary>
    /// 「编辑地址」里必须有一行可改的「当前浮高」，保存后地址立即换；
    /// 它是固定元件：不可删、类型固定 REAL。
    /// </summary>
    [Fact]
    public async Task CurrentFloatHeightAddressShouldBeAbleToAtEditAddressModifyChange()
    {
        var recipePath = ConfigPath.RecipeConfig;
        var Original = await File.ReadAllTextAsync(recipePath);

        try
        {
            await using var Client = new FakeModbusClient();
            var serviceInstance = NewPageAddressService(Client);

            var ItemCollection = serviceInstance.CollectSetPointTablePage("锁附").ToList();
            var floatHeightItem = ItemCollection.First(Item => Item.Category == "手动区");

            Assert.Equal("当前浮高", floatHeightItem.Key);
            Assert.Equal("D240", floatHeightItem.AddressText);
            Assert.Equal(TagDataType.Float32.ToString(), floatHeightItem.DataTypeText);
            Assert.False(floatHeightItem.Deletable);

            floatHeightItem.AddressText = "D260";

            await serviceInstance.SavePointTablePageAsync("锁附", new AddressEditRequest
            {
                Title = "锁附示教",
                DefaultGroup = "锁附",
                ItemCollection = ItemCollection,
            });

            // 落盘后重新加载：地址已改成 D260
            Assert.Equal(260, RecipeConfigLoader.Load(out _).PointTable.Find("锁附")!.CurrentFloatHeightAddress);
        }
        finally
        {
            await File.WriteAllTextAsync(recipePath, Original);
        }
    }
}
