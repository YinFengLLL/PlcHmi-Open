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
/// 历史记录模块的回归测试：数值缩放（INT/DINT + 小数位）、CSV 导出、
/// 配置校验、权限边界与按天落盘。
///
/// <para>端到端采集链路（触发位上升沿 → 落一条记录）见
/// <see cref="HistoryRecordIntegrationTest"/> —— 那里要起完整的宿主与虚拟 PLC，
/// 与这些纯逻辑用例分开更易定位。</para>
///
/// <para>与 <c>RecipeStoreRecoveryTests</c> 同属 <c>config-files</c> 集合：
/// 用例会写 <c>config/history-record.json</c>，与其他直接读写真实配置文件的用例必须串行。</para>
/// </summary>
[Collection("config-files")]
public class HistoryRecordTest
{
    // ── 数值缩放与 CSV ──────────────────────────────────────────────────

    [Theory]
    [InlineData(2, 600, "6.00")]
    [InlineData(0, 600, "600")]
    [InlineData(1, 600, "60.0")]
    [InlineData(3, 600, "0.600")]
    [InlineData(2, -1, "-0.01")]
    public void DecimalBitShouldPlcIntegerValueScaleAsEngineeringValueText(int DecimalBit, double SourceValue, string expected)
    {
        var Item = new HistoryDataItem { Name = "实际扭力", Address = "D2109", DataType = TagDataType.Int32, DecimalBit = DecimalBit };

        Assert.Equal(expected, Item.ShowText(SourceValue));
    }

    [Fact]
    public void CsvShouldWriteHeaderAndScaleAfterValue()
    {
        var Config = new HistoryRecordConfig
        {
            DataItemCollection =
            [
                new() { Name = "实际扭力", Address = "D2109", DataType = TagDataType.Int32, DecimalBit = 2, Unit = "N·m" },
                new() { Name = "结果", Address = "D2121", DataType = TagDataType.Int16 },
            ],
        };

        var Entry = new HistoryRecordEntry
        {
            Timestamp = new DateTime(2026, 9, 24, 20, 13, 46),
            NumericValueCollection = [600, 1],
        };

        var Text = HistoryRecordCsvCodec.Encode(Config, [Entry]);
        var Row = Text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("时间,实际扭力(N·m),结果", Row[0]);
        Assert.Equal("2026-09-24 20:13:46,6.00,1", Row[1]);
    }

    [Fact]
    public void CsvShouldPairHasCommaNumberColumnNameAddQuoteNumber()
    {
        var Config = new HistoryRecordConfig
        {
            DataItemCollection = [new() { Name = "扭力,上限", Address = "D2105", DataType = TagDataType.Int32 }],
        };

        var Text = HistoryRecordCsvCodec.Encode(Config, []);

        Assert.Contains("\"扭力,上限\"", Text);
    }

    // ── 配置校验 ────────────────────────────────────────────────────────

    [Fact]
    public async Task DefaultConfigShouldValidateThroughBeyond()
    {
        await using var Client = new FakeModbusClient();
        var serviceInstance = NewBuildService(Client);

        var validateItem = serviceInstance.Validate(serviceInstance.Config);

        Assert.True(validateItem.IsValid, validateItem.Summary);
        Assert.NotEmpty(serviceInstance.Config.DataItemCollection);
    }

    [Fact]
    public async Task TriggerBitFillAsWordComponentShouldBeRejected()
    {
        await using var Client = new FakeModbusClient();
        var serviceInstance = NewBuildService(Client);
        var Config = serviceInstance.Config.Clone();
        Config.TriggerAddress = "D2100";

        var validateItem = serviceInstance.Validate(Config);

        Assert.False(validateItem.IsValid);
        Assert.Contains("M 区", validateItem.Summary);
    }

    [Fact]
    public async Task DataItemFillAsBitAddressShouldBeRejected()
    {
        await using var Client = new FakeModbusClient();
        var serviceInstance = NewBuildService(Client);
        var Config = serviceInstance.Config.Clone();
        Config.DataItemCollection[0].Address = "D2100.1";

        var validateItem = serviceInstance.Validate(Config);

        Assert.False(validateItem.IsValid);
        Assert.Contains("位地址", validateItem.Summary);
    }

    [Fact]
    public async Task DataItemTypeNotIsINT_DINT_REALShouldBeRejected()
    {
        await using var Client = new FakeModbusClient();
        var serviceInstance = NewBuildService(Client);
        var Config = serviceInstance.Config.Clone();

        // 2026-10-24：REAL（32 位浮点）已成为合法类型，这里改用一个真正不支持的类型
        Config.DataItemCollection[0].DataType = TagDataType.Boolean;

        var validateItem = serviceInstance.Validate(Config);

        Assert.False(validateItem.IsValid);
        Assert.Contains("只支持 INT", validateItem.Summary);
    }

    /// <summary>
    /// 2026-10-24 现场要求：数据项支持 REAL（32 位浮点，占 2 字）。
    /// 校验要放行，且按 2 字做区间重叠检查（与下一项不能咬在一起）。
    /// </summary>
    [Fact]
    public async Task DataItemSupportRealAndByTwoWordValidateOverlap()
    {
        await using var Client = new FakeModbusClient();
        var serviceInstance = NewBuildService(Client);

        var singleColumn = new HistoryRecordConfig
        {
            Title = "浮点测试",
            TriggerAddress = "M8",
            DataItemCollection =
            [
                new() { Name = "胶量", Address = "D1420", DataType = TagDataType.Float32, DecimalBit = 3, Unit = "mg" },
            ],
        };

        Assert.True(serviceInstance.Validate(singleColumn).IsValid, serviceInstance.Validate(singleColumn).Summary);

        // 下一项紧挨着写在高位字上（D1421）→ 必须判为重叠
        var conflict = singleColumn.Clone();
        conflict.DataItemCollection.Add(new() { Name = "下一项", Address = "D1421", DataType = TagDataType.Int16 });

        var validateItem = serviceInstance.Validate(conflict);

        Assert.False(validateItem.IsValid);
        Assert.Contains("重叠", validateItem.Summary);
    }

    /// <summary>
    /// DINT 占两个字，紧挨着写下一项就会与它重叠——这是现场最容易犯的地址抄写错误，
    /// 必须拦下来，否则两列的取值会互相"咬"在一起且谁也说不清哪个对。
    /// </summary>
    [Fact]
    public async Task AddressAreaBetweenOverlapShouldBeRejected()
    {
        await using var Client = new FakeModbusClient();
        var serviceInstance = NewBuildService(Client);

        var Config = new HistoryRecordConfig
        {
            Title = "重叠测试",
            TriggerAddress = "M700",
            DataItemCollection =
            [
                new() { Name = "实际扭力", Address = "D2109", DataType = TagDataType.Int32, DecimalBit = 2 },
                new() { Name = "高位字", Address = "D2110", DataType = TagDataType.Int16 },
            ],
        };

        var validateItem = serviceInstance.Validate(Config);

        Assert.False(validateItem.IsValid);
        Assert.Contains("重叠", validateItem.Summary);
    }

    [Fact]
    public async Task NameDuplicateAndDecimalBitBeyondBoundaryShouldBeRejected()
    {
        await using var Client = new FakeModbusClient();
        var serviceInstance = NewBuildService(Client);

        var Config = new HistoryRecordConfig
        {
            Title = "非法配置",
            TriggerAddress = "M700",
            DataItemCollection =
            [
                new() { Name = "扭力", Address = "D2109", DataType = TagDataType.Int32, DecimalBit = 2 },
                new() { Name = "扭力", Address = "D2111", DataType = TagDataType.Int32, DecimalBit = 9 },
            ],
        };

        var validateItem = serviceInstance.Validate(Config);

        Assert.False(validateItem.IsValid);
        Assert.Contains("重复", validateItem.Summary);
        Assert.Contains("小数位", validateItem.Summary);
    }

    // ── 权限边界（服务层，R5） ──────────────────────────────────────────

    [Fact]
    public async Task OperatorSaveConfigOrClearRemoveHistoryShouldBeRejected()
    {
        await using var Client = new FakeModbusClient();

        var User = new UserService(NullLogger<UserService>.Instance);
        var serviceInstance = NewBuildService(Client, new UserPermissionGuard(User, NullLogger<UserPermissionGuard>.Instance));

        var Config = serviceInstance.Config.Clone();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => serviceInstance.SaveConfigAsync(Config));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => serviceInstance.ClearRemoveAllAsync());
    }

    [Fact]
    public async Task SaveConfigAfterShouldBeAbleToPersistAndBeNewRealCaseReadBack()
    {
        await using var Client = new FakeModbusClient();

        var originalFile = File.Exists(ConfigPath.HistoryRecordConfig)
            ? await File.ReadAllTextAsync(ConfigPath.HistoryRecordConfig)
            : null;

        try
        {
            var serviceInstance = NewBuildService(Client);

            var Config = serviceInstance.Config.Clone();
            Config.Title = "单列数据体";
            Config.RetainDays = 30;
            Config.DataItemCollection = [new() { Name = "实际扭力", Address = "D2109", DataType = TagDataType.Int32, DecimalBit = 2 }];

            await serviceInstance.SaveConfigAsync(Config);

            var newRealCase = NewBuildService(Client);
            await newRealCase.InitializeAsync();

            Assert.Equal("单列数据体", newRealCase.Config.Title);
            Assert.Equal(30, newRealCase.Config.RetainDays);
            Assert.Single(newRealCase.Config.DataItemCollection);
            Assert.Equal("D2109", newRealCase.Config.DataItemCollection[0].Address);
        }
        finally
        {
            if (originalFile is not null)
            {
                await File.WriteAllTextAsync(ConfigPath.HistoryRecordConfig, originalFile);
            }
        }
    }

    // ── 按天落盘 ────────────────────────────────────────────────────────

    [Fact]
    public async Task ByDaysPersistShouldBeAbleToSurviveHoldRestartAndCanWholeBodyClearRemove()
    {
        // 用远期日期，避免与真实运行数据 / 其他用例互相干扰
        var dateValue = new DateTime(2035, 6, 1);

        try
        {
            var Store = new HistoryRecordStore(NullLogger<HistoryRecordStore>.Instance);
            await Store.InitializeAsync(dateValue, 90);

            await Store.AppendAsync(new HistoryRecordEntry { Timestamp = dateValue.AddHours(8), NumericValueCollection = [600, 1] });
            await Store.AppendAsync(new HistoryRecordEntry { Timestamp = dateValue.AddHours(9), NumericValueCollection = [620, 1] });

            Assert.Equal(2, (await Store.ReadDayAsync(dateValue)).Count);
            Assert.Contains(dateValue, Store.HasRecordDateCollection());

            // 新实例 = 模拟重启：当天已有记录必须先被装载，再追加不能覆盖它们
            var restartAfter = new HistoryRecordStore(NullLogger<HistoryRecordStore>.Instance);
            await restartAfter.InitializeAsync(dateValue, 90);
            await restartAfter.AppendAsync(new HistoryRecordEntry { Timestamp = dateValue.AddHours(10), NumericValueCollection = [640, 1] });

            var All = await restartAfter.ReadDayAsync(dateValue);
            Assert.Equal(3, All.Count);
            Assert.Equal(600, All[0].NumericValueCollection[0]);
            Assert.Equal(640, All[^1].NumericValueCollection[0]);

            restartAfter.ClearRemoveAll();
            Assert.Empty(await restartAfter.ReadDayAsync(dateValue));
            Assert.DoesNotContain(dateValue, restartAfter.HasRecordDateCollection());
        }
        finally
        {
            foreach (var path in new[]
                     {
                         ConfigPath.HistoryRecordFile(dateValue),
                         ConfigPath.HistoryRecordFile(dateValue) + ".bak",
                     })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public async Task ExceedPeriodFileShouldAtInitializeWhenBeClean()
    {
        var oldDate = new DateTime(2035, 1, 1);
        var TodayDate = new DateTime(2035, 6, 1);

        try
        {
            var Store = new HistoryRecordStore(NullLogger<HistoryRecordStore>.Instance);
            await Store.InitializeAsync(oldDate, 90);
            await Store.AppendAsync(new HistoryRecordEntry { Timestamp = oldDate.AddHours(8), NumericValueCollection = [1] });

            var cleanNumber = Store.CleanBeyondPeriod(TodayDate, 90);

            Assert.True(cleanNumber >= 1);
            Assert.DoesNotContain(oldDate, Store.HasRecordDateCollection());
        }
        finally
        {
            foreach (var path in new[]
                     {
                         ConfigPath.HistoryRecordFile(oldDate),
                         ConfigPath.HistoryRecordFile(oldDate) + ".bak",
                     })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    // ── 辅助 ────────────────────────────────────────────────────────────

    /// <summary>装载一份被测服务。客户端由用例持有并释放（<c>await using</c>），与服务同生命周期。</summary>
    private static HistoryRecordService NewBuildService(FakeModbusClient Client, IPermissionGuard? Permission = null) =>
        new(new InovanceAddressParser(PlcSeries.SmallPlc),
            Client,
            Permission ?? new AllowAllPermissionGuard(),
            new HistoryRecordStore(NullLogger<HistoryRecordStore>.Instance),
            NullLogger<HistoryRecordService>.Instance);
}
