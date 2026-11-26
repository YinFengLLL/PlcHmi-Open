using Microsoft.Extensions.Logging.Abstractions;
using PlcHmi.Business.Configuration;
using PlcHmi.Business.Services;
using PlcHmi.Communication.Inovance;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;
using PlcHmi.Tests.Fake;

namespace PlcHmi.Tests;

/// <summary>
/// 示教页顶部固定状态灯（各轴回原状态）的配置与校验测试。
///
/// <para><b>2026-10-16 现场要求：</b>锁附示教 / 点胶示教页各放一盏固定状态灯，读指定 M 区位
/// （0 = 各轴未回原 → 闪烁；1 = 各轴回原完成 → 常亮），地址由管理员在「编辑地址」里改。
/// 本组钉住三件事：<b>老配置升级后灯仍在</b>（缺条目按缺省地址合成）、
/// <b>地址能被管理员改并落盘</b>、<b>写成 D 区必须被拒绝</b>（灯用 FC01 读位，D 区是字）。</para>
/// </summary>
[Collection("config-files")]
public class StatusLampConfigTest
{
    private static PageAddressService NewService(RecipeConfig configItem, IModbusClient Connect)
    {
        var Parser = new InovanceAddressParser(PlcSeries.SmallPlc);

        return new PageAddressService(
            Connect,
            new TagWriter(Connect, new TagRepository(NullLogger<TagRepository>.Instance), Parser,
                NullLogger<TagWriter>.Instance),
            configItem,
            new AllowAllPermissionGuard(),
            NullLogger<PageAddressService>.Instance);
    }

    [Fact]
    public async Task ConfigMissingEntryWhenShouldByDefaultAddressValidAsStatusLamp()
    {
        await using var Client = new FakeModbusClient();

        // 空配置 = 现场升级后 recipe-config.json 里还没有"状态灯"段
        var serviceInstance = NewService(new RecipeConfig(), Client);

        var list = serviceInstance.StatusLamp("锁附");

        var lampItem = Assert.Single(list);
        Assert.Equal("回原状态灯", lampItem.Key);
        Assert.Equal("锁附", lampItem.Group);
        Assert.Equal(StatusLampItem.defaultAddress, lampItem.Address);
    }

    [Fact]
    public async Task CollectSetScrewPageShouldContainStatusLampEntryAndNotDeletable()
    {
        await using var Client = new FakeModbusClient();
        var serviceInstance = NewService(RecipeConfigLoader.Load(out _), Client);

        var list = serviceInstance.CollectSetPointTablePage("锁附");

        var lampEntry = Assert.Single(list, Item => Item.Category == "状态灯");
        Assert.Equal("M430", lampEntry.AddressText);
        Assert.Equal(TagDataType.Boolean.ToString(), lampEntry.DataTypeText);

        // 固定元件：可改地址不可删（删了页面就没有灯，现场也不知道该找谁）
        Assert.False(lampEntry.Deletable);
    }

    [Fact]
    public async Task DispensePageStatusLampShouldAndScrewPageEachIndependent()
    {
        await using var Client = new FakeModbusClient();
        var serviceInstance = NewService(RecipeConfigLoader.Load(out _), Client);

        Assert.Single(serviceInstance.StatusLamp("锁附"));
        Assert.Single(serviceInstance.StatusLamp("点胶"));

        // 点胶页不应混入锁附分组的条目（分组过滤生效）
        Assert.All(serviceInstance.StatusLamp("点胶"), lampItem => Assert.Equal("点胶", lampItem.Group));
    }

    [Fact]
    public async Task SavePointTablePageShouldWriteBackStatusLampAddressAndByMAreaRuleRangeConvert()
    {
        var recipePath = ConfigPath.RecipeConfig;
        var Original = await File.ReadAllTextAsync(recipePath);

        try
        {
            await using var Client = new FakeModbusClient();
            var serviceInstance = NewService(RecipeConfigLoader.Load(out _), Client);

            var Request = new AddressEditRequest
            {
                Title = "锁附示教",
                DefaultGroup = "锁附",
                ItemCollection = serviceInstance.CollectSetPointTablePage("锁附").ToList(),
            };

            // 小写 + 无前缀规范化：现场手输时大小写随意，落盘必须统一成 M 区大写
            Request.ItemCollection.First(Item => Item.Category == "状态灯").AddressText = "m466";

            await serviceInstance.SavePointTablePageAsync("锁附", Request);

            var Config = RecipeConfigLoader.Load(out _);
            var lampItem = Config.StatusLamp.First(Item => Item.Group == "锁附");
            Assert.Equal("M466", lampItem.Address);

            // 点胶分组不受影响（现场两页都用 M430）
            Assert.Equal("M430", Config.StatusLamp.First(Item => Item.Group == "点胶").Address);
        }
        finally
        {
            await File.WriteAllTextAsync(recipePath, Original);
        }
    }

    [Fact]
    public async Task SavePointTablePageWhenStatusLampWriteAsDAreaShouldBeRejected()
    {
        var recipePath = ConfigPath.RecipeConfig;
        var Original = await File.ReadAllTextAsync(recipePath);

        try
        {
            await using var Client = new FakeModbusClient();
            var serviceInstance = NewService(RecipeConfigLoader.Load(out _), Client);

            var Request = new AddressEditRequest
            {
                Title = "锁附示教",
                DefaultGroup = "锁附",
                ItemCollection = serviceInstance.CollectSetPointTablePage("锁附").ToList(),
            };

            // D 区是字元件：灯用 FC01 读位，写错区会"能存但读错"，必须当场拒绝
            Request.ItemCollection.First(Item => Item.Category == "状态灯").AddressText = "D500";

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => serviceInstance.SavePointTablePageAsync("锁附", Request));

            Assert.Contains("M 区", exception.Message, StringComparison.Ordinal);

            // 拒绝时不得改到磁盘上的配置
            Assert.Equal(Original, await File.ReadAllTextAsync(recipePath));
        }
        finally
        {
            await File.WriteAllTextAsync(recipePath, Original);
        }
    }
}
