using System.Text.Json;
using PlcHmi.Business.Configuration;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Tests;

/// <summary>
/// 老现场配置的「点胶 30 点 → 50 点」结构升级（2026-11）。
///
/// <para><b>守的是什么：</b>发布包只补缺失文件、不覆盖现场 <c>config/</c>，
/// 所以升级必须由程序在启动时把现场的 <c>recipe-config.json</c> 改过来 ——
/// 但又不能乱改：点数不是 30、或列基址被现场改过时，一律不碰。</para>
///
/// <para>另外钉死"最小改动"：迁移前后除那三处值以外<b>逐字节相同</b>
/// （不重新序列化整个 JSON，避免重排字段 / 丢键）。</para>
/// </summary>
public class RecipeConfigMigrationTest : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "plchmi-迁移-" + Guid.NewGuid().ToString("N")[..8]);

    public RecipeConfigMigrationTest() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响用例结论
        }
    }

    /// <summary>写一份"1.7.0 现场版"风格的配置（点胶 30 点、Y/Z 旧基址）。</summary>
    private string WriteConfig(int PointCount = 30, int yBaseAddress = 2760, int zBaseAddress = 2820)
    {
        var Config = new RecipeConfig();

        Config.PointTable.PointTable.Add(BuildTable("锁附", 30, 2500, 2560, 2620));
        Config.PointTable.PointTable.Add(BuildTable("点胶", PointCount, 2700, yBaseAddress, zBaseAddress, HasInstructionCode: true));

        var path = Path.Combine(_dir, "recipe-config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(Config, JsonConfig.writeHumanStyle));
        return path;
    }

    private static PointTableDefinition BuildTable(
        string Key, int PointCount, int x, int y, int z, bool HasInstructionCode = false)
    {
        var Table = new PointTableDefinition { Key = Key, Name = $"{Key}示教", PointCount = PointCount };

        if (HasInstructionCode)
        {
            Table.Column.Add(new PointTableColumnDefinition
            {
                Key = "指令码", Name = "指令码", BaseAddress = 3400,
                DataType = TagDataType.UInt16, PositionTag = null,
            });
        }

        Table.Column.Add(new PointTableColumnDefinition { Key = "X", Name = "X坐标", BaseAddress = x, PositionTag = "AxisGlueXPosition" });
        Table.Column.Add(new PointTableColumnDefinition { Key = "Y", Name = "Y坐标", BaseAddress = y, PositionTag = "AxisYPosition" });
        Table.Column.Add(new PointTableColumnDefinition
        {
            Key = Key == "锁附" ? "浮高" : "Z", Name = "Z坐标", BaseAddress = z, PositionTag = "AxisGlueZPosition",
        });

        return Table;
    }

    private static RecipeConfig readOnce(string path) =>
        JsonSerializer.Deserialize<RecipeConfig>(File.ReadAllText(path), JsonConfig.Read)!;

    [Fact]
    public void LegacyConfigShouldUpgradeTo50PointAndAfterMoveYZBaseAddress()
    {
        var path = WriteConfig();
        var originalText = File.ReadAllText(path);

        var Remark = RecipeConfigMigration.App(path);

        Assert.NotNull(Remark);
        Assert.Contains("50 点", Remark, StringComparison.Ordinal);

        var migrationAfter = readOnce(path);
        var dispense = migrationAfter.PointTable.Find("点胶")!;

        Assert.Equal(50, dispense.PointCount);
        Assert.Equal(2700, dispense.FindColumn("X")!.BaseAddress);
        Assert.Equal(2800, dispense.FindColumn("Y")!.BaseAddress);
        Assert.Equal(2900, dispense.FindColumn("Z")!.BaseAddress);
        Assert.Equal(3400, dispense.FindColumn("指令码")!.BaseAddress);

        // 锁附不受影响
        Assert.Equal(30, migrationAfter.PointTable.Find("锁附")!.PointCount);
        Assert.Equal(2560, migrationAfter.PointTable.Find("锁附")!.FindColumn("Y")!.BaseAddress);

        // 只有那三处值变了：把新值改回旧值应与原文逐字节相同
        var migrationText = File.ReadAllText(path);
        var fallback = migrationText
            .Replace("\"pointCount\": 50", "\"pointCount\": 30", StringComparison.Ordinal)
            .Replace("\"baseAddress\": 2800", "\"baseAddress\": 2760", StringComparison.Ordinal)
            .Replace("\"baseAddress\": 2900", "\"baseAddress\": 2820", StringComparison.Ordinal);

        Assert.Equal(originalText, fallback);

        // 旧文件已留备份
        Assert.True(File.Exists(path + ".bak"), "迁移后应留下 .bak 以便回滚");
        Assert.Equal(originalText, File.ReadAllText(path + ".bak"));
    }

    [Fact]
    public void MigrationShouldIdempotent_AgainTimesStartNoLongerChangeMove()
    {
        var path = WriteConfig();

        Assert.NotNull(RecipeConfigMigration.App(path));

        var onceAfter = File.ReadAllText(path);

        Assert.Null(RecipeConfigMigration.App(path));
        Assert.Equal(onceAfter, File.ReadAllText(path));
    }

    /// <summary>现场自己算过地址（基址不是出厂旧值）时，宁可不动，也不能把地址改错。</summary>
    [Fact]
    public void BaseAddressBeFieldChangeBeyondWhenShouldNotAutoMigration()
    {
        var path = WriteConfig(yBaseAddress: 2761);
        var originalText = File.ReadAllText(path);

        Assert.Null(RecipeConfigMigration.App(path));
        Assert.Equal(originalText, File.ReadAllText(path));
        Assert.Equal(30, readOnce(path).PointTable.Find("点胶")!.PointCount);
    }

    [Fact]
    public void PointCountNotIs30FieldConfigShouldNotBeChangeMove()
    {
        var path = WriteConfig(PointCount: 20);
        var originalText = File.ReadAllText(path);

        Assert.Null(RecipeConfigMigration.App(path));
        Assert.Equal(originalText, File.ReadAllText(path));
    }

    [Fact]
    public void FileMissingWhenShouldSafetyReturn()
    {
        Assert.Null(RecipeConfigMigration.App(Path.Combine(_dir, "不存在.json")));
    }
}
