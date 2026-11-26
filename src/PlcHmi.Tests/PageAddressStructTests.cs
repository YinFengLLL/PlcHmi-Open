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
/// 页面地址编辑的<b>结构体成员</b>部分（2026-10-21）：勾选"结构体成员"后保存，
/// 成员偏移与结构体长度必须按成员顺序与数据类型重排，页面上的地址提示随之更新。
///
/// <para>单独一个文件：<see cref="PageAddressServiceTest"/> 已接近 400 行规模门禁。</para>
/// </summary>
[Collection("config-files")]
public class PageAddressStructMemberTest
{
    private static PageAddressService NewService(IModbusClient Connect) =>
        new(Connect,
            new TagWriter(Connect, new TagRepository(NullLogger<TagRepository>.Instance),
                new InovanceAddressParser(PlcSeries.SmallPlc), NullLogger<TagWriter>.Instance),
            RecipeConfigLoader.Load(out _),
            new AllowAllPermissionGuard(),
            NullLogger<PageAddressService>.Instance);

    /// <summary>
    /// 现场要求：点胶动作区首个按钮固定为「参数模式」（M5006，0 默认参数 / 1 独立参数），
    /// 与其他动作按钮同风格，地址可在「编辑地址」里改，但不能删除。
    /// </summary>
    [Fact]
    public async Task CollectSetDispensePageShouldParameterModeFixedButtonArrangeAtActionAreaFirstBit()
    {
        await using var Client = new FakeModbusClient();
        var serviceInstance = NewService(Client);

        var action = serviceInstance.CollectSetPointTablePage("点胶").Where(Item => Item.Category == "动作按钮").ToList();

        Assert.NotEmpty(action);
        Assert.Equal("点胶-参数模式", action[0].Key);
        Assert.Equal("M5008", action[0].AddressText);   // 现场稳定版（2026-11-10）把参数模式挪到 M5008
        Assert.False(action[0].Deletable);                        // 固定元件：不可删
        Assert.All(action.Skip(1), Item => Assert.True(Item.Deletable));

        // 服务层排序同样把固定按钮放最前
        Assert.Equal("点胶-参数模式", serviceInstance.ActionButton("点胶")[0].Key);

        // 锁附页没有这个按钮
        Assert.DoesNotContain(
            serviceInstance.CollectSetPointTablePage("锁附"),
            Item => Item.Category == "动作按钮" && Item.Key == "点胶-参数模式");
    }

    /// <summary>
    /// 现场报障：把「抬针高度」数据类型改成 INT16 后，后面的「点胶速度」必须从 D3506 前移到 D3505，
    /// 结构体长度从 8 变 7；页面重新收集时显示的地址同样要跟上。
    /// </summary>
    [Fact]
    public async Task SavePointTablePageShouldRearrangeStructMemberOffsetAndLength()
    {
        var recipePath = ConfigPath.RecipeConfig;
        var Original = await File.ReadAllTextAsync(recipePath);

        try
        {
            await using var Client = new FakeModbusClient();
            var serviceInstance = NewService(Client);

            var ItemCollection = serviceInstance.CollectSetPointTablePage("点胶").ToList();
            var needleLift = ItemCollection.First(Item => Item.Category == "辅助地址" && Item.Key == "点胶-抬针高度");

            Assert.True(needleLift.StructMember);
            Assert.True(needleLift.SettableStructMember);
            Assert.Equal("D3504", needleLift.AddressText);

            needleLift.DataTypeText = "Int16";   // REAL(2 字) → INT(1 字)

            await serviceInstance.SavePointTablePageAsync("点胶", new AddressEditRequest
            {
                Title = "点胶示教",
                DefaultGroup = "点胶",
                ItemCollection = ItemCollection,
            });

            var Config = RecipeConfigLoader.Load(out _);
            var needleLiftHeight = Config.AuxAddress.First(Item => Item.Key == "点胶-抬针高度");
            var dispenseSpeed = Config.AuxAddress.First(Item => Item.Key == "点胶-点胶速度");

            Assert.Equal(TagDataType.Int16, needleLiftHeight.DataType);
            Assert.Equal(4, needleLiftHeight.StructInnerOffset);
            Assert.Equal("D3504", needleLiftHeight.Address);

            Assert.Equal(5, dispenseSpeed.StructInnerOffset);   // ← 前移一个字
            Assert.Equal("D3505", dispenseSpeed.Address);     // ← 界面必须显示这个
            Assert.Equal(7, dispenseSpeed.StructLength);     // ← 长度从 8 变 7

            // 页面重新收集（保存后刷新依据）也要拿到新地址
            var reCollectSet = serviceInstance.CollectSetPointTablePage("点胶").First(Item => Item.Key == "点胶-点胶速度");
            Assert.Equal("D3505", reCollectSet.AddressText);
        }
        finally
        {
            await File.WriteAllTextAsync(recipePath, Original);
        }
    }

    /// <summary>
    /// 取消勾选「结构体成员」后：结构体三参数清零、其余成员重排；
    /// 重新加载时不得被"老配置补齐"逻辑又认回成员（否则勾选框自己变回去）。
    /// </summary>
    [Fact]
    public async Task CancelStructMemberSaveAfterShouldNotBeLegacyConfigFillRestore()
    {
        var recipePath = ConfigPath.RecipeConfig;
        var Original = await File.ReadAllTextAsync(recipePath);

        try
        {
            await using var Client = new FakeModbusClient();
            var serviceInstance = NewService(Client);

            var ItemCollection = serviceInstance.CollectSetPointTablePage("点胶").ToList();
            var needleLift = ItemCollection.First(Item => Item.Category == "辅助地址" && Item.Key == "点胶-抬针高度");
            needleLift.StructMember = false;
            needleLift.AddressText = "D3760";   // 退出结构体后用自己的固定地址

            await serviceInstance.SavePointTablePageAsync("点胶", new AddressEditRequest
            {
                Title = "点胶示教",
                DefaultGroup = "点胶",
                ItemCollection = ItemCollection,
            });

            var Config = RecipeConfigLoader.Load(out _);   // 走一遍加载（含老配置补齐）
            var needleLiftHeight = Config.AuxAddress.First(Item => Item.Key == "点胶-抬针高度");
            var dispenseSpeed = Config.AuxAddress.First(Item => Item.Key == "点胶-点胶速度");

            Assert.False(needleLiftHeight.StructMember);
            Assert.Equal(0, needleLiftHeight.StructBaseAddress);
            Assert.Equal(0, needleLiftHeight.StructLength);
            Assert.Equal("D3760", needleLiftHeight.Address);

            // 少了一个 2 字成员：长度 2+1+1+2 = 6，点胶速度回到偏移 4
            Assert.Equal(4, dispenseSpeed.StructInnerOffset);
            Assert.Equal(6, dispenseSpeed.StructLength);
        }
        finally
        {
            await File.WriteAllTextAsync(recipePath, Original);
        }
    }
}
