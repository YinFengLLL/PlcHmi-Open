using PlcHmi.Business.Configuration;
using PlcHmi.Communication.Modbus;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Tests;

/// <summary>
/// 轴参数区的契约测试：地址排布、数据类型、允许区间。
///
/// <para><b>守的是什么（2026-09-12 现场实测 + 2026-09-17 按 PLC 地址分配表迁移）</b>：
/// 参数项原先一律按 <c>UInt16</c> 编码、只写 1 个寄存器，而现场约定是 Float32（占 2 字）；
/// 后一项的低字会踩掉前一项的高字——PLC 读到的是错位的值。
/// 现地址按分配表逐轴落位（轴 1–轴 6 各 100 字，偏移 2），这一组把新契约钉死。</para>
/// </summary>
public class RecipeFloat32ParameterTest
{
    private static RecipeConfig Config() => RecipeConfigLoader.Load(out _);

    [Fact]
    public void ParameterShouldAsSixItemPositioningSpeedAndEachAtItsAxisArea()
    {
        var configPairObject = Config();

        // 2026-10-12：点胶锁付机为六轴（锁附 X/Y/Z/ZZ + 点胶 X/Z）
        Assert.Equal(6, configPairObject.Parameter.Count);
        Assert.Equal(
            new[]
            {
                "X.LocateSpeed", "Y.LocateSpeed", "Z.LocateSpeed",
                "ZZ.LocateSpeed", "GlueX.LocateSpeed", "GlueZ.LocateSpeed",
            },
            configPairObject.Parameter.Select(PointItem => PointItem.Key).ToArray());

        // 2026-10-21 现场配方：六轴速度集中在 D1400 起（每项 2 字）
        Assert.Equal(
            new[] { 1400, 1402, 1404, 1406, 1410, 1408 },
            configPairObject.Parameter.Select(PointItem => PointItem.Address).ToArray());

        // 六轴速度集中在 D1400 起，每项 Float32 占 2 字（地址必为偶数）
        Assert.All(configPairObject.Parameter, PointItem => Assert.Equal(0, PointItem.Address % 2));
    }

    [Fact]
    public void ParameterShouldDeclareAsFloat32AndEachOccupyTwoWord()
    {
        foreach (var specItem in Config().Parameter)
        {
            Assert.Equal(TagDataType.Float32, specItem.DataType);
            Assert.Equal(2, specItem.RegisterCount);
        }
    }

    [Fact]
    public void ParameterAddressShouldNotOverlap()
    {
        var range = Config().Parameter
            .Select(PointItem => (PointItem.Key, Start: PointItem.Address, End: PointItem.AddressEndBoundary))
            .ToList();

        for (var Entry = 0; Entry < range.Count; Entry++)
        {
            for (var innerLayerIndex = Entry + 1; innerLayerIndex < range.Count; innerLayerIndex++)
            {
                Assert.False(
                    range[Entry].Start < range[innerLayerIndex].End && range[innerLayerIndex].Start < range[Entry].End,
                    $"{range[Entry].Key} 与 {range[innerLayerIndex].Key} 地址重叠");
            }
        }
    }

    [Theory]
    [InlineData(1200, true)]
    [InlineData(1500, true)]
    [InlineData(1799, true)]
    [InlineData(1199, false)]     // 2026-09-17 起轴参数区从 D1200 开始
    [InlineData(1800, false)]     // 轴 6 结束于 D1799，D1800 已出区间
    public void AxisParameterAllowAreaBetweenShouldAsD1200ToD1799(int addressValue, bool expected)
    {
        Assert.Equal(expected, Config().ParameterAddressRange.Contains(addressValue));
    }

    [Fact]
    public void DefaultPositioningSpeedCDABWordOrderShouldSymbolValidMeasuredVector()
    {
        foreach (var specItem in Config().Parameter)
        {
            var registerList = RegisterCodec.Encode(specItem.DataType, specItem.Default, WordOrder.LittleEndian);

            Assert.Equal(2, registerList.Length);

            // 四个默认值都是整数，CDAB 下低字必为 0、高字承载全部信息
            Assert.Equal((ushort)0x0000, registerList[0]);

            var decoded = (float)RegisterCodec.Decode(TagDataType.Float32, registerList, WordOrder.LittleEndian);
            Assert.Equal(specItem.Default, decoded, 3);
        }
    }

    [Fact]
    public void JogSpeedGearShouldOverwriteSixAxis()
    {
        var configPairObject = Config();

        Assert.Equal(6, configPairObject.JogSpeedLevel.Count);
        Assert.Equal(
            new[] { "X", "Y", "Z", "ZZ", "GlueX", "GlueZ" },
            configPairObject.JogSpeedLevel.Select(gear => gear.Axis).ToArray());

        // 六轴共用同一个速度档位地址（切档一次，六轴同时生效）；现场版地址为 D15
        Assert.All(
            configPairObject.JogSpeedLevel,
            gear => Assert.Equal(15, gear.Address));
    }

    [Theory]
    // 2026-10-13 现场口径：档位写入值 低=0、中=1、高=2
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(-1, false)]
    public void GearValueOnlyAcceptZeroToTwo(int Level, bool expected) =>
        Assert.Equal(expected, JogSpeedLevelAddress.IsValidLevel(Level));

    [Fact]
    public void ConfigInShouldNotAgainOutCurrentDeprecatedManualSpeedParameter()
    {
        Assert.DoesNotContain(
            Config().Parameter,
            PointItem => PointItem.Key.Contains("ManualSpeed", StringComparison.OrdinalIgnoreCase));
    }
}
