using Microsoft.Extensions.Logging.Abstractions;
using PlcHmi.Business.Services;
using PlcHmi.Communication.Inovance;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;
using PlcHmi.Tests.Fake;
using Xunit.Abstractions;

namespace PlcHmi.Tests;

/// <summary>
/// 运行监控页元件地址管理的回归测试。
///
/// <para>覆盖：默认元件表与需求地址、D/M 区校验、重叠与冲突拦截、
/// 保存链路（更新点位 → 落盘 → 重建采集计划）与权限兜底。</para>
/// </summary>
public class RunMonitorAddressTest
{
    private readonly ITestOutputHelper _output;

    public RunMonitorAddressTest(ITestOutputHelper Output) => _output = Output;

    private static (RunMonitorAddressService Service, FakeTagRepository Point, FakePollingEngine Polling) NewService(
        IPermissionGuard? guard = null, FakeTagRepository? pointTable = null)
    {
        var Table = pointTable ?? FakeTagRepository.RunPageDefaultPoint();
        var Polling = new FakePollingEngine();
        var serviceInstance = new RunMonitorAddressService(
            Table,
            new InovanceAddressParser(PlcSeries.SmallPlc),
            Polling,
            guard ?? new AllowAllPermissionGuard(),
            NullLogger<RunMonitorAddressService>.Instance);
        return (serviceInstance, Table, Polling);
    }

    // ── 默认元件表 ──────────────────────────────────────────────────────

    [Fact]
    public void DefaultComponentTable_ShouldContainAll15ComponentAndDefaultAddressSymbolValidFieldRulePlan()
    {
        var (serviceInstance, _, _) = NewService();

        var expectedAddress = new Dictionary<string, string>
        {
            ["RunStatus"] = "D6",
            ["CycleTime"] = "D1100",
            ["OutputCount"] = "D1102",
            ["TargetCount"] = "D1104",
            ["GoodCount"] = "D1106",
            ["BadCount"] = "D1108",
            // 左下状态条三盏灯（M 区位）+ 已锁附颗数（D 区 INT）（2026-10-16 现场要求）
            ["ScrewDone"] = "M490",
            ["GlueDone"] = "M491",
            ["ProcessDone"] = "M492",
            ["ScrewCount"] = "D1116",
            ["CmdDataClear"] = "M1000",
            ["CmdStart"] = "M200",
            ["CmdStop"] = "M201",
            ["CmdReset"] = "M202",
            ["CmdAlarmClear"] = "M203",
        };

        Assert.Equal(expectedAddress.Count, serviceInstance.ComponentCollection.Count);

        foreach (var (TagName, Address) in expectedAddress)
        {
            var ComponentItem = serviceInstance.ComponentCollection.Single(Entry => Entry.TagName == TagName);
            Assert.Equal(Address, ComponentItem.DefaultAddress);
            Assert.Equal(Address, ComponentItem.Address);   // 默认点位表下当前地址 = 默认地址
        }

        // 命令类与监控量的划分要与校验规则一致
        Assert.Equal(10, serviceInstance.ComponentCollection.Count(Entry => !Entry.IsCommand));
        Assert.Equal(5, serviceInstance.ComponentCollection.Count(Entry => Entry.IsCommand));

        // 三盏工位灯与颗数都是"只读监控量"：写错成 M 区会被校验拦下
        foreach (var TagName in new[] { "ScrewDone", "GlueDone", "ProcessDone", "ScrewCount" })
        {
            Assert.False(serviceInstance.ComponentCollection.Single(Entry => Entry.TagName == TagName).IsCommand);
        }
    }

    [Fact]
    public void StationStatusLampFillDAreaAddress_ValidateShouldReject()
    {
        // 三盏灯是 M 区位（FC01 读线圈）：填 D 区会去读一个字的位，值对不上
        var (serviceInstance, _, _) = NewService();

        var candidate = serviceInstance.ComponentCollection.Select(Entry => Entry.Clone()).ToList();
        candidate.First(Entry => Entry.TagName == "ScrewDone").Address = "D1110";

        var validateItem = serviceInstance.Validate(candidate);

        Assert.False(validateItem.IsValid);
        Assert.Contains(validateItem.ErrorCollection, error => error.Contains("锁附完成") && error.Contains("M 区"));
    }

    [Fact]
    public void ScrewedPieceCountFillMAreaAddress_ValidateShouldReject()
    {
        // 颗数是 D 区 INT：M 区位装不下一个计数
        var (serviceInstance, _, _) = NewService();

        var candidate = serviceInstance.ComponentCollection.Select(Entry => Entry.Clone()).ToList();
        candidate.First(Entry => Entry.TagName == "ScrewCount").Address = "M495";

        var validateItem = serviceInstance.Validate(candidate);

        Assert.False(validateItem.IsValid);
        Assert.Contains(validateItem.ErrorCollection, error => error.Contains("已锁附颗数") && error.Contains("D 区"));
    }

    [Fact]
    public void StationStateComponentAndOtherMonitoredValueOverlap_ValidateShouldReject()
    {
        var (serviceInstance, _, _) = NewService();

        // 已锁附颗数与良品数量落在同一地址：两条采集会互相覆盖读到的值
        var candidate = serviceInstance.ComponentCollection.Select(Entry => Entry.Clone()).ToList();
        candidate.First(Entry => Entry.TagName == "ScrewCount").Address = "D1106";

        var validateItem = serviceInstance.Validate(candidate);

        Assert.False(validateItem.IsValid);
        Assert.Contains(validateItem.ErrorCollection, error => error.Contains("重叠"));
    }

    [Fact]
    public void DefaultAddressTable_ValidateShouldAllThroughBeyond()
    {
        var (serviceInstance, _, _) = NewService();

        var validateItem = serviceInstance.Validate(serviceInstance.ComponentCollection.Select(Entry => Entry.Clone()).ToList());

        Assert.True(validateItem.IsValid, $"出厂默认地址必须能通过自己的校验：{validateItem.Summary}");
    }

    // ── 校验规则 ────────────────────────────────────────────────────────

    [Fact]
    public void MonitoredValueFillMAreaAddress_ValidateShouldReject()
    {
        var (serviceInstance, _, _) = NewService();

        var candidate = serviceInstance.ComponentCollection.Select(Entry => Entry.Clone()).ToList();
        candidate.First(Entry => Entry.TagName == "RunStatus").Address = "M300";

        var validateItem = serviceInstance.Validate(candidate);

        Assert.False(validateItem.IsValid);
        Assert.Contains(validateItem.ErrorCollection, error => error.Contains("M 区"));
    }

    [Fact]
    public void CommandFillDAreaAddress_ValidateShouldReject()
    {
        var (serviceInstance, _, _) = NewService();

        var candidate = serviceInstance.ComponentCollection.Select(Entry => Entry.Clone()).ToList();
        candidate.First(Entry => Entry.TagName == "CmdStart").Address = "D1150";

        var validateItem = serviceInstance.Validate(candidate);

        Assert.False(validateItem.IsValid);
        Assert.Contains(validateItem.ErrorCollection, error => error.Contains("M 区"));
    }

    [Fact]
    public void CandidateAddressMutualOverlap_ValidateShouldReject()
    {
        var (serviceInstance, _, _) = NewService();

        // 节拍 D1100 是 UInt32 占 2 字（D1100–D1101），当前产量挪到 D1101 即重叠
        var candidate = serviceInstance.ComponentCollection.Select(Entry => Entry.Clone()).ToList();
        candidate.First(Entry => Entry.TagName == "OutputCount").Address = "D1101";

        var validateItem = serviceInstance.Validate(candidate);

        Assert.False(validateItem.IsValid);
        Assert.Contains(validateItem.ErrorCollection, error => error.Contains("重叠"));
    }

    [Fact]
    public void CandidateAddressAndOtherPointConflict_ValidateShouldReject()
    {
        var (serviceInstance, _, _) = NewService();

        // D1221 是 AxisXPosition（Float32 占 2 字），不能被监控量占用
        var candidate = serviceInstance.ComponentCollection.Select(Entry => Entry.Clone()).ToList();
        candidate.First(Entry => Entry.TagName == "RunStatus").Address = "D1221";

        var validateItem = serviceInstance.Validate(candidate);

        Assert.False(validateItem.IsValid);
        Assert.Contains(validateItem.ErrorCollection, error => error.Contains("AxisXPosition"));
    }

    [Fact]
    public void CommandAtInAlarmBitRetainArea_ValidateShouldReject()
    {
        var (serviceInstance, _, _) = NewService();

        var candidate = serviceInstance.ComponentCollection.Select(Entry => Entry.Clone()).ToList();
        candidate.First(Entry => Entry.TagName == "CmdDataClear").Address = "M510";

        var validateItem = serviceInstance.Validate(candidate);

        Assert.False(validateItem.IsValid);
        Assert.Contains(validateItem.ErrorCollection, error => error.Contains("报警位区"));
    }

    [Fact]
    public void AddressIsEmptyOrUnableToParse_ValidateShouldReject()
    {
        var (serviceInstance, _, _) = NewService();

        var candidate = serviceInstance.ComponentCollection.Select(Entry => Entry.Clone()).ToList();
        candidate.First(Entry => Entry.TagName == "GoodCount").Address = "";
        candidate.First(Entry => Entry.TagName == "BadCount").Address = "X不是地址";

        var validateItem = serviceInstance.Validate(candidate);

        Assert.False(validateItem.IsValid);
        Assert.Equal(2, validateItem.ErrorCollection.Count);
    }

    // ── 保存链路 ────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveThroughBeyondAfter_PointAddressShouldUpdate_PersistAndRebuildSamplingCountPlan()
    {
        var (serviceInstance, pointTable, Polling) = NewService();

        var candidate = serviceInstance.ComponentCollection.Select(Entry => Entry.Clone()).ToList();
        candidate.First(Entry => Entry.TagName == "RunStatus").Address = "D100";
        candidate.First(Entry => Entry.TagName == "CmdDataClear").Address = "M1050";

        var eventTrigger = false;
        serviceInstance.AddressChanged += (_, _) => eventTrigger = true;

        await serviceInstance.SaveAsync(candidate);

        Assert.Equal("D100", pointTable.Find("RunStatus")!.Address);
        Assert.Equal("M1050", pointTable.Find("CmdDataClear")!.Address);
        Assert.Equal(1, pointTable.saveCount);          // 原子落盘（测试替身记录调用）
        Assert.Equal(1, Polling.RebuildCount);             // 采集计划重建，新地址即刻生效
        Assert.True(eventTrigger);
        Assert.Equal("D100", serviceInstance.FindAddress("RunStatus"));

        _output.WriteLine("保存链路完整：更新点位 → 落盘 → 重建采集计划均被触发");
    }

    [Fact]
    public async Task SaveValidateInvalid_ShouldThrowAndNotWriteAnyPoint()
    {
        var (serviceInstance, pointTable, Polling) = NewService();

        var candidate = serviceInstance.ComponentCollection.Select(Entry => Entry.Clone()).ToList();
        candidate.First(Entry => Entry.TagName == "RunStatus").Address = "M300";   // 监控量不许用 M 区

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => serviceInstance.SaveAsync(candidate));

        Assert.Equal("D6", pointTable.Find("RunStatus")!.Address);   // 原地址未被污染
        Assert.Equal(0, pointTable.saveCount);
        Assert.Equal(0, Polling.RebuildCount);
    }

    [Fact]
    public async Task FieldMissingPoint_SaveWhenShouldByFactoryDefinitionBuildMissingAndPersist()
    {
        // 复刻"老现场升级"：现场 tags.json 属于现场文件、不会被新版覆盖，
        // 里面没有新版自带的工位状态三灯与已锁附颗数。若保存直接失败，
        // 管理员就只能手工编辑 JSON——这条用例钉住"运行时补建"这条退路。
        var oldPointTable = new FakeTagRepository(
            new TagDefinition { Name = "RunStatus", Address = "D6", DataType = TagDataType.UInt16, AccessQuery = TagAccessQuery.Read, Group = TagGroup.Fast },
            new TagDefinition { Name = "CycleTime", Address = "D1100", DataType = TagDataType.UInt32, AccessQuery = TagAccessQuery.Read, Group = TagGroup.Fast },
            new TagDefinition { Name = "CmdStart", Address = "M200", DataType = TagDataType.Boolean, AccessQuery = TagAccessQuery.Write, Group = TagGroup.Command });

        var (serviceInstance, pointTable, Polling) = NewService(pointTable: oldPointTable);

        var candidate = serviceInstance.ComponentCollection.Select(Entry => Entry.Clone()).ToList();
        await serviceInstance.SaveAsync(candidate);

        // 缺的四个工位状态点位都补建了；现场已有的点位不得被重建（不覆盖现场配置）
        foreach (var Name in new[] { "ScrewDone", "GlueDone", "ProcessDone", "ScrewCount" })
        {
            Assert.Contains(Name, pointTable.BuildMissingRecord);
        }

        Assert.DoesNotContain("RunStatus", pointTable.BuildMissingRecord);
        Assert.DoesNotContain("CmdStart", pointTable.BuildMissingRecord);

        var pieceCountPoint = pointTable.Find("ScrewCount")!;
        Assert.Equal("D1116", pieceCountPoint.Address);
        Assert.Equal(TagDataType.Int16, pieceCountPoint.DataType);
        Assert.Equal(TagGroup.Fast, pieceCountPoint.Group);
        Assert.Equal(TagAccessQuery.Read, pieceCountPoint.AccessQuery);   // 只读监控量：不得写成可写点位

        // 三盏灯按出厂定义补成 M 区位（布尔 / 只读）：补成 D 区就永远读不到
        var screwPoint = pointTable.Find("ScrewDone")!;
        Assert.Equal("M490", screwPoint.Address);
        Assert.Equal(TagDataType.Boolean, screwPoint.DataType);
        Assert.Equal(TagAccessQuery.Read, screwPoint.AccessQuery);

        // 补建的点位随整表保存落盘，采集计划也重建了
        Assert.Equal(1, pointTable.saveCount);
        Assert.Equal(1, Polling.RebuildCount);
    }

    [Fact]
    public async Task NoneEditBindAddressPermission_SaveShouldBeRejected()
    {
        // 模拟"操作员越权调用服务层"：守卫直接拒绝一切
        var rejectGuard = new RejectAllPermissionGuard();
        var (serviceInstance, pointTable, Polling) = NewService(rejectGuard);

        var candidate = serviceInstance.ComponentCollection.Select(Entry => Entry.Clone()).ToList();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => serviceInstance.SaveAsync(candidate));

        Assert.Equal(0, pointTable.saveCount);
        Assert.Equal(0, Polling.RebuildCount);
    }

    /// <summary>全部拒绝的权限守卫（模拟无权限身份）。</summary>
    private sealed class RejectAllPermissionGuard : IPermissionGuard
    {
        public void WantRequire(Permission permissionItem) =>
            throw new UnauthorizedAccessException($"测试守卫：缺少 {permissionItem} 权限。");
    }
}
