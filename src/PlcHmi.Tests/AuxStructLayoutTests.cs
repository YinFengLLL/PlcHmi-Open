using PlcHmi.Business.Configuration;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Tests;

/// <summary>
/// 点胶单点参数结构体的成员排布（2026-10-21 现场口径）。
///
/// <para><b>守的是什么：</b>"改一个成员的数据类型，后面的成员地址必须依次前移"。
/// 现场把抬针高度从 REAL 改成 INT 后，点胶速度应落在 D3505——若偏移不重排，
/// 上位机会看着 D3506（PLC 实际那里已是别的字），示教写下去的数值全部错位。</para>
/// </summary>
public class AuxAddressStructureLayoutTest
{
    private static AuxAddressItem Member(string Key, TagDataType Kind, int Offset) => new()
    {
        Key = Key,
        Name = Key,
        Group = "点胶",
        DataType = Kind,
        StructMember = true,
        StructBaseAddress = 3500,
        StructInnerOffset = Offset,
        StructLength = 8,
    };

    private static List<AuxAddressItem> FactoryFiveMember() =>
    [
        Member("点胶-提前关胶", TagDataType.Float32, 0),
        Member("点胶-运动速度延时", TagDataType.Int16, 2),
        Member("点胶-关胶后延时", TagDataType.Int16, 3),
        Member("点胶-抬针高度", TagDataType.Float32, 4),
        Member("点胶-点胶速度", TagDataType.Float32, 6),
    ];

    [Fact]
    public void FactoryFiveMemberShouldArrangeAsPackedOffsetAndLengthIsEight()
    {
        var list = FactoryFiveMember();

        var length = AuxAddressStructureLayout.ReLayout(list, "点胶");

        Assert.Equal(8, length);
        Assert.Equal([0, 2, 3, 4, 6], list.Select(Item => Item.StructInnerOffset).ToArray());
        Assert.All(list, Item => Assert.Equal(8, Item.StructLength));
        Assert.All(list, Item => Assert.Equal(3500, Item.StructBaseAddress));
        Assert.Equal("D3506", list[4].Address);
    }

    /// <summary>现场报障的原样复现：抬针高度 REAL → INT16，点胶速度必须从 D3506 前移到 D3505。</summary>
    [Fact]
    public void NeedleLiftHeightChangeINT16AfterDispenseSpeedShouldBeforeMoveToD3505()
    {
        var list = FactoryFiveMember();
        list[3].DataType = TagDataType.Int16;

        var length = AuxAddressStructureLayout.ReLayout(list, "点胶");

        Assert.Equal(7, length);
        Assert.Equal(4, list[3].StructInnerOffset);            // 抬针高度：INT 占 1 字
        Assert.Equal("D3504", list[3].Address);
        Assert.Equal(5, list[4].StructInnerOffset);            // 点胶速度：前移到 5
        Assert.Equal("D3505", list[4].Address);              // ← 现场必须看到的地址
        Assert.All(list, Item => Assert.Equal(7, Item.StructLength));
    }

    [Fact]
    public void CancelCheckedMemberShouldNotAndLayout()
    {
        var list = FactoryFiveMember();
        list[3].StructMember = false;   // 抬针高度退出结构体

        var length = AuxAddressStructureLayout.ReLayout(list, "点胶");

        // 提前关胶 2 + 延时 1 + 关胶后延时 1 + 点胶速度 2 = 6
        Assert.Equal(6, length);
        Assert.Equal(4, list[4].StructInnerOffset);
        Assert.Equal("D3504", list[4].Address);
        Assert.Equal(4, list[3].StructInnerOffset);   // 非成员不被改动（原值保留）
    }

    [Fact]
    public void NoHasBaseAddressNewMemberShouldByDefaultBaseAddressAtBit()
    {
        var list = FactoryFiveMember();

        foreach (var Item in list)
        {
            Item.StructBaseAddress = 0;
        }

        var length = AuxAddressStructureLayout.ReLayout(list, "点胶");

        Assert.Equal(8, length);
        Assert.All(list, Item => Assert.Equal(AuxAddressStructureLayout.defaultBaseAddress, Item.StructBaseAddress));
        Assert.Equal("D3500", list[0].Address);
    }

    [Fact]
    public void OtherGroupMemberNotAffectedByAffectImpact()
    {
        var screwItem = new AuxAddressItem
        {
            Key = "锁附-某个结构体成员",
            Group = "锁附",
            DataType = TagDataType.Int16,
            StructMember = true,
            StructBaseAddress = 2000,
            StructInnerOffset = 9,
            StructLength = 40,
        };

        var list = FactoryFiveMember();
        list.Add(screwItem);

        AuxAddressStructureLayout.ReLayout(list, "点胶");

        Assert.Equal(2000, screwItem.StructBaseAddress);
        Assert.Equal(9, screwItem.StructInnerOffset);
        Assert.Equal(40, screwItem.StructLength);
    }

    // ── 点胶「参数模式」固定按钮（M5006）────────────────────────────────

    [Fact]
    public void DefaultConfigShouldFillOutParameterModeFixedButtonAndCanDuplicateInvoke()
    {
        var Config = new RecipeConfig();

        Assert.Equal(1, ParameterModeButtonDefault.App(Config));

        var Item = Config.ActionButton[0];
        Assert.True(Item.Fixed);
        Assert.Equal("点胶", Item.Group);
        Assert.Equal("M5006", Item.Address);
        Assert.Equal("默认参数", Item.StateText(false));
        Assert.Equal("独立参数", Item.StateText(true));

        // 幂等：已有固定按钮时不重复添加
        Assert.Equal(0, ParameterModeButtonDefault.App(Config));
        Assert.Single(Config.ActionButton);
    }
}
