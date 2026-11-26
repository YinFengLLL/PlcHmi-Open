using Microsoft.Extensions.Logging.Abstractions;
using PlcHmi.Business.Configuration;
using PlcHmi.Business.Services;
using PlcHmi.Communication.Inovance;
using PlcHmi.Core.Enums;
using PlcHmi.Tests.Fake;

namespace PlcHmi.Tests;

/// <summary>
/// 轴地址的"输入书写形式"与保存链路测试。
///
/// <para><b>守的是什么：</b>参数页「轴速度」表的地址列显示为 <c>D1202</c>，
/// 而现场习惯是把整格全选后直接填寄存器号 <c>1252</c>。服务层的写入路径本来就是宽松解析
/// （<c>TrimStart('D')</c>），若校验只认带前缀写法，就会出现
/// "页面报『不是有效的软元件地址』、服务其实完全能写"的分裂——现场表现就是
/// <b>「改了轴速度地址保存不了」</b>（2026-10-14 反馈）。</para>
///
/// <para>另一半是落盘形态：位置地址会被写进 <c>tags.json</c>，那里必须是<b>软元件符号</b>。
/// 裸数字写进去会让地址解析器解析失败、采集计划静默跳过该点位
/// （"改了地址却不生效"，最难查的一类），所以保存前必须规范成 <c>D{序号}</c>。</para>
///
/// <para>会写测试输出目录里的 <c>config/recipe-config.json</c> 与 <c>config/tags.json</c>，
/// 与其它碰配置文件的用例同集合，禁用并行。</para>
/// </summary>
[Collection("config-files")]
public class AxisAddressInputFormatTest
{
    private static AxisAddressService NewService() =>
        new(
            RecipeConfigLoader.Load(out _),
            new TagRepository(NullLogger<TagRepository>.Instance),
            new InovanceAddressParser(PlcSeries.SmallPlc),
            new FakePollingEngine(),
            new AllowAllPermissionGuard(),
            NullLogger<AxisAddressService>.Instance);

    [Fact]
    public void PositioningSpeedAddressOmitPrefixShouldBeAccepted()
    {
        var serviceInstance = NewService();
        var candidate = serviceInstance.DataGrid.Clone();

        candidate.Find("X")!.PositioningSpeedAddress = "1252";

        // 校验与写入必须同口径：服务写入用的就是宽松解析
        Assert.True(serviceInstance.Validate(candidate).IsValid, "裸寄存器号应按 D 区受理");
    }

    [Fact]
    public void CommandCategoryAddressOmitPrefixShouldStillBeRejected()
    {
        var serviceInstance = NewService();
        var candidate = serviceInstance.DataGrid.Clone();

        // 点动 / 回零允许 M 区线圈或 D 区字内位：裸数字无法判断意图，必须写明前缀
        candidate.Find("X")!.JogForwardAddress = "470";

        var validateItem = serviceInstance.Validate(candidate);

        Assert.False(validateItem.IsValid);
        Assert.Contains(validateItem.ErrorCollection, eventArgs =>
            eventArgs.Contains("不是有效的软元件地址", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OnlyChangePositioningSpeedAddressShouldBeAbleToPersistAndReadBack()
    {
        var (recipePath, tagPath, originalRecipe, originalTag) = BackupConfig();

        try
        {
            var serviceInstance = NewService();
            var candidate = serviceInstance.DataGrid.Clone();

            // 模拟参数页「轴速度」表里改定位速度地址（唯一编辑入口）
            candidate.Find("X")!.PositioningSpeedAddress = "D1252";

            Assert.True(serviceInstance.Validate(candidate).IsValid);

            await serviceInstance.SaveAsync(candidate);

            Assert.Equal("D1252", serviceInstance.DataGrid.Find("X")!.PositioningSpeedAddress);
            Assert.Contains("\"address\": 1252", await File.ReadAllTextAsync(recipePath), StringComparison.Ordinal);
        }
        finally
        {
            RestoreConfig(recipePath, tagPath, originalRecipe, originalTag);
        }
    }

    [Fact]
    public async Task RawRegisterNumberSaveAfterShouldRuleRangeAsWithPrefixAndWriteEntertags()
    {
        var (recipePath, tagPath, originalRecipe, originalTag) = BackupConfig();

        try
        {
            var serviceInstance = NewService();
            var candidate = serviceInstance.DataGrid.Clone();

            // 现场习惯：全选地址格后直接填寄存器号（省掉前缀）
            candidate.Find("X")!.PositioningSpeedAddress = "1252";
            candidate.Find("X")!.PositionAddress = "1221";

            await serviceInstance.SaveAsync(candidate);

            // 回读一律是带前缀的规范写法（界面显示的就是它）
            Assert.Equal("D1252", serviceInstance.DataGrid.Find("X")!.PositioningSpeedAddress);
            Assert.Equal("D1221", serviceInstance.DataGrid.Find("X")!.PositionAddress);

            // 位置地址落在 tags.json，必须是能解析的软元件符号，否则采集计划会静默跳过该点位
            Assert.Contains("\"AxisXPosition\"", await File.ReadAllTextAsync(tagPath), StringComparison.Ordinal);
            Assert.Contains("\"D1221\"", await File.ReadAllTextAsync(tagPath), StringComparison.Ordinal);
        }
        finally
        {
            RestoreConfig(recipePath, tagPath, originalRecipe, originalTag);
        }
    }

    // ── 配置备份 / 还原 ─────────────────────────────────────────────────

    private static (string recipePath, string tagPath, string originalRecipe, string originalTag) BackupConfig()
    {
        var recipePath = ConfigPath.RecipeConfig;
        var tagPath = ConfigPath.TagCollection;

        return (recipePath, tagPath, File.ReadAllText(recipePath), File.ReadAllText(tagPath));
    }

    private static void RestoreConfig(string recipePath, string tagPath, string originalRecipe, string originalTag)
    {
        File.WriteAllText(recipePath, originalRecipe);
        File.WriteAllText(tagPath, originalTag);
    }
}
