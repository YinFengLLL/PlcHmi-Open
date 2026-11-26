using Microsoft.Extensions.Logging.Abstractions;
using PlcHmi.Business.Configuration;
using PlcHmi.Business.Services;
using PlcHmi.Communication.Inovance;
using PlcHmi.Core.Enums;
using PlcHmi.Tests.Fake;

namespace PlcHmi.Tests;

/// <summary>
/// 轴地址保存会写测试输出目录里的 <c>config/recipe-config.json</c> 与 <c>config/tags.json</c>，
/// 与其它读取配置的测试并行跑会互相看到半途的配置。本集合禁用并行。
/// </summary>
[CollectionDefinition("config-files", DisableParallelization = true)]
public sealed class ConfigFileCollection
{
    // 仅作为集合标记，不需要成员
}

/// <summary>
/// 轴地址管理的服务层校验测试。
///
/// <para><b>2026-10-12 变更：</b>按现场要求<b>取消地址范围限制</b>——
/// 不再校验轴参数区（D1200–D1799）、PLC 保留区与 tags.json 其它点位。
/// 因此原先"越界 / 保留区 / PLC 自有字段 / 与命令位冲突"那批用例已随规则一并移除；
/// 保留的是与"范围"无关的判定：地址能否解析、元素类型、本次提交内是否重叠。</para>
/// </summary>
[Collection("config-files")]
public class AxisAddressServiceTest
{
    private static InovanceAddressParser Parser() => new(PlcSeries.SmallPlc);

    private static AxisAddressService NewService(FakePollingEngine Polling) =>
        new(
            RecipeConfigLoader.Load(out _),
            new TagRepository(NullLogger<TagRepository>.Instance),
            Parser(),
            Polling,
            new AllowAllPermissionGuard(),
            NullLogger<AxisAddressService>.Instance);

    [Fact]
    public void DefaultTableShouldReadCompleteSixAxisAndHomeAddress()
    {
        var serviceInstance = NewService(new FakePollingEngine());

        // 点胶锁付机六轴：锁附 X/Y/Z/ZZ + 点胶 X/Z
        Assert.Equal(6, serviceInstance.DataGrid.AxisList.Count);
        Assert.Equal(
            new[] { "X", "Y", "Z", "ZZ", "GlueX", "GlueZ" },
            serviceInstance.DataGrid.AxisList.Select(EntryItem => EntryItem.Axis).ToArray());

        var coordinateX = serviceInstance.DataGrid.Find("X");
        Assert.NotNull(coordinateX);
        // 2026-10-21 现场配方：定位速度 D1400 起、位置 D100 起、点动档位共用 D15
        Assert.Equal("D1400", coordinateX!.PositioningSpeedAddress);
        Assert.Equal("D15", coordinateX.JogSpeedLevelAddress);
        Assert.Equal("D100", coordinateX.PositionAddress);

        // 点动：配置（jogButtons）优先于 tags.json，六轴一律走 M 区线圈
        Assert.Equal("M1002", coordinateX.JogForwardAddress);
        Assert.Equal("M1003", coordinateX.JogReverseAddress);
        Assert.Equal("M440", coordinateX.HomeAddress);

        Assert.Equal("M1152", serviceInstance.DataGrid.Find("ZZ")!.JogForwardAddress);
        Assert.Equal("M1252", serviceInstance.DataGrid.Find("GlueX")!.JogForwardAddress);
        Assert.Equal("M1202", serviceInstance.DataGrid.Find("GlueZ")!.JogForwardAddress);
        Assert.Equal("M445", serviceInstance.DataGrid.Find("GlueZ")!.HomeAddress);
    }

    [Fact]
    public void DefaultTableValidateShouldPass()
    {
        var serviceInstance = NewService(new FakePollingEngine());

        // 同字不同位（D1210.2 / D1210.3）不应被判为重叠；无位置回读的轴允许留空
        var validateItem = serviceInstance.Validate(serviceInstance.DataGrid.Clone());

        Assert.True(validateItem.IsValid, validateItem.Summary);
    }

    [Fact]
    public void CancelRangeLimitAfterAxisParameterAreaOuterAddressShouldBeAccepted()
    {
        var serviceInstance = NewService(new FakePollingEngine());
        var candidate = serviceInstance.DataGrid.Clone();

        // 旧规则会拒绝这些地址（D1000 段 / 越界段），现在只要求"是 D 区且不重叠"
        candidate.Find("X")!.PositioningSpeedAddress = "D1050";
        candidate.Find("Y")!.PositioningSpeedAddress = "D2000";
        // 档位地址六轴共用（2026-10-13 现场要求）：改就一起改，保持一致
        foreach (var axisItem in candidate.AxisList)
        {
            axisItem.JogSpeedLevelAddress = "D3000";
        }

        var validateItem = serviceInstance.Validate(candidate);

        Assert.True(validateItem.IsValid, validateItem.Summary);
    }

    [Fact]
    public void JogAddressAllowDIntraWordBit()
    {
        var serviceInstance = NewService(new FakePollingEngine());
        var candidate = serviceInstance.DataGrid.Clone();

        candidate.Find("X")!.JogForwardAddress = "D1210.5";

        var validateItem = serviceInstance.Validate(candidate);

        Assert.True(validateItem.IsValid, validateItem.Summary);
    }

    [Fact]
    public void JogAddressWholeWordDAreaShouldBeRejected()
    {
        var serviceInstance = NewService(new FakePollingEngine());
        var candidate = serviceInstance.DataGrid.Clone();

        // 整字写法（无 .位）：点动是位命令，写整字会覆盖同字的其它命令位
        candidate.Find("X")!.JogForwardAddress = "D230";

        var validateItem = serviceInstance.Validate(candidate);

        Assert.False(validateItem.IsValid);
        Assert.Contains(validateItem.ErrorCollection, eventArgs =>
            eventArgs.Contains("必须是 M 区地址或 D/R 区字内位", StringComparison.Ordinal));
    }

    [Fact]
    public void PositionAddressKeepEmptyShouldBeAccepted()
    {
        var serviceInstance = NewService(new FakePollingEngine());
        var candidate = serviceInstance.DataGrid.Clone();

        candidate.Find("ZZ")!.PositionAddress = string.Empty;

        var validateItem = serviceInstance.Validate(candidate);

        Assert.True(validateItem.IsValid, validateItem.Summary);
    }

    [Fact]
    public void PositioningSpeedKeepEmptyShouldBeRejected()
    {
        var serviceInstance = NewService(new FakePollingEngine());
        var candidate = serviceInstance.DataGrid.Clone();

        candidate.Find("X")!.PositioningSpeedAddress = string.Empty;

        var validateItem = serviceInstance.Validate(candidate);

        Assert.False(validateItem.IsValid);
        Assert.Contains(validateItem.ErrorCollection, eventArgs => eventArgs.Contains("不能为空", StringComparison.Ordinal));
    }

    [Fact]
    public void IllegalAddressShouldBeRejected()
    {
        var serviceInstance = NewService(new FakePollingEngine());
        var candidate = serviceInstance.DataGrid.Clone();

        candidate.Find("X")!.PositionAddress = "X123";

        var validateItem = serviceInstance.Validate(candidate);

        Assert.False(validateItem.IsValid);
    }

    [Fact]
    public void SameWordDifferentBitNotCalculateOverlap_SameBitOnlyCalculate()
    {
        var serviceInstance = NewService(new FakePollingEngine());
        var candidate = serviceInstance.DataGrid.Clone();

        // 同一字的两个不同位：D1210.2 与 D1210.3 互不冲突
        candidate.Find("X")!.JogForwardAddress = "D1210.2";
        candidate.Find("Y")!.JogForwardAddress = "D1210.3";

        Assert.True(serviceInstance.Validate(candidate).IsValid);

        // 同一位才冲突
        candidate.Find("Y")!.JogForwardAddress = "D1210.2";

        var validateItem = serviceInstance.Validate(candidate);

        Assert.False(validateItem.IsValid);
        Assert.Contains(validateItem.ErrorCollection, eventArgs => eventArgs.Contains("重叠", StringComparison.Ordinal));
    }

    [Fact]
    public void HomeAndJogSharedSameOneCoilShouldBeJudgeOverlap()
    {
        var serviceInstance = NewService(new FakePollingEngine());
        var candidate = serviceInstance.DataGrid.Clone();

        candidate.Find("X")!.HomeAddress = candidate.Find("Y")!.HomeAddress;

        var validateItem = serviceInstance.Validate(candidate);

        Assert.False(validateItem.IsValid);
        Assert.Contains(validateItem.ErrorCollection, eventArgs => eventArgs.Contains("重叠", StringComparison.Ordinal));
    }

    [Fact]
    public void TwoAxisAddressOverlapShouldBeRejected()
    {
        var serviceInstance = NewService(new FakePollingEngine());
        var candidate = serviceInstance.DataGrid.Clone();

        candidate.Find("Y")!.PositioningSpeedAddress = candidate.Find("X")!.PositioningSpeedAddress;

        var validateItem = serviceInstance.Validate(candidate);

        Assert.False(validateItem.IsValid);
        Assert.Contains(validateItem.ErrorCollection, eventArgs => eventArgs.Contains("重叠", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SaveValidAddressShouldPersistAndTriggerSamplingCountPlanRebuild()
    {
        var recipePath = ConfigPath.RecipeConfig;
        var tagPath = ConfigPath.TagCollection;

        var originalRecipe = await File.ReadAllTextAsync(recipePath);
        var originalTag = await File.ReadAllTextAsync(tagPath);

        try
        {
            var Polling = new FakePollingEngine();
            var serviceInstance = NewService(Polling);

            var candidate = serviceInstance.DataGrid.Clone();

            // 档位地址六轴共用：改就一起改
        foreach (var axisItem in candidate.AxisList)
        {
            axisItem.JogSpeedLevelAddress = "D1213";
        }

            var validateItem = serviceInstance.Validate(candidate);
            Assert.True(validateItem.IsValid, validateItem.Summary);

            await serviceInstance.SaveAsync(candidate);

            Assert.Equal(1, Polling.RebuildCount);
            Assert.Equal("D1213", serviceInstance.DataGrid.Find("X")!.JogSpeedLevelAddress);
            Assert.Contains("\"address\": 1213", await File.ReadAllTextAsync(recipePath), StringComparison.Ordinal);
        }
        finally
        {
            // 配置文件是测试输出目录里的共享副本，必须还原，否则会污染其它测试
            await File.WriteAllTextAsync(recipePath, originalRecipe);
            await File.WriteAllTextAsync(tagPath, originalTag);
        }
    }

    [Fact]
    public async Task SaveOverlapAddressShouldThrowAndNotPersist()
    {
        var recipePath = ConfigPath.RecipeConfig;
        var Original = await File.ReadAllTextAsync(recipePath);

        try
        {
            var Polling = new FakePollingEngine();
            var serviceInstance = NewService(Polling);

            var candidate = serviceInstance.DataGrid.Clone();
            candidate.Find("Y")!.PositioningSpeedAddress = candidate.Find("X")!.PositioningSpeedAddress;

            await Assert.ThrowsAsync<InvalidOperationException>(() => serviceInstance.SaveAsync(candidate));

            Assert.Equal(0, Polling.RebuildCount);
            Assert.Equal(Original, await File.ReadAllTextAsync(recipePath));
        }
        finally
        {
            await File.WriteAllTextAsync(recipePath, Original);
        }
    }

    [Fact]
    public async Task SaveHomeAddressShouldWriteBackConfig()
    {
        var recipePath = ConfigPath.RecipeConfig;
        var tagPath = ConfigPath.TagCollection;

        var originalRecipe = await File.ReadAllTextAsync(recipePath);
        var originalTag = await File.ReadAllTextAsync(tagPath);

        try
        {
            var serviceInstance = NewService(new FakePollingEngine());
            var candidate = serviceInstance.DataGrid.Clone();

            candidate.Find("X")!.HomeAddress = "M490";
            await serviceInstance.SaveAsync(candidate);

            Assert.Equal("M490", serviceInstance.DataGrid.Find("X")!.HomeAddress);
            Assert.Contains("\"M490\"", await File.ReadAllTextAsync(recipePath), StringComparison.Ordinal);
        }
        finally
        {
            await File.WriteAllTextAsync(recipePath, originalRecipe);
            await File.WriteAllTextAsync(tagPath, originalTag);
        }
    }
}
