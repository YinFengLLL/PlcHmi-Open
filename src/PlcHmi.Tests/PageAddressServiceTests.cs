using Microsoft.Extensions.Logging.Abstractions;
using PlcHmi.Business.Configuration;
using PlcHmi.Business.Services;
using PlcHmi.Communication.Inovance;
using PlcHmi.Communication.Simulation;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;
using PlcHmi.Tests.Fake;

namespace PlcHmi.Tests;

/// <summary>
/// 页面地址服务测试。
///
/// <para><b>守的是什么</b>：现场要求"页面上所有与 PLC 交互的元件都能改地址"。
/// 这个服务是唯一的收集 / 保存入口，一旦漏收一类元件，现场就会出现
/// "这个按钮地址改不了，只能去改 JSON"的返工。同时它承担管理员权限边界。</para>
/// </summary>
[Collection("config-files")]
public class PageAddressServiceTest
{
    /// <summary>全部拒绝的权限守卫（仅测试用）。</summary>
    private sealed class RejectAllPermissionGuard : IPermissionGuard
    {
        public void WantRequire(Permission permissionItem) =>
            throw new UnauthorizedAccessException($"测试：缺少「{permissionItem}」权限。");
    }

    private static PageAddressService NewService(IPermissionGuard guard, IModbusClient Connect)
    {
        var Parser = new InovanceAddressParser(PlcSeries.SmallPlc);

        return new PageAddressService(
            Connect,
            new TagWriter(Connect, new TagRepository(NullLogger<TagRepository>.Instance), Parser,
                NullLogger<TagWriter>.Instance),
            RecipeConfigLoader.Load(out _),
            guard,
            NullLogger<PageAddressService>.Instance);
    }

    [Fact]
    public async Task CollectSetScrewPageShouldOverwriteAllFiveCategoryComponent()
    {
        await using var Client = new FakeModbusClient();
        var serviceInstance = NewService(new AllowAllPermissionGuard(), Client);
        var list = serviceInstance.CollectSetPointTablePage("锁附");

        // 点表列：X / Y / 浮高
        var pointTableColumn = list.Where(Item => Item.Category == "点表列").ToList();
        Assert.Equal(3, pointTableColumn.Count);
        Assert.Contains(pointTableColumn, Item => Item.Key == "浮高" && Item.AddressText == "D2620");

        // 动作按钮、点动、回零、辅助地址都要收齐
        Assert.Contains(list, Item => Item.Category == "动作按钮" && Item.Name == "电机复位");
        Assert.Contains(list, Item => Item.Category == "点动" && Item.Key == "X|正转" && Item.AddressText == "M1002");
        Assert.Contains(list, Item => Item.Category == "回零" && Item.Key == "ZZ" && Item.AddressText == "M443");
        Assert.Contains(list, Item => Item.Category == "辅助地址" && Item.Key == "上料点Y" && Item.Deletable);

        // 锁附页只收锁附分组的点动轴（X/Y/Z/ZZ），不收点胶轴
        Assert.DoesNotContain(list, Item => Item.Category == "点动" && Item.Key.StartsWith("GlueX", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CollectSetDispensePageShouldOnlyHasDispenseGroupAxis()
    {
        await using var Client = new FakeModbusClient();
        var serviceInstance = NewService(new AllowAllPermissionGuard(), Client);
        var list = serviceInstance.CollectSetPointTablePage("点胶");

        var jogAxis = list
            .Where(Item => Item.Category == "点动")
            .Select(Item => Item.Key.Split('|')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 点胶 X / Y（与锁附 Y 共轴）/ Z
        Assert.Equal(3, jogAxis.Count);
        Assert.Contains("GlueX", jogAxis);
        Assert.Contains("Y", jogAxis);
        Assert.Contains("GlueZ", jogAxis);

        // 指令码列也在点表列里
        Assert.Contains(list, Item => Item.Category == "点表列" && Item.Key == "指令码" && Item.AddressText == "D3400");
    }

    [Fact]
    public async Task CollectSetCalibrationPageShouldContainCenterOffsetAndCalibrationStart()
    {
        await using var Client = new FakeModbusClient();
        var serviceInstance = NewService(new AllowAllPermissionGuard(), Client);
        var list = serviceInstance.CollectSetCalibrationPage();

        // 地址对齐现场稳定版（2026-11-10 现场配置为准）：上相机 X/Y/Z = D1650 / D1652 / D1554、
        // 偏移 D1654、启动 M84（现场把标定启动从 M82 挪到 M84，M83 留给下相机）
        Assert.Contains(list, Item => Item.Category == "标定字段" && Item.Key == "X" && Item.AddressText == "D1650");
        Assert.Contains(list, Item => Item.Category == "标定字段" && Item.Key == "Y" && Item.AddressText == "D1652");
        Assert.Contains(list, Item => Item.Category == "标定字段" && Item.Key == "Z" && Item.AddressText == "D1554");
        Assert.Contains(list, Item => Item.Category == "标定字段" && Item.Key == "偏移量" && Item.AddressText == "D1654");
        Assert.Contains(list, Item => Item.Category == "标定按钮" && Item.Key == "启动" && Item.AddressText == "M84");

        // 固定标定按钮不再有「移至指定位置」（现场改用「标定」分组的可配置动作按钮）
        Assert.DoesNotContain(list, Item => Item.Category == "标定按钮" && Item.Name.Contains("移至", StringComparison.Ordinal));
    }

    /// <summary>
    /// 下相机（2026-10-17 新增）：地址处理模式与上相机一致，但**只有 X / Z，没有 Y**。
    /// <para>键必须带「下相机」前缀——编辑器的行按「类别 + 键」定位，
    /// 与上相机的 X / Z 重键会把两套地址写串（改下相机会把上相机改掉）。</para>
    /// </summary>
    [Fact]
    public async Task CollectSetCalibrationPageShouldContainBottomCameraThreeItemAndNoHasY()
    {
        await using var Client = new FakeModbusClient();
        var serviceInstance = NewService(new AllowAllPermissionGuard(), Client);
        var list = serviceInstance.CollectSetCalibrationPage();

        // 现场配置已带下相机 X/Z（2026-11-10 现场稳定版）：X=D1660 / Z=D1556、偏移沿用 D1664、启动 M83
        Assert.Contains(list, Item => Item.Category == "标定字段" && Item.Key == "下相机X" && Item.AddressText == "D1660");
        Assert.Contains(list, Item => Item.Category == "标定字段" && Item.Key == "下相机Z" && Item.AddressText == "D1556");
        Assert.Contains(list, Item => Item.Category == "标定字段" && Item.Key == "下相机偏移量" && Item.AddressText == "D1664");
        Assert.Contains(list, Item => Item.Category == "标定按钮" && Item.Key == "下相机启动" && Item.AddressText == "M83");

        // 下相机没有 Y：不得出现「下相机Y」这一行
        Assert.DoesNotContain(list, Item => Item.Key.Contains("下相机Y", StringComparison.Ordinal));
    }

    /// <summary>
    /// 出厂配置自检：下相机只有 X / Z（<b>没有 Y</b>），且全部标定字段地址互不重叠。
    /// <para>Float32 每项占 2 字：上相机 3600/3602/3604/3606（各 2 字）之后，
    /// 下相机从 3608 起排——一旦地址写重叠，两个字段会互相覆盖寄存器（值看着像"随机跳"）。</para>
    /// </summary>
    [Fact]
    public void FactoryConfig_BottomCameraShouldOnlyHasXAndZAndAddressNotAndTopCameraOverlap()
    {
        var configItem = RecipeConfigLoader.Load(out _).Calibration;

        Assert.Equal(2, configItem.BottomCameraCenter.Count);
        Assert.Equal(["X", "Z"], configItem.BottomCameraCenter.Select(fieldItem => fieldItem.Key).ToArray());
        Assert.Null(configItem.FindBottomCamera("Y"));   // 下相机没有 Y

        // 全部标定字段（上相机 X/Y/Z + 上相机偏移 + 下相机 X/Z + 下相机偏移）两两不重叠
        var areaBetweenList = configItem.Center
            .Concat([configItem.PerPointOffset])
            .Concat(configItem.BottomCameraCenter)
            .Concat([configItem.BottomCameraPerPointOffset])
            .Select(fieldItem => (fieldItem.DisplayName, begin: fieldItem.BaseAddress, stop: fieldItem.BaseAddress + fieldItem.RegisterCount - 1))
            .ToList();

        for (var item = 0; item < areaBetweenList.Count; item++)
        {
            for (var item2 = item + 1; item2 < areaBetweenList.Count; item2++)
            {
                var commitCross = areaBetweenList[item].begin <= areaBetweenList[item2].stop && areaBetweenList[item2].begin <= areaBetweenList[item].stop;

                Assert.False(commitCross,
                    $"{areaBetweenList[item].DisplayName}(D{areaBetweenList[item].begin}) 与 {areaBetweenList[item2].DisplayName}(D{areaBetweenList[item2].begin}) 地址重叠");
            }
        }

        // 两个启动按钮也必须是不同的 M 位（同址会同时触发两台相机的标定）
        Assert.NotEqual(configItem.StartAddress, configItem.BottomCameraStartAddress);
    }

    /// <summary>
    /// 改下相机地址不得动上相机：两条地址链共用一个编辑器，
    /// 键前缀写错就会"改下相机、上相机跟着变"（数据串位，现场极难查）。
    /// </summary>
    [Fact]
    public async Task SaveCalibrationPage_BottomCameraAddressMustNotWriteSerialToTopCamera()
    {
        var recipePath = ConfigPath.RecipeConfig;
        var Original = await File.ReadAllTextAsync(recipePath);

        try
        {
            await using var Client = new FakeModbusClient();
            var serviceInstance = NewService(new AllowAllPermissionGuard(), Client);

            var ItemCollection = serviceInstance.CollectSetCalibrationPage().ToList();

            // 3748 / 3750：避开点胶结构体数组（D3500–D3739）与其它点（3760 起）
            ItemCollection.First(Item => Item.Key == "下相机X").AddressText = "D3748";
            ItemCollection.First(Item => Item.Key == "下相机偏移量").AddressText = "D3750";
            ItemCollection.First(Item => Item.Key == "下相机启动").AddressText = "M430";

            await serviceInstance.SaveCalibrationPageAsync(new AddressEditRequest
            {
                Title = "标定 · 其它点",
                ItemCollection = ItemCollection,
            });

            // 从磁盘重新加载：同时验证"已落盘"（服务内部快照是另一个对象）
            var configItem = RecipeConfigLoader.Load(out _).Calibration;
            Assert.Equal(3748, configItem.FindBottomCamera("X")!.BaseAddress);
            Assert.Equal(3750, configItem.BottomCameraPerPointOffset.BaseAddress);
            Assert.Equal("M430", configItem.BottomCameraStartAddress);

            // 上相机三项必须原封不动（现场地址：X=D1650、偏移=D1654、启动 M84）
            Assert.Equal(1650, configItem.FindCenter("X")!.BaseAddress);
            Assert.Equal(1654, configItem.PerPointOffset.BaseAddress);
            Assert.Equal("M84", configItem.StartAddress);
        }
        finally
        {
            await File.WriteAllTextAsync(recipePath, Original);
        }
    }

    [Fact]
    public async Task NonAdminSaveShouldBeRejected()
    {
        await using var Client = new FakeModbusClient();
        var serviceInstance = NewService(new RejectAllPermissionGuard(), Client);
        var Request = new AddressEditRequest
        {
            Title = "锁附示教",
            ItemCollection = serviceInstance.CollectSetPointTablePage("锁附").ToList(),
        };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => serviceInstance.SavePointTablePageAsync("锁附", Request));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => serviceInstance.SaveCalibrationPageAsync(Request));
    }

    [Fact]
    public async Task SavePointTablePageShouldWriteBackPointTableColumnAndAuxAddress()
    {
        var recipePath = ConfigPath.RecipeConfig;
        var Original = await File.ReadAllTextAsync(recipePath);

        try
        {
            await using var Client = new FakeModbusClient();
            var serviceInstance = NewService(new AllowAllPermissionGuard(), Client);
            var Request = new AddressEditRequest
            {
                Title = "锁附示教",
                DefaultGroup = "锁附",
                ItemCollection = serviceInstance.CollectSetPointTablePage("锁附").ToList(),
            };

            var xColumn = Request.ItemCollection.First(Item => Item.Category == "点表列" && Item.Key == "X");
            xColumn.AddressText = "D2450";

            var onMaterialPointY = Request.ItemCollection.First(Item => Item.Category == "辅助地址" && Item.Key == "上料点Y");
            onMaterialPointY.AddressText = "D2684";

            // 删除一个辅助地址
            var pendingDelete = Request.ItemCollection.First(Item => Item.Category == "辅助地址" && Item.Key == "辅助地址11");
            Request.ItemCollection.Remove(pendingDelete);
            Request.DeletedItem.Add(pendingDelete);

            await serviceInstance.SavePointTablePageAsync("锁附", Request);

            var Text = await File.ReadAllTextAsync(recipePath);
            Assert.Contains("2450", Text, StringComparison.Ordinal);
            Assert.Contains("D2684", Text, StringComparison.Ordinal);
            Assert.DoesNotContain("辅助地址11", Text, StringComparison.Ordinal);
        }
        finally
        {
            await File.WriteAllTextAsync(recipePath, Original);
        }
    }

    [Fact]
    public async Task SavePointTablePageShouldBeAbleToAddAndDeleteActionButton()
    {
        var recipePath = ConfigPath.RecipeConfig;
        var Original = await File.ReadAllTextAsync(recipePath);

        try
        {
            await using var Client = new FakeModbusClient();
            var serviceInstance = NewService(new AllowAllPermissionGuard(), Client);
            var Request = new AddressEditRequest
            {
                Title = "锁附示教",
                DefaultGroup = "锁附",
                ItemCollection = serviceInstance.CollectSetPointTablePage("锁附").ToList(),
            };

            Request.ItemCollection.Add(new AddressEditItem
            {
                Category = "动作按钮",
                Key = "锁附-自定义动作",
                Name = "自定义动作",
                AddressText = "M499",
                DataTypeText = "布尔",
                Group = "锁附",
                Deletable = true,
                Insert = true,
            });

            var pendingDelete = Request.ItemCollection.First(Item => Item.Category == "动作按钮" && Item.Key == "锁附-电批启动");
            Request.ItemCollection.Remove(pendingDelete);
            Request.DeletedItem.Add(pendingDelete);

            await serviceInstance.SavePointTablePageAsync("锁附", Request);

            var Config = RecipeConfigLoader.Load(out _);
            var newButton = Config.ActionButton.FirstOrDefault(Item => Item.Key == "锁附-自定义动作");
            Assert.NotNull(newButton);
            Assert.Equal("M499", newButton!.Address);
            Assert.Equal("锁附", newButton.Group);
            Assert.DoesNotContain(Config.ActionButton, Item => Item.Key == "锁附-电批启动");
        }
        finally
        {
            await File.WriteAllTextAsync(recipePath, Original);
        }
    }

    [Fact]
    public async Task CalibrationPageShouldBeAbleToAddActionButtonAndAuxAddress()
    {
        var recipePath = ConfigPath.RecipeConfig;
        var Original = await File.ReadAllTextAsync(recipePath);

        try
        {
            await using var Client = new FakeModbusClient();
            var serviceInstance = NewService(new AllowAllPermissionGuard(), Client);

            var Request = new AddressEditRequest
            {
                Title = "标定 · 其它点",
                DefaultGroup = "标定",
                ItemCollection = serviceInstance.CollectSetCalibrationPage().ToList(),
            };

            Request.ItemCollection.Add(new AddressEditItem
            {
                Category = "动作按钮",
                Key = "标定-确认",
                Name = "标定确认",
                AddressText = "M498",
                DataTypeText = "布尔",
                Group = "标定",
                Deletable = true,
                Insert = true,
            });

            Request.ItemCollection.Add(new AddressEditItem
            {
                Category = "辅助地址",
                Key = "标定-中心偏差",
                Name = "中心偏差",
                AddressText = "D3748",
                DataTypeText = "Float32",
                Group = "标定",
                Deletable = true,
                Insert = true,
            });

            await serviceInstance.SaveCalibrationPageAsync(Request);

            var Config = RecipeConfigLoader.Load(out _);
            Assert.Contains(Config.ActionButton, Item => Item.Key == "标定-确认" && Item.Address == "M498" && Item.Group == "标定");
            Assert.Contains(Config.AuxAddress, Item => Item.Key == "标定-中心偏差" && Item.Address == "D3748");

            // 保存后应能被重新收集（页面刷新依据）
            Assert.Contains(serviceInstance.CollectSetCalibrationPage(), Item => Item.Category == "动作按钮" && Item.Key == "标定-确认");
        }
        finally
        {
            await File.WriteAllTextAsync(recipePath, Original);
        }
    }

    [Fact]
    public async Task AuxAddressShouldBeAbleToWriteAndReadBack()
    {
        await using var Device = new VirtualPlcDevice(NullLogger<VirtualPlcDevice>.Instance);
        await using var Client = new SimulationModbusClient(Device, NullLogger<SimulationModbusClient>.Instance);
        await Client.ConnectAsync();

        var serviceInstance = NewService(new AllowAllPermissionGuard(), Client);
        var Item = serviceInstance.AuxAddress("锁附").First(item => item.Key == "上料点Y");

        await serviceInstance.WriteAuxAddressAsync(Item, 123.45);

        var ReadBack = await serviceInstance.ReadAuxAddressAsync(Item);
        Assert.Equal(123.45, ReadBack, 2);
    }

    [Fact]
    public async Task DisconnectedWhenReadAuxAddressShouldReturnZero()
    {
        await using var Client = new FakeModbusClient();
        var serviceInstance = NewService(new AllowAllPermissionGuard(), Client);
        var Item = serviceInstance.AuxAddress("锁附").First(item => item.Key == "上料点Y");

        Assert.Equal(0, await serviceInstance.ReadAuxAddressAsync(Item));
    }
}
