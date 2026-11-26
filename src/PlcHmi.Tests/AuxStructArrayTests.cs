using PlcHmi.Business.Configuration;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Tests;

/// <summary>
/// 点胶「单点工艺参数」结构体数组回归（2026-10-21 现场要求）。
///
/// <para><b>现场口径：</b>每个点有一份工艺参数（提前关胶 / 移动前出胶延时 / 关胶后延时 /
/// 抬针高度 / 点胶速度），PLC 里是一个结构体数组：
/// 第 n 点（0 基）的字段地址 = 结构体基址 + n × 结构体长度 + 结构体内偏移。
/// 结构体 8 字：第 1 点 D3500–D3507、第 2 点 D3508–D3515……</para>
///
/// <para><b>为什么要单测：</b>这套寻址一旦算错，改第 3 点会悄悄改到第 2 点（或压到相邻字段），
/// 现场表现是"点胶参数偶发不对"，而数据本身看起来完全正常——最难查的一类。</para>
/// </summary>
[Collection("config-files")]
public class AuxAddressStructArrayTest
{
    /// <summary>按现场表构造：偏移 0/2/3/4/6，长度 8。</summary>
    private static AuxAddressItem BuildItem(string Name, int Offset, TagDataType Kind) => new()
    {
        Key = Name,
        Name = Name,
        Group = "点胶",
        DataType = Kind,
        StructMember = true,
        StructBaseAddress = 3500,
        StructInnerOffset = Offset,
        StructLength = 8,
    };

    [Fact]
    public void ByPointAddress_No1PointShouldFromBaseAddressStart()
    {
        var Item = BuildItem("提前关胶", 0, TagDataType.Float32);

        Assert.True(Item.IsStructArray);
        Assert.Equal("D3500", Item.ByPointAddress(0));
        Assert.Equal(3500, Item.ByPointIndex(0));
    }

    /// <summary>现场原话：选中第二点后，提前关胶从 D3500 到 D3508。</summary>
    [Fact]
    public void ByPointAddress_NoTwoPointShouldOffsetOneStructLength()
    {
        var Item = BuildItem("提前关胶", 0, TagDataType.Float32);

        Assert.Equal("D3508", Item.ByPointAddress(1));
        Assert.Equal("D3516", Item.ByPointAddress(2));
    }

    /// <summary>每个字段在自己的偏移上：D3502/D3503/D3504/D3506（长 8 字，不互相压）。</summary>
    [Fact]
    public void ByPointAddress_EachFieldShouldPersistAtSelfOffsetOn()
    {
        Assert.Equal("D3502", BuildItem("移动前出胶延时", 2, TagDataType.Int16).ByPointAddress(0));
        Assert.Equal("D3503", BuildItem("关胶后延时", 3, TagDataType.Int16).ByPointAddress(0));
        Assert.Equal("D3504", BuildItem("抬针高度", 4, TagDataType.Float32).ByPointAddress(0));
        Assert.Equal("D3506", BuildItem("点胶速度", 6, TagDataType.Float32).ByPointAddress(0));

        // 第 2 点：整体 +8
        Assert.Equal("D3510", BuildItem("移动前出胶延时", 2, TagDataType.Int16).ByPointAddress(1));
        Assert.Equal("D3514", BuildItem("点胶速度", 6, TagDataType.Float32).ByPointAddress(1));
    }

    /// <summary>非数组模式（锁附页的拍照点等）必须仍按固定地址走，不能被动到。</summary>
    [Fact]
    public void NonArrayMode_ShouldBackToFixedAddress()
    {
        var Item = new AuxAddressItem { Key = "拍照点X", Name = "拍照点 X", Address = "D2680", DataType = TagDataType.Float32 };

        Assert.False(Item.IsStructArray);
        Assert.Equal("D2680", Item.ByPointAddress(0));
        Assert.Equal("D2680", Item.ByPointAddress(17));   // 与点序号无关
        Assert.Equal(2680, Item.ByPointIndex(5));
    }

    /// <summary>点序号为负（未选中时的哨兵值）按 0 处理，不能算出区段外的地址。</summary>
    [Fact]
    public void NegativePointIndexShouldByNoOnePointProcess()
    {
        var Item = BuildItem("提前关胶", 0, TagDataType.Float32);

        Assert.Equal("D3500", Item.ByPointAddress(-1));
        Assert.Equal(3500, Item.ByPointIndex(-3));
    }

    // ── 出厂配置自检 ────────────────────────────────────────────────────

    /// <summary>
    /// 出厂配置里点胶的 5 个单点参数必须共用同一套结构体（基址 3500 / 长度 8），
    /// 且字段区间互不重叠、总长不超过结构体长度。
    /// </summary>
    [Fact]
    public void FactoryConfig_DispenseSinglePointParameterShouldSharedSameOneStructAndNotOverlap()
    {
        var configItem = RecipeConfigLoader.Load(out _);
        var dispenseItem = configItem.AuxAddress.Where(Item => Item.Group == "点胶").ToList();

        Assert.Equal(5, dispenseItem.Count);
        Assert.All(dispenseItem, Item => Assert.True(Item.IsStructArray, $"「{Item.Name}」未配置为结构体数组"));
        Assert.All(dispenseItem, Item => Assert.Equal(3500, Item.StructBaseAddress));
        Assert.All(dispenseItem, Item => Assert.Equal(8, Item.StructLength));

        // 按偏移 + 字数排一遍，任意两项不得重叠
        var areaBetween = dispenseItem
            .Select(Item => (Item.Name, begin: Item.StructInnerOffset, stop: Item.StructInnerOffset + Item.RegisterCount - 1))
            .OrderBy(area => area.begin)
            .ToList();

        for (var ordinal = 0; ordinal < areaBetween.Count; ordinal++)
        {
            Assert.True(areaBetween[ordinal].stop < 8,
                $"「{areaBetween[ordinal].Name}」超出结构体长度（止于偏移 {areaBetween[ordinal].stop}）");

            if (ordinal > 0)
            {
                Assert.True(areaBetween[ordinal].begin > areaBetween[ordinal - 1].stop,
                    $"「{areaBetween[ordinal - 1].Name}」与「{areaBetween[ordinal].Name}」在结构体内重叠");
            }
        }
    }

    /// <summary>
    /// <b>现场事故回归（2026-10-21）：</b>覆盖升级不会覆盖现场 <c>recipe-config.json</c>，
    /// 里面没有 <c>结构体基址/结构体内偏移/结构体长度</c> → 反序列化后全 0 →
    /// <c>是结构体数组</c> 为 false → 新功能静默失效（地址不随点偏移、也不显示"当前为第 N 点"）。
    /// 加载时必须按出厂定义补齐。
    /// </summary>
    [Fact]
    public void LoadLegacyFieldConfig_ShouldAutoFillStructParameter()
    {
        // 复刻现场文件：5 项地址正确（D3500/D3502/D3503/D3504/D3506），但没有结构体三参数
        var configPairObject = new RecipeConfig
        {
            AuxAddress =
            [
                new() { Key = "点胶-提前关胶", Name = "提前关胶", Group = "点胶", Address = "D3500", DataType = TagDataType.Float32 },
                new() { Key = "点胶-运动速度延时", Name = "出胶延时", Group = "点胶", Address = "D3502", DataType = TagDataType.Int16 },
                new() { Key = "点胶-关胶后延时", Name = "关胶后延时", Group = "点胶", Address = "D3503", DataType = TagDataType.Int16 },
                new() { Key = "点胶-抬针高度", Name = "抬针高度", Group = "点胶", Address = "D3504", DataType = TagDataType.Float32 },
                new() { Key = "点胶-点胶速度", Name = "点胶速度", Group = "点胶", Address = "D3506", DataType = TagDataType.Float32 },
                // 地址对不上出厂规划的项：一律不动（现场有自己的安排）
                new() { Key = "点胶-提前关胶-备用", Name = "备用", Group = "点胶", Address = "D3900", DataType = TagDataType.Float32 },
            ],
        };

        var fill = AuxAddressStructDefault.App(configPairObject);

        Assert.Equal(5, fill);

        var preCloseGlue = configPairObject.AuxAddress[0];
        Assert.True(preCloseGlue.IsStructArray);
        Assert.Equal(3500, preCloseGlue.StructBaseAddress);
        Assert.Equal(8, preCloseGlue.StructLength);
        Assert.Equal(0, preCloseGlue.StructInnerOffset);

        // 补齐后按点寻址立刻可用：第 2 点 = D3508
        Assert.Equal("D3508", preCloseGlue.ByPointAddress(1));

        // 地址与出厂规划不符的项保持原样
        var spare = configPairObject.AuxAddress[^1];
        Assert.False(spare.IsStructArray);
        Assert.Equal("D3900", spare.ByPointAddress(3));
    }

    /// <summary>已经配好结构体参数的现场不做二次覆盖（补齐只补零），只补「结构体成员」标记。</summary>
    [Fact]
    public void AlreadyHasStructParameter_ShouldNotBeFillOverwrite()
    {
        var configPairObject = new RecipeConfig
        {
            AuxAddress =
            [
                new()
                {
                    Key = "点胶-提前关胶", Address = "D3500", Group = "点胶",
                    StructBaseAddress = 3500, StructInnerOffset = 0, StructLength = 6,   // 现场改成了 6
                },
            ],
        };

        // 2026-10-21：新增「结构体成员」勾选框后，老配置只有三参数 —— 认回成员（返回 1），
        // 但现场的基址 / 偏移 / 长度一个字都不动
        Assert.Equal(1, AuxAddressStructDefault.App(configPairObject));

        var Item = configPairObject.AuxAddress[0];
        Assert.True(Item.StructMember);
        Assert.Equal(3500, Item.StructBaseAddress);
        Assert.Equal(0, Item.StructInnerOffset);
        Assert.Equal(6, Item.StructLength);

        // 再跑一次不再补（幂等）
        Assert.Equal(0, AuxAddressStructDefault.App(configPairObject));
    }

    /// <summary>
    /// 点胶结构体数组必须整段落在机种区段内（D2500–D3999）——
    /// 落在区段外的话"换机种"时单点工艺参数不会跟着换，等于换了机种还用旧参数。
    /// </summary>
    [Fact]
    public void MachineModelSection_ShouldOverwriteDispenseStructArrayAllPoint()
    {
        var configItem = RecipeConfigLoader.Load(out _);
        var PointCount = configItem.PointTable.Find("点胶")!.PointCount;
        var Item = configItem.AuxAddress.First(eachRow => eachRow.Group == "点胶" && eachRow.IsStructArray);

        var firstWord = Item.ByPointIndex(0);
        var lastWord = Item.ByPointIndex(PointCount - 1) + Item.StructLength - 1;

        Assert.True(MachineModelSection.Contains(firstWord), $"首点 D{firstWord} 不在机种区段内");
        Assert.True(MachineModelSection.Contains(lastWord), $"末点 D{lastWord} 不在机种区段内");
        Assert.Equal(3999, MachineModelSection.end);
    }
}
