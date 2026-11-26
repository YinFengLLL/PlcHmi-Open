using Microsoft.Extensions.Logging.Abstractions;
using PlcHmi.Business.Configuration;
using PlcHmi.Business.Services;
using PlcHmi.Communication.Inovance;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;
using PlcHmi.Tests.Fake;

namespace PlcHmi.Tests;

/// <summary>
/// 批次 F 回归：辅助地址「分组 + 键」定位与每组 <see cref="AuxAddressItem.perGroupUpperLimit"/> 个上限。
///
/// <para><b>守的缺陷</b>：<c>应用辅助地址</c> 旧逻辑全局按键 <c>FirstOrDefault</c> 就地更新，
/// 撞键即覆盖旧项；<c>移除</c> 旧逻辑全局按键 <c>RemoveAll</c>，会误删跨组同名项。
/// 契约是「键（同一分组内唯一）」，所以定位与删除都必须同时匹配分组。</para>
///
/// <para><b>为什么只测服务层</b>：测试项目只引用 Core / Communication / Business，
/// 没有引用 App（WPF），对话框里的「下一个未占用键」私有方法无法在此直接调用，
/// 其键唯一化靠代码审查保证；这里测的是服务层「分组 + 键」与 30 上限两条硬约束。</para>
///
/// <para><b>配置隔离</b>：用自建的 <see cref="RecipeConfig"/> 直接构造真实服务，
/// 落盘仍会写测试输出目录的 <c>config/recipe-config.json</c>，故每个用例都备份 / 还原；
/// 加入 <c>config-files</c> 集合以禁用并行，避免与其它配置用例互相看到半途状态。</para>
/// </summary>
[Collection("config-files")]
public class AuxAddressUpperLimitTest
{
    private const string screwGroup = "锁附";
    private const string dispenseGroup = "点胶";
    private const string calibrationGroup = "标定";

    // ── 构造 ────────────────────────────────────────────────────────────

    /// <summary>构造一个最小但可保存的配置：点表要有「锁附 / 点胶」两页（点表页保存要查表）。</summary>
    private static RecipeConfig NewConfig(IEnumerable<AuxAddressItem> auxAddressTable, IEnumerable<ActionButtonItem>? actionButtonTable = null)
    {
        return new RecipeConfig
        {
            PointTable = new PointTableConfig
            {
                PointTable =
                [
                    new PointTableDefinition { Key = screwGroup, Name = screwGroup, JogGroup = screwGroup },
                    new PointTableDefinition { Key = dispenseGroup, Name = dispenseGroup, JogGroup = dispenseGroup },
                ],
            },
            AuxAddress = auxAddressTable.ToList(),
            ActionButton = actionButtonTable is null ? new List<ActionButtonItem>() : actionButtonTable.ToList(),
        };
    }

    /// <summary>用自建配置构造真实服务（不实现 页面地址服务接口 替身，避免接口新增方法时编译失败）。</summary>
    private static PageAddressService NewService(RecipeConfig configPairObject, IModbusClient Connect)
    {
        var Parser = new InovanceAddressParser(PlcSeries.SmallPlc);

        return new PageAddressService(
            Connect,
            new TagWriter(Connect, new TagRepository(NullLogger<TagRepository>.Instance), Parser,
                NullLogger<TagWriter>.Instance),
            configPairObject,
            new AllowAllPermissionGuard(),
            NullLogger<PageAddressService>.Instance);
    }

    private static AuxAddressItem AuxItem(string Key, string Group, string Address = "D3900", string Name = "") =>
        new()
        {
            Key = Key,
            Name = string.IsNullOrEmpty(Name) ? Key : Name,
            Group = Group,
            Address = Address,
        };

    private static AddressEditItem EditItem(
        string Key, string Group, string Address = "D3900", string Name = "", bool Insert = false) =>
        new()
        {
            Category = "辅助地址",
            Key = Key,
            Name = string.IsNullOrEmpty(Name) ? Key : Name,
            AddressText = Address,
            DataTypeText = "Float32",
            Group = Group,
            Deletable = true,
            Insert = Insert,
        };

    private static AddressEditItem ButtonEditItem(string Key, string Group, string Address = "M490", string Name = "") =>
        new()
        {
            Category = "动作按钮",
            Key = Key,
            Name = string.IsNullOrEmpty(Name) ? Key : Name,
            AddressText = Address,
            DataTypeText = "布尔",
            Group = Group,
            Deletable = true,
        };

    private static ActionButtonItem ButtonItem(string Key, string Group, bool Fixed = false, string Address = "M490") =>
        new()
        {
            Key = Key,
            Name = Key,
            Group = Group,
            Address = Address,
            Fixed = Fixed,
        };

    /// <summary>两个保存入口都走同一套应用 / 校验逻辑，用 Theory 一次覆盖。</summary>
    private static Task Save(PageAddressService serviceInstance, bool calibrationPage, AddressEditRequest Request) =>
        calibrationPage ? serviceInstance.SaveCalibrationPageAsync(Request) : serviceInstance.SavePointTablePageAsync(screwGroup, Request);

    private static async Task<string> BackupRecipe() =>
        await File.ReadAllTextAsync(ConfigPath.RecipeConfig);

    private static async Task RestoreRecipe(string Original) =>
        await File.WriteAllTextAsync(ConfigPath.RecipeConfig, Original);

    // ── 用例 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 已有 <c>辅助地址9</c>（锁附组）时，新增一个同键但不同分组（标定组）的项：
    /// 旧逻辑全局按键命中旧项就地改掉 → 旧项被覆盖、总数不变；新逻辑按「分组 + 键」应新建。
    /// </summary>
    [Fact]
    public async Task SameKeyDifferentGroupAuxAddressAddShouldNotOverwriteOldItem()
    {
        var Original = await BackupRecipe();

        try
        {
            var Config = NewConfig([AuxItem("辅助地址9", screwGroup, "D3366", "ZZ轴锁附终点")]);
            await using var Client = new FakeModbusClient();
            var serviceInstance = NewService(Config, Client);

            var Request = new AddressEditRequest
            {
                Title = "标定 · 其它点",
                DefaultGroup = calibrationGroup,
                ItemCollection = [EditItem("辅助地址9", calibrationGroup, "D3900", "标定新增项", Insert: true)],
            };

            await serviceInstance.SaveCalibrationPageAsync(Request);

            Assert.Equal(2, Config.AuxAddress.Count);

            var oldItem = Config.AuxAddress.Single(Item => Item.Group == screwGroup && Item.Key == "辅助地址9");
            Assert.Equal("ZZ轴锁附终点", oldItem.Name);
            Assert.Equal("D3366", oldItem.Address);

            var newItem = Config.AuxAddress.Single(Item => Item.Group == calibrationGroup && Item.Key == "辅助地址9");
            Assert.Equal("标定新增项", newItem.Name);
            Assert.Equal("D3900", newItem.Address);
        }
        finally
        {
            await RestoreRecipe(Original);
        }
    }

    /// <summary>
    /// 两个分组各有一个同名键，改「后出现的那个分组」时不得串改前一个：
    /// 旧 <c>FirstOrDefault</c> 全局按键会先命中列表靠前的项 → 改点胶却把锁附改掉。
    /// </summary>
    [Fact]
    public async Task TwoGroupSameNameKeyChangeItsOneShouldNotSerialChangeAnotherOne()
    {
        var Original = await BackupRecipe();

        try
        {
            // 顺序刻意：被误命中的「锁附」放前面
            var Config = NewConfig(
            [
                AuxItem("辅助地址7", screwGroup, "D3346", "锁附原值"),
                AuxItem("辅助地址7", dispenseGroup, "D3500", "点胶原值"),
            ]);
            await using var Client = new FakeModbusClient();
            var serviceInstance = NewService(Config, Client);

            var Request = new AddressEditRequest
            {
                Title = "点胶示教",
                DefaultGroup = dispenseGroup,
                ItemCollection = [EditItem("辅助地址7", dispenseGroup, "D3800", "点胶改后")],
            };

            await serviceInstance.SavePointTablePageAsync(dispenseGroup, Request);

            Assert.Equal(2, Config.AuxAddress.Count);
            Assert.Equal("锁附原值", Config.AuxAddress.Single(Item => Item.Group == screwGroup).Name);
            Assert.Equal("D3346", Config.AuxAddress.Single(Item => Item.Group == screwGroup).Address);

            var dispenseItem = Config.AuxAddress.Single(Item => Item.Group == dispenseGroup);
            Assert.Equal("点胶改后", dispenseItem.Name);
            Assert.Equal("D3800", dispenseItem.Address);
        }
        finally
        {
            await RestoreRecipe(Original);
        }
    }

    /// <summary>动作按钮同名的分组 + 键定位（不调用历史 查找动作按钮 全局按键方法）。</summary>
    [Fact]
    public async Task TwoGroupSameNameActionButtonChangeItsOneShouldNotSerialChangeAnotherOne()
    {
        var Original = await BackupRecipe();

        try
        {
            var Config = NewConfig([],
            [
                ButtonItem("动作1", screwGroup, Address: "M490"),
                ButtonItem("动作1", dispenseGroup, Address: "M491"),
            ]);
            await using var Client = new FakeModbusClient();
            var serviceInstance = NewService(Config, Client);

            var Request = new AddressEditRequest
            {
                Title = "点胶示教",
                DefaultGroup = dispenseGroup,
                ItemCollection = [ButtonEditItem("动作1", dispenseGroup, "M493", "点胶按钮改后")],
            };

            await serviceInstance.SavePointTablePageAsync(dispenseGroup, Request);

            Assert.Equal(2, Config.ActionButton.Count);
            Assert.Equal("M490", Config.ActionButton.Single(Item => Item.Group == screwGroup).Address);

            var dispenseButton = Config.ActionButton.Single(Item => Item.Group == dispenseGroup);
            Assert.Equal("M493", dispenseButton.Address);
            Assert.Equal("点胶按钮改后", dispenseButton.Name);
        }
        finally
        {
            await RestoreRecipe(Original);
        }
    }

    /// <summary>
    /// 连续新增到 30 个不报重复键，第 31 个被两个保存入口的兜底校验拒绝（落盘前抛异常）。
    /// </summary>
    [Theory]
    [InlineData(false, screwGroup)]
    [InlineData(true, calibrationGroup)]
    public async Task ConnectContinueAddTo30ShouldSucceedAndKeyNoDuplicateAndNo31BeReject(bool calibrationPage, string Group)
    {
        var Original = await BackupRecipe();

        try
        {
            var Config = NewConfig([]);
            await using var Client = new FakeModbusClient();
            var serviceInstance = NewService(Config, Client);

            var fullQuotaRequest = new AddressEditRequest
            {
                Title = "满额",
                DefaultGroup = Group,
                ItemCollection = Enumerable.Range(1, AuxAddressItem.perGroupUpperLimit)
                    .Select(Sequence => EditItem($"辅助地址{Sequence}", Group, Insert: true))
                    .ToList(),
            };

            await Save(serviceInstance, calibrationPage, fullQuotaRequest);

            Assert.Equal(AuxAddressItem.perGroupUpperLimit, Config.AuxAddress.Count);
            Assert.Equal(
                AuxAddressItem.perGroupUpperLimit,
                Config.AuxAddress.Select(Item => Item.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());

            // 第 31 个：新增项已被加入 _配置.辅助地址，校验必须覆盖新增后的状态
            var exceedQuotaRequest = new AddressEditRequest
            {
                Title = "超额",
                DefaultGroup = Group,
                ItemCollection = [EditItem($"辅助地址{AuxAddressItem.perGroupUpperLimit + 1}", Group, Insert: true)],
            };

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => Save(serviceInstance, calibrationPage, exceedQuotaRequest));

            Assert.Contains($"超过 {AuxAddressItem.perGroupUpperLimit}", exception.Message, StringComparison.Ordinal);
            Assert.Equal(AuxAddressItem.perGroupUpperLimit + 1, Config.AuxAddress.Count); // 校验在落盘前抛，内存里已 31
        }
        finally
        {
            await RestoreRecipe(Original);
        }
    }

    /// <summary>
    /// 跨组同名辅助地址删除：旧逻辑全局按键 <c>RemoveAll</c> 会把两组的同名项一起删掉
    /// （别的分组的项不在本页请求里，删了就真的没了，不会被重新加回来）。
    /// </summary>
    [Fact]
    public async Task CrossGroupSameNameAuxAddressDeleteShouldNotWrongDelete()
    {
        var Original = await BackupRecipe();

        try
        {
            var Config = NewConfig(
            [
                AuxItem("辅助地址8", screwGroup, "D3364", "锁附ZZ起点"),
                AuxItem("辅助地址8", dispenseGroup, "D3500", "点胶ZZ起点"),
                AuxItem("辅助地址20", dispenseGroup, "D3810", "点胶保留项"),
            ]);
            await using var Client = new FakeModbusClient();
            var serviceInstance = NewService(Config, Client);

            // 点胶页编辑器只列出本组条目：删的是点胶组的 辅助地址8，
            // 锁附组的同名项根本不在这个请求里（这正是误删会丢数据的场景）。
            // 项集合里留一个本组正常项，模拟"删一行、其余行照常回写"。
            var Request = new AddressEditRequest
            {
                Title = "点胶示教",
                DefaultGroup = dispenseGroup,
                ItemCollection = [EditItem("辅助地址20", dispenseGroup, "D3810", "点胶保留项")],
                DeletedItem = [EditItem("辅助地址8", dispenseGroup, "D3500", "点胶ZZ起点")],
            };

            await serviceInstance.SavePointTablePageAsync(dispenseGroup, Request);

            // 正确结果：锁附同名项 + 点胶保留项（旧逻辑全局 RemoveAll 会把锁附同名项也删掉 → 只剩 1 条）
            Assert.Equal(2, Config.AuxAddress.Count);
            var screwItem = Config.AuxAddress.Single(Item => Item.Group == screwGroup && Item.Key == "辅助地址8");
            Assert.Equal("锁附ZZ起点", screwItem.Name);
            Assert.Equal("D3364", screwItem.Address);
            Assert.Contains(Config.AuxAddress, Item => Item.Group == dispenseGroup && Item.Key == "辅助地址20");
            Assert.DoesNotContain(Config.AuxAddress, Item => Item.Group == dispenseGroup && Item.Key == "辅助地址8");
        }
        finally
        {
            await RestoreRecipe(Original);
        }
    }

    /// <summary>
    /// 跨组同名动作按钮删除 + 固定按钮保护：只删目标组的非固定项，
    /// 同组固定按钮（<c>!甲.固定</c>）与其它组同名项都要留下。
    /// </summary>
    [Fact]
    public async Task CrossGroupSameNameActionButtonDeleteShouldNotWrongDeleteAndFixedButtonAffectedByKeepProtect()
    {
        var Original = await BackupRecipe();

        try
        {
            var Config = NewConfig([],
            [
                ButtonItem("动作1", screwGroup, Address: "M490"),
                ButtonItem("动作1", dispenseGroup, Address: "M491"),
                ButtonItem("动作2", screwGroup, Fixed: true, Address: "M492"),
                ButtonItem("动作9", dispenseGroup, Address: "M494"),
            ]);
            await using var Client = new FakeModbusClient();
            var serviceInstance = NewService(Config, Client);

            // 点胶页只删本组的 动作1；锁附组的同名项与固定按钮都不在请求里。
            // 旧逻辑全局按键 RemoveAll 会把锁附的 动作1 也删掉（固定按钮靠 !固定 保护留下）。
            var Request = new AddressEditRequest
            {
                Title = "点胶示教",
                DefaultGroup = dispenseGroup,
                ItemCollection =
                [
                    ButtonEditItem("动作9", dispenseGroup, "M494", "点胶保留按钮"),
                ],
                DeletedItem = [ButtonEditItem("动作1", dispenseGroup, "M491", "动作1")],
            };

            await serviceInstance.SavePointTablePageAsync(dispenseGroup, Request);

            // 锁附同名项 + 锁附固定按钮 + 点胶保留按钮 = 3（旧逻辑误删后只剩 2）
            Assert.Equal(3, Config.ActionButton.Count);
            Assert.Contains(Config.ActionButton, Item => Item.Group == screwGroup && Item.Key == "动作1" && Item.Address == "M490");
            Assert.Contains(Config.ActionButton, Item => Item.Key == "动作2" && Item.Fixed && Item.Address == "M492");
            Assert.Contains(Config.ActionButton, Item => Item.Group == dispenseGroup && Item.Key == "动作9");
            Assert.DoesNotContain(Config.ActionButton, Item => Item.Group == dispenseGroup && Item.Key == "动作1");
        }
        finally
        {
            await RestoreRecipe(Original);
        }
    }
}
