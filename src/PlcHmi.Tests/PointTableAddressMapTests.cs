using PlcHmi.Business.Configuration;
using PlcHmi.Communication.Recipe;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Tests;

/// <summary>
/// 通用点表地址计算的测试。
///
/// <para>锁附：X 2500、Y 2560、浮高 2620；点胶：指令码 3400、X 2700、Y 2800、Z 2900
/// （2026-11-18 起点胶扩到 <b>50 点</b>：每列 100 字，Y/Z 基址随之后移，以现场 recipe-config.json 为准）。
/// 每点 Float32 占 2 字（指令码 UInt16 占 1 字）。偏移算错一个数，现场表现是
/// "轴跑到错误位置"或"指令码错位"，因此这些断言必须逐条钉死。</para>
/// </summary>
public class PointTableAddressMapTest
{
    private static PointTableDefinition ScrewTable() => new()
    {
        Key = "锁附",
        Name = "锁附示教",
        PointCount = 30,
        Column =
        [
            new PointTableColumnDefinition { Key = "X", BaseAddress = 2500 },
            new PointTableColumnDefinition { Key = "Y", BaseAddress = 2560 },
            new PointTableColumnDefinition { Key = "浮高", BaseAddress = 2620 },
        ],
    };

    private static PointTableDefinition DispenseTable() => new()
    {
        Key = "点胶",
        Name = "点胶示教",
        PointCount = 50,
        Column =
        [
            new PointTableColumnDefinition { Key = "指令码", BaseAddress = 3400, DataType = TagDataType.UInt16 },
            new PointTableColumnDefinition { Key = "X", BaseAddress = 2700 },
            new PointTableColumnDefinition { Key = "Y", BaseAddress = 2800 },
            new PointTableColumnDefinition { Key = "Z", BaseAddress = 2900 },
        ],
    };

    [Theory]
    [InlineData(0, "X", 2500)]
    [InlineData(1, "X", 2502)]
    [InlineData(29, "X", 2558)]
    [InlineData(0, "Y", 2560)]
    [InlineData(1, "Y", 2562)]
    [InlineData(29, "Y", 2618)]
    [InlineData(0, "浮高", 2620)]
    [InlineData(1, "浮高", 2622)]
    public void ScrewCoordinateAddressShouldSymbolValidAndPlcAgreedAddressTable(int Sequence, string ColumnKey, int expected) =>
        Assert.Equal(expected, new PointTableAddressMap(ScrewTable()).Address(Sequence, ColumnKey));

    [Theory]
    [InlineData(0, "指令码", 3400)]
    [InlineData(1, "指令码", 3401)]
    [InlineData(29, "指令码", 3429)]
    [InlineData(49, "指令码", 3449)]
    [InlineData(0, "X", 2700)]
    [InlineData(1, "X", 2702)]
    [InlineData(49, "X", 2798)]
    [InlineData(1, "Y", 2802)]
    [InlineData(49, "Y", 2898)]
    [InlineData(1, "Z", 2902)]
    [InlineData(49, "Z", 2998)]
    public void DispenseAddressShouldSymbolValidAndPlcAgreedAddressTable(int Sequence, string ColumnKey, int expected) =>
        Assert.Equal(expected, new PointTableAddressMap(DispenseTable()).Address(Sequence, ColumnKey));

    [Fact]
    public void PerPointStepLongShouldAndDataTypeConsistent()
    {
        var screw = new PointTableAddressMap(ScrewTable());
        Assert.Equal(2, screw.WordPerPointBit("X"));
        Assert.Equal(60, screw.ColumnBlockWord("X"));       // 30 点 × 2 字
        Assert.Equal(2502 - 2500, screw.Address(1, "X") - screw.Address(0, "X"));

        var dispense = new PointTableAddressMap(DispenseTable());
        Assert.Equal(1, dispense.WordPerPointBit("指令码"));   // 指令码单字
        Assert.Equal(50, dispense.ColumnBlockWord("指令码"));    // 50 点 × 1 字
        Assert.Equal(2, dispense.WordPerPointBit("X"));
        Assert.Equal(100, dispense.ColumnBlockWord("X"));        // 50 点 × 2 字
    }

    [Fact]
    public void BeyondBoundaryIndexAndUnknownColumnShouldThrow()
    {
        var Map = new PointTableAddressMap(ScrewTable());

        Assert.Throws<ArgumentOutOfRangeException>(() => Map.Address(-1, "X"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Map.Address(30, "X"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Map.Address(0, "不存在"));
        Assert.False(Map.IndexValid(30));
    }

    [Fact]
    public void PointCountOrColumnAsZeroWhenShouldRejectStructCreate()
    {
        Assert.Throws<ArgumentException>(() => new PointTableAddressMap(new PointTableDefinition { PointCount = 0 }));
        Assert.Throws<ArgumentException>(
            () => new PointTableAddressMap(new PointTableDefinition { PointCount = 10 }));
    }

    [Fact]
    public void RealConfigColumnBaseAddressShouldAndNeedRequireConsistent()
    {
        // 守住"改配置改错列基址"这类问题：直接读现场配置文件
        var Config = RecipeConfigLoader.Load(out var error);
        Assert.Null(error);

        var screw = Config.PointTable.Find("锁附");
        Assert.NotNull(screw);
        Assert.Equal(30, screw!.PointCount);
        Assert.Equal(2500, screw.FindColumn("X")!.BaseAddress);
        Assert.Equal(2560, screw.FindColumn("Y")!.BaseAddress);

        var dispense = Config.PointTable.Find("点胶");
        Assert.NotNull(dispense);
        Assert.Equal(50, dispense!.PointCount);
        Assert.Equal(3400, dispense.FindColumn("指令码")!.BaseAddress);
        Assert.Equal(2700, dispense.FindColumn("X")!.BaseAddress);
        Assert.Equal(2800, dispense.FindColumn("Y")!.BaseAddress);
        Assert.Equal(2900, dispense.FindColumn("Z")!.BaseAddress);
    }

    /// <summary>
    /// 配置自检：动作按钮 / 回零 / 点动的线圈地址不得互相重叠。
    ///
    /// <para><b>为什么必须有这条</b>：取消地址范围限制后，页面上的元件地址由管理员自行维护，
    /// 没有第二道闸门拦住"两个功能写同一个 M 点"。2026-10-12 审计就真的发现了
    /// 「锁附-电批启动 M480」与「点胶 Z 轴点动正转 M480」撞车——按下点动会把电批点着。
    /// 这类错误在界面上完全看不出来，只能靠配置自检钉住。</para>
    /// </summary>
    [Fact]
    public void RealConfigActionButtonAndJogHomeCoilMustNotOverlap()
    {
        var Config = RecipeConfigLoader.Load(out _);
        var Occupy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Register(string addressValue, string usePurpose)
        {
            if (string.IsNullOrWhiteSpace(addressValue))
            {
                return;
            }

            Assert.False(Occupy.TryGetValue(addressValue, out var alreadyHas),
                $"地址 {addressValue} 被重复占用：{alreadyHas} 与 {usePurpose}");

            Occupy[addressValue] = usePurpose;
        }

        foreach (var Item in Config.ActionButton)
        {
            Register(Item.Address, $"动作按钮「{Item.DisplayName}」");
        }

        foreach (var Item in Config.Home)
        {
            Register(Item.Address, $"回零「{Item.DisplayName}」");
        }

        // 点胶 Y 与锁附 Y 是同一根轴（共轴），配置里会出现两行同样的线圈地址——
        // 按轴去重后再比对，避免把"共轴"误报成冲突。
        foreach (var Item in Config.Jog
                     .GroupBy(Item => Item.Axis, StringComparer.OrdinalIgnoreCase)
                     .Select(groupValue => groupValue.First()))
        {
            Register(Item.ForwardAddress, $"点动「{Item.DisplayName}」正转");
            Register(Item.ReverseAddress, $"点动「{Item.DisplayName}」反转");
        }
    }

    [Fact]
    public void RealConfigSaveCoordinateColumnShouldOnlyOverwritePairShouldAxis()
    {
        // 「保存坐标点」写哪些列是现场语义：锁附写 X/Y + 当前浮高（2026-10-24 起浮高由测高地址给出），
        // 点胶写 X/Y/Z
        var Config = RecipeConfigLoader.Load(out _);

        var screw = Config.PointTable.Find("锁附")!;
        Assert.True(screw.IsAndSaveCoordinate("X"));
        Assert.True(screw.IsAndSaveCoordinate("Y"));
        Assert.True(screw.IsAndSaveCoordinate("浮高"));
        Assert.Equal(240, screw.CurrentFloatHeightAddress);

        var dispense = Config.PointTable.Find("点胶")!;
        Assert.True(dispense.IsAndSaveCoordinate("X"));
        Assert.True(dispense.IsAndSaveCoordinate("Y"));
        Assert.True(dispense.IsAndSaveCoordinate("Z"));
        Assert.False(dispense.IsAndSaveCoordinate("指令码"));
    }

    [Fact]
    public void AxisSpeedParameterShouldAsSixAxisAndSingleBitUnifiedOneAsMilliMeterPerSeconds()
    {
        // 参数页「轴速度」表的行来自 appsettings 的轴列表：六轴、单位一律 mm/s
        var Settings = AppSettingsLoader.Load(out _);

        Assert.Equal(6, Settings.AxisList.Count);
        Assert.Equal(
            new[] { "X", "Y", "Z", "ZZ", "GlueX", "GlueZ" },
            Settings.AxisList.Select(EntryItem => EntryItem.Axis).ToArray());
        Assert.All(Settings.AxisList, EntryItem => Assert.Equal("mm/s", EntryItem.Unit));
    }
}
