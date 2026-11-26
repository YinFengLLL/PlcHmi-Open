using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PlcHmi.Business.Configuration;
using PlcHmi.Business.Services;
using PlcHmi.Communication.Inovance;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;
using Xunit.Abstractions;

namespace PlcHmi.Tests;

/// <summary>
/// 管理员编辑配置条目的回归测试。
///
/// <para><b>背景（2026-09-11 需求方反馈）：</b>旧版校验用 <c>ReferenceEquals</c> 排除自身，
/// 但编辑对话框操作的是 Clone 副本，引用比较永远不成立——管理员只要编辑已有条目
/// （改名字、改地址）就弹「已被占用」。本文件锁死这个行为，防止回归。</para>
/// </summary>
/// <remarks>
/// 与其它读写 <c>config/</c> 的用例同集合：本组会临时替换 <c>param-tables.json</c>
/// 并保存 <c>manual-widgets.json</c>，并行跑会和其它用例抢同一个文件。
/// </remarks>
[Collection("config-files")]
public class AdminEditTest
{
    private readonly ITestOutputHelper _output;

    public AdminEditTest(ITestOutputHelper Output) => _output = Output;

    private static AlarmDefinitionService NewAlarmService() =>
        new(new InovanceAddressParser(PlcSeries.SmallPlc),
            NullLogger<AlarmDefinitionService>.Instance,
            new AllowAllPermissionGuard());

    private static ParameterTableService NewParameterService()
    {
        var Parser = new InovanceAddressParser(PlcSeries.SmallPlc);

        // TagWriter 只在真正写 PLC 时用到；Validate 不触碰它。
        TagWriter Writer = null!;
        return new ParameterTableService(
            Parser, Writer, NullLogger<ParameterTableService>.Instance, new AllowAllPermissionGuard());
    }

    private static ManualComponentService NewComponentService() =>
        new(new InovanceAddressParser(PlcSeries.SmallPlc),
            NullLogger<ManualComponentService>.Instance,
            new AllowAllPermissionGuard());

    // ── 报警编辑 ────────────────────────────────────────────────────────

    [Fact]
    public async Task EditAlreadyHasAlarm_ChangeNameWordAndAddress_ShouldNotReportAlreadyBeOccupied()
    {
        var serviceInstance = NewAlarmService();
        await serviceInstance.InitializeAsync();

        var Original = serviceInstance.DefinitionCollection.First(EntryItem => EntryItem.Id == "A001");
        var alreadyEdit = Original.Clone();
        Assert.NotSame(Original, alreadyEdit);   // 编辑走的是克隆副本——旧 bug 的前提

        alreadyEdit.Message = "新报警文本";
        alreadyEdit.Address = "M510";

        var validateItem = serviceInstance.Validate(alreadyEdit, isUpdate: true);

        Assert.True(validateItem.IsValid, $"编辑已有报警不应报错：{validateItem.Summary}");
        Assert.DoesNotContain("已被占用", validateItem.Summary);
    }

    [Fact]
    public async Task AddAlarm_NumberAndAlreadyHasEntrySame_ShouldStillBeBlocked()
    {
        var serviceInstance = NewAlarmService();
        await serviceInstance.InitializeAsync();

        var currentHas = serviceInstance.DefinitionCollection.First(EntryItem => EntryItem.Id == "A001");

        // 新增一条编号也叫 A001 的报警——必须拦住，否则列表里出现两条 A001
        var newUser = currentHas.Clone();
        var validateItem = serviceInstance.Validate(newUser, isUpdate: false);

        Assert.False(validateItem.IsValid);
        Assert.Contains("已被占用", validateItem.Summary);
    }

    [Fact]
    public async Task EditAlarm_ValidateThroughBeyondAfterUpdateShouldBeAbleToByNumberPositioning()
    {
        var serviceInstance = NewAlarmService();
        await serviceInstance.InitializeAsync();

        var Original = serviceInstance.DefinitionCollection.First(EntryItem => EntryItem.Id == "A002");
        var alreadyEdit = Original.Clone();
        alreadyEdit.Message = "更新后的文本";

        Assert.True(serviceInstance.Validate(alreadyEdit, isUpdate: true).IsValid);

        serviceInstance.Update(alreadyEdit);   // 不应抛「找不到编号」

        Assert.Equal("更新后的文本", serviceInstance.DefinitionCollection.Single(EntryItem => EntryItem.Id == "A002").Message);
    }

    // ── 参数表编辑 ──────────────────────────────────────────────────────

    /// <summary>用例自带两条延时（现场出厂配置只有按钮表，延时表为空）。</summary>
    private const string testDelayTable = """
        {
          "delayCollection": [
            {
              "id": "delay-1",
              "name": "测试延时1",
              "address": "D1000",
              "unit": "s",
              "min": 0,
              "max": 2147483.647,
              "enabled": true,
              "order": 1
            },
            {
              "id": "delay-2",
              "name": "测试延时2",
              "address": "D1002",
              "unit": "s",
              "min": 0,
              "max": 2147483.647,
              "enabled": true,
              "order": 2
            }
          ],
          "buttonCollection": [
            {
              "id": "btn-1",
              "name": "MES 开关",
              "address": "M5000",
              "enabled": true,
              "order": 1
            }
          ]
        }
        """;

    private static async Task<string> SafeInstallTestDelayTable()
    {
        var path = ConfigPath.ParameterTable;
        var Original = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, testDelayTable);
        return Original;
    }

    [Fact]
    public async Task EditAlreadyHasDelayEntry_ChangeNameWordAndAddress_ShouldNotReportAlreadyBeOccupied()
    {
        var backup = await SafeInstallTestDelayTable();

        try
        {
            var serviceInstance = NewParameterService();
            await serviceInstance.InitializeAsync();

            var Original = serviceInstance.DelayCollection.First(EntryItem => EntryItem.Id == "delay-1");
            var alreadyEdit = Original.Clone();

            alreadyEdit.Name = "改过的延时名";
            alreadyEdit.Address = "D1050";   // 延时专属区 D1000–D1099 内

            var validateItem = serviceInstance.Validate(ParameterTableCategory.Delay, alreadyEdit, isUpdate: true);

            Assert.True(validateItem.IsValid, $"编辑已有延时不应报错：{validateItem.Summary}");
        }
        finally
        {
            await File.WriteAllTextAsync(ConfigPath.ParameterTable, backup);
        }
    }

    [Fact]
    public async Task EditAlreadyHasButtonEntry_ShouldNotReportAlreadyBeOccupied()
    {
        var serviceInstance = NewParameterService();
        await serviceInstance.InitializeAsync();

        var Original = serviceInstance.ButtonCollection.First(EntryItem => EntryItem.Id == "btn-1");
        var alreadyEdit = Original.Clone();
        alreadyEdit.Name = "MES 开关（改）";
        alreadyEdit.Address = "M5001";

        var validateItem = serviceInstance.Validate(ParameterTableCategory.Button, alreadyEdit, isUpdate: true);

        Assert.True(validateItem.IsValid, $"编辑已有按钮不应报错：{validateItem.Summary}");
    }

    [Fact]
    public async Task AddParameterEntry_NumberAndAlreadyHasEntrySame_ShouldStillBeBlocked()
    {
        var backup = await SafeInstallTestDelayTable();

        try
        {
            var serviceInstance = NewParameterService();
            await serviceInstance.InitializeAsync();

            var newUser = serviceInstance.DelayCollection.First().Clone();
            var validateItem = serviceInstance.Validate(ParameterTableCategory.Delay, newUser, isUpdate: false);

            Assert.False(validateItem.IsValid);
            Assert.Contains("已被占用", validateItem.Summary);
        }
        finally
        {
            await File.WriteAllTextAsync(ConfigPath.ParameterTable, backup);
        }
    }

    [Fact]
    public async Task ServiceLayerButtonSetValidate_SameOneEntryDuplicateValidate_ShouldNotReportAlreadyBeOccupied()
    {
        // SetButtonAsync / WriteDelayAsync 内部会再校验一次（isUpdate: true 路径）。
        // 这里直接模拟同一条目反复 Validate：旧实现对列表内原始对象也会自撞。
        var serviceInstance = NewParameterService();
        await serviceInstance.InitializeAsync();

        var pendingValidateEntry = serviceInstance.ButtonCollection.First();

        for (var round = 0; round < 3; round++)
        {
            var validateItem = serviceInstance.Validate(ParameterTableCategory.Button, pendingValidateEntry, isUpdate: true);
            Assert.True(validateItem.IsValid, $"第 {round + 1} 次校验不应报错：{validateItem.Summary}");
        }
    }

    // ── 手动页气缸编辑（需求方反馈「管理员无法编辑气缸」的业务层实证） ──

    [Fact]
    public async Task EditCylinder_ChangeNameWordAndAddress_ValidateThroughBeyondAndCanPositioningUpdate()
    {
        var serviceInstance = NewComponentService();
        await serviceInstance.InitializeAsync();

        // 现场配置里 cyl-1 是"螺丝机开盖"按钮，气缸是 cyl-2（螺丝机定位气缸）
        var Original = serviceInstance.ComponentCollection.First(componentEntry => componentEntry.Id == "cyl-2");
        Assert.Equal(ManualComponentType.Cylinder, Original.Kind);

        // 与 ManualViewModel.EditWidgetAsync 相同的编辑路径：Clone → 改字段 → Validate → Update
        var alreadyEdit = Original.Clone();
        alreadyEdit.Name = "上料气缸（改名）";
        alreadyEdit.Address = "M226";

        var validateItem = serviceInstance.Validate(alreadyEdit);
        Assert.True(validateItem.IsValid, $"气缸编辑校验不应报错：{validateItem.Summary}");

        serviceInstance.Update(alreadyEdit);   // 不应抛「找不到 Id」

        var updated = serviceInstance.ComponentCollection.Single(componentEntry => componentEntry.Id == "cyl-2");
        Assert.Equal("上料气缸（改名）", updated.Name);
        Assert.Equal("M226", updated.Address);
    }

    [Fact]
    public async Task EditCylinder_SaveReloadAfterChangeMoveShouldPersist()
    {
        var serviceInstance = NewComponentService();
        await serviceInstance.InitializeAsync();

        var Original = serviceInstance.ComponentCollection.First(componentEntry => componentEntry.Id == "cyl-2");
        var alreadyEdit = Original.Clone();
        alreadyEdit.Name = "压装气缸（改名）";

        Assert.True(serviceInstance.Validate(alreadyEdit).IsValid);
        serviceInstance.Update(alreadyEdit);
        await serviceInstance.SaveAsync();

        // 新建一个服务实例重新加载，验证改动真的落了盘
        var reloaded = NewComponentService();
        await reloaded.InitializeAsync();

        var alreadyPersist = reloaded.ComponentCollection.Single(componentEntry => componentEntry.Id == "cyl-2");
        Assert.Equal("压装气缸（改名）", alreadyPersist.Name);

        _output.WriteLine($"落盘验证通过：{alreadyPersist.Id} → {alreadyPersist.Name} @ {alreadyPersist.Address}");
    }

    [Fact]
    public void CylinderBindNonMAreaAddress_ValidateShouldReject()
    {
        var serviceInstance = NewComponentService();

        var ComponentItem = new ManualComponentDefinition
        {
            Id = "cyl-x", Kind = ManualComponentType.Cylinder, Name = "非法气缸", Order = 9,
            Address = "Y0",   // Y 区专供输出，上位机不允许直接操作
        };

        var validateItem = serviceInstance.Validate(ComponentItem);

        Assert.False(validateItem.IsValid);
        Assert.Contains("M 区", validateItem.Summary);
    }
}
