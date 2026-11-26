using System.Text;
using PlcHmi.Business.Configuration;
using PlcHmi.Business.Services;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Tests;

/// <summary>
/// 变量表 CSV 解析与变量表服务单测：结构体行跳过、类型映射、编码探测、名称/地址双索引。
/// </summary>
public class VariableTableTest
{
    /// <summary>与汇川 AutoShop 导出格式一致的样例（含结构体头行与字.位行）。</summary>
    private const string sampleCsv =
        "序号,变量名,数据类型,隐藏初始值,初始值,掉电保持,网络公开,注释,软元件地址,\n" +
        "1,X轴参数,单轴参数,,...,保持,公有,,D1200,\n" +
        ",X轴参数.r_定位速度,REAL,,0.000000,,,,D1200,\n" +
        ",X轴参数.r_加减速度,REAL,,0.000000,,,,D1202,\n" +
        ",X轴命令.b_轴使能,BOOL,,OFF,,,,D1210.0,\n" +
        ",X轴命令.i_手动速度档位,INT,,0,,,,D1212,\n" +
        ",系统状态.b_运行,BOOL,,OFF,,,,M200,\n";

    [Fact]
    public void Parse_SampleCsv_ShouldSkipStructHeadRowAndMapType()
    {
        var result = VariableTableCsvParser.ParseText(sampleCsv);

        // 6 行数据：结构体头行“X轴参数”（类型=单轴参数）被跳过 → 5 条
        Assert.Equal(5, result.Document.EntryCollection.Count);
        Assert.Equal(1, result.SkipRowNumber);

        var positioningSpeed = result.Document.Find("X轴参数.r_定位速度");
        Assert.NotNull(positioningSpeed);
        Assert.Equal("D1200", positioningSpeed.SoftElementAddress);
        Assert.Equal(TagDataType.Float32, positioningSpeed.DataType);

        var gear = result.Document.Find("X轴命令.i_手动速度档位");
        Assert.NotNull(gear);
        Assert.Equal(TagDataType.Int16, gear.DataType);

        var run = result.Document.Find("系统状态.b_运行");
        Assert.NotNull(run);
        Assert.Equal("M200", run.SoftElementAddress);
        Assert.Equal(TagDataType.Boolean, run.DataType);
    }

    [Fact]
    public void Parse_WordBitShapeStyleAddress_ShouldRetainEntry()
    {
        // D1210.0 是 D 字内位：条目保留，且 2026-09-17 起可寻址（按整字读-改-写）
        var result = VariableTableCsvParser.ParseText(sampleCsv);

        var enable = result.Document.Find("X轴命令.b_轴使能");
        Assert.NotNull(enable);
        Assert.Equal("D1210.0", enable.SoftElementAddress);
    }

    [Fact]
    public void Parse_EmptyContent_ShouldThrowDescriptiveException()
    {
        Assert.Throws<InvalidOperationException>(() => VariableTableCsvParser.ParseText("   "));
    }

    [Fact]
    public void Parse_AllRowNotDetect_ShouldThrowDescriptiveException()
    {
        var Content = "序号,变量名,数据类型,隐藏初始值,初始值,掉电保持,网络公开,注释,软元件地址,\n" +
                   ",结构体A,自定义类型,,0,,,,D100,\n";

        Assert.Throws<InvalidOperationException>(() => VariableTableCsvParser.ParseText(Content));
    }

    [Fact]
    public void ReadText_GBKEncodeFile_ShouldBeCorrectDecode()
    {
        var path = Path.Combine(Path.GetTempPath(), $"变量表_{Guid.NewGuid():N}.csv");

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            File.WriteAllBytes(path, Encoding.GetEncoding("GBK").GetBytes(sampleCsv));

            var Text = VariableTableCsvParser.ReadText(path);

            Assert.Contains("X轴参数.r_定位速度", Text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── 变量表服务：名称 / 地址双索引 ──────────────────────────────────

    private static VariableTableService CreateService(VariableTableDocument Document) =>
        new(Document, new PlcHmi.Communication.Inovance.InovanceAddressParser(PlcSeries.SmallPlc),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<VariableTableService>.Instance);

    [Fact]
    public void Service_ByAddressReverseCheck_StartAndCountBothMustPreciseConfirmMatch()
    {
        var Service = CreateService(VariableTableCsvParser.ParseText(sampleCsv).Document);

        // REAL 变量占 2 字：只有 (保持寄存器, 1200, 2) 命中
        Assert.NotNull(Service.ByAddressFind(ModbusArea.HoldingRegister, 1200, 2));
        Assert.Null(Service.ByAddressFind(ModbusArea.HoldingRegister, 1200, 1));
        Assert.Null(Service.ByAddressFind(ModbusArea.HoldingRegister, 1201, 1));   // 高字不是首地址

        // 单字 INT 变量
        Assert.NotNull(Service.ByAddressFind(ModbusArea.HoldingRegister, 1212, 1));

        // M 区 BOOL 变量（线圈区）
        Assert.NotNull(Service.ByAddressFind(ModbusArea.Coil, 200, 1));

        // 字.位变量（D1210.0）：按整字占 1 字寻址，进地址索引
        var bitEntry = Service.ByFirstAddressFind(ModbusArea.HoldingRegister, 1210, out var bitCount);
        Assert.NotNull(bitEntry);
        Assert.Equal("X轴命令.b_轴使能", bitEntry!.VariableName);
        Assert.Equal(1, bitCount);
    }

    [Fact]
    public void Service_SameAddressWholeWordEntryShouldPreferAtWordBitEntry()
    {
        // 同一字上既有整字变量（D1210 REAL）又有位变量（D1210.0 BOOL）时，
        // 反查必须回到整字变量：整字写会覆盖位，定位错对象会写错数据
        var Document = new VariableTableDocument
        {
            EntryCollection =
            [
                new() { VariableName = "位变量", SoftElementAddress = "D1210.0", DataType = TagDataType.Boolean },
                new() { VariableName = "整字变量", SoftElementAddress = "D1210", DataType = TagDataType.Int16 },
            ],
        };

        var Service = CreateService(Document);

        var hit = Service.ByFirstAddressFind(ModbusArea.HoldingRegister, 1210, out var countValue);
        Assert.NotNull(hit);
        Assert.Equal("整字变量", hit!.VariableName);
        Assert.Equal(1, countValue);
    }

    [Fact]
    public void Service_Find_SizeWriteAndEmptyWhiteTolerate()
    {
        var Service = CreateService(VariableTableCsvParser.ParseText(sampleCsv).Document);

        Assert.NotNull(Service.Find(" x轴参数.r_定位速度 "));
        Assert.Null(Service.Find("不存在的变量"));
        Assert.Null(Service.Find(""));
    }

    [Fact]
    public async Task Service_ImportCSV_ShouldReplaceMemoryTableAndPersist()
    {
        var Service = CreateService(new VariableTableDocument());
        var path = Path.Combine(Path.GetTempPath(), $"变量表_{Guid.NewGuid():N}.csv");

        try
        {
            // 导入读取的是 CSV 源文件，落盘目标是 config/variables.json；
            // 测试进程的 config 目录即测试输出目录，允许写入
            await File.WriteAllTextAsync(path, sampleCsv);

            var result = await Service.ImportAsync(path);

            Assert.Equal(5, result.ValidCount);
            Assert.Equal(5, Service.EntryCollection.Count);
            Assert.NotNull(Service.ByAddressFind(ModbusArea.HoldingRegister, 1200, 2));
        }
        finally
        {
            File.Delete(path);
            File.Delete(PlcHmi.Business.Configuration.ConfigPath.VariableTable);
        }
    }
}
