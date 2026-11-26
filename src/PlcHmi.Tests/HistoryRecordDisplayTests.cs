using Microsoft.Extensions.Logging.Abstractions;
using PlcHmi.Business.Configuration;
using PlcHmi.Business.Services;
using PlcHmi.Communication.Inovance;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;
using PlcHmi.Tests.Fake;
namespace PlcHmi.Tests;

/// <summary>
/// 历史记录的<b>显示增强</b>回归：DINT 按 ASCII 显示、列颜色（红 / 黄 / 绿，可选）。
///
/// <para>与 <see cref="HistoryRecordTest"/>（数值缩放 / CSV / 校验）分工：那是"采到什么"，
/// 这里是"采到了怎么显示"。从后者拆出——它已顶到项目规模门禁。</para>
///
/// <para>两条服务层校验用例要读 <c>config/history-record.json</c>，与其它动配置文件的用例
/// 同属 <c>config-files</c> 集合，禁用并行。</para>
/// </summary>
[Collection("config-files")]
public class HistoryRecordShowTest
{
    /// <summary>
    /// 按字序把 4 个字符打包成 32 位值（与实现的拆解互为逆运算）。
    /// <para><b>字内低字节在前</b>（2026-10-17 现场确认）：首字符落在第一个字的低字节——
    /// 现场读到的字是 <c>19279</c>（0x4B4F）时应显示 <c>OK</c>，即 'O'(0x4F) 在低字节、
    /// 'K'(0x4B) 在高字节。两个字之间的先后才由字序决定。</para>
    /// </summary>
    private static int PackCharacter(char item, char item2, char item3, char D, WordOrder wordOrder)
    {
        var firstWord = item | (item2 << 8);
        var secondWord = item3 | (D << 8);

        return wordOrder is WordOrder.BigEndian
            ? (firstWord << 16) | secondWord   // 高字在前
            : firstWord | (secondWord << 16);  // 低字在前（CDAB，汇川默认）
    }

    // ── ASCII 显示（2026-10-17 现场要求：DINT 可选按字符显示）──────────

    /// <summary>
    /// 现场实例回归：读到的字是 <c>19279</c>（0x4B4F）时必须显示 <c>OK</c>。
    /// <para>这条用例钉死"<b>字内低字节在前</b>"的约定——曾经按"寄存器大端"实现，
    /// 同样的字会显示成 <c>KO</c>，现场直接报障。</para>
    /// </summary>
    [Fact]
    public void AsciiShow_19279ShouldShowAsOK()
    {
        var Item = new HistoryDataItem
        {
            Name = "结果文本", Address = "D2130", DataType = TagDataType.Int32, ShowAsAscii = true,
        };

        // 高 16 位为 0 → 后两个字符是不可打印字节，占位为点
        Assert.Equal("OK..", Item.ShowText(19279, WordOrder.LittleEndian));
    }

    /// <summary>
    /// DINT 按 ASCII 显示：4 个字符打包进一个 32 位寄存器。
    /// <para><b>两个字的先后必须按配置的字序</b>：CDAB 与大字节序下"哪两个字谁在前"相反，
    /// 写死一种顺序会让另一种显示成乱序（红线 R1：字序必须可配）。</para>
    /// </summary>
    [Fact]
    public void DintByAsciiShow_ShouldByConfigWordOrderSplitByte()
    {
        var Item = new HistoryDataItem
        {
            Name = "批次号", Address = "D2130", DataType = TagDataType.Int32, ShowAsAscii = true,
        };

        Assert.Equal("AB12", Item.ShowText(PackCharacter('A', 'B', '1', '2', WordOrder.LittleEndian), WordOrder.LittleEndian));
        Assert.Equal("AB12", Item.ShowText(PackCharacter('A', 'B', '1', '2', WordOrder.BigEndian), WordOrder.BigEndian));

        // 用错字序会乱序——这条断言本身就是"字序必须可配"的理由
        Assert.Equal("12AB", Item.ShowText(PackCharacter('A', 'B', '1', '2', WordOrder.LittleEndian), WordOrder.BigEndian));

        // 不传字序时按汇川默认（小字节序），老调用点行为不变
        Assert.Equal("AB12", Item.ShowText(PackCharacter('A', 'B', '1', '2', WordOrder.LittleEndian)));
    }

    /// <summary>不可打印字节用 <c>.</c> 占位：控制字符画进表格会把行高撑坏。</summary>
    [Fact]
    public void DintByAsciiShow_NotHitPrintByteShouldOccupyBitAsPoint()
    {
        var Item = new HistoryDataItem
        {
            Name = "产品码", Address = "D2132", DataType = TagDataType.Int32, ShowAsAscii = true,
        };

        Assert.Equal("....", Item.ShowText(
            PackCharacter('\0', '\x1F', '\x7F', '\x80', WordOrder.LittleEndian), WordOrder.LittleEndian));

        Assert.Equal("K7.-", Item.ShowText(
            PackCharacter('K', '7', '\0', '-', WordOrder.LittleEndian), WordOrder.LittleEndian));
    }

    /// <summary>ASCII 显示优先于小数位（字符没有小数点），且只对 DINT 生效。</summary>
    [Fact]
    public void AsciiShowShouldPreferAtDecimalBitAndOnlyDintGenerateEffect()
    {
        var dintItem = new HistoryDataItem
        {
            Name = "批次号", Address = "D2130", DataType = TagDataType.Int32,
            DecimalBit = 2, ShowAsAscii = true,
        };

        Assert.Equal("AB12", dintItem.ShowText(PackCharacter('A', 'B', '1', '2', WordOrder.LittleEndian)));

        // INT 只有 2 字节，凑不出 4 个字符：即便配置里勾了也按数值显示（界面已禁用勾选）
        var intItem = new HistoryDataItem
        {
            Name = "工位号", Address = "D2100", DataType = TagDataType.Int16,
            DecimalBit = 0, ShowAsAscii = true,
        };

        Assert.Equal("12", intItem.ShowText(12));
        Assert.False(intItem.SettableAscii);
    }

    /// <summary>
    /// 2026-10-24 现场要求：新支持 REAL（32 位浮点）。
    /// <para>REAL 的原始值<b>已经是工程量</b>，小数位只决定显示保留几位、不做 ÷10^位的缩放——
    /// 照整数那样除会把 12.35 显示成 0.12，属于"能存但显示错"，比报错难查得多。</para>
    /// </summary>
    [Fact]
    public void RealShouldByEngineeringValueShowAndDecimalBitOnlyControlRetainBitCount()
    {
        var twoBit = new HistoryDataItem
        {
            Name = "胶量", Address = "D1420", DataType = TagDataType.Float32, DecimalBit = 2, Unit = "mg",
        };

        Assert.Equal("12.35", twoBit.ShowText(12.3456));
        Assert.Equal("REAL", twoBit.TypeText);
        Assert.False(twoBit.SettableAscii);   // 浮点的 4 个字节拆成字符全是乱码

        var zeroBit = twoBit.Clone();
        zeroBit.DecimalBit = 0;
        Assert.Equal("12", zeroBit.ShowText(12.3456));   // 四舍五入到整数

        // CSV 导出走同一套显示规则
        var Config = new HistoryRecordConfig
        {
            Title = "浮点记录", TriggerAddress = "M8", DataItemCollection = [twoBit],
        };

        var Text = HistoryRecordCsvCodec.Encode(
            Config,
            [new HistoryRecordEntry { Timestamp = new DateTime(2026, 10, 24, 9, 0, 0), NumericValueCollection = [12.3456] }]);

        Assert.Contains("12.35", Text, StringComparison.Ordinal);
    }

    /// <summary>CSV 导出与界面显示一致：ASCII 列导出的是字符，不是打包后的整数。</summary>
    [Fact]
    public void CsvExportAsciiColumnShouldWriteOutCharacterAndNonInteger()
    {
        var Config = new HistoryRecordConfig
        {
            Title = "锁螺丝记录",
            TriggerAddress = "M700",
            DataItemCollection =
            [
                new() { Name = "批次号", Address = "D2130", DataType = TagDataType.Int32, ShowAsAscii = true },
                new() { Name = "实际扭力", Address = "D2109", DataType = TagDataType.Int32, DecimalBit = 2 },
            ],
        };

        var Entry = new HistoryRecordEntry
        {
            Timestamp = new DateTime(2026, 10, 17, 8, 30, 0),
            NumericValueCollection = [PackCharacter('A', 'B', '1', '2', WordOrder.LittleEndian), 600],
        };

        var Text = HistoryRecordCsvCodec.Encode(Config, [Entry]);

        Assert.Contains("AB12", Text, StringComparison.Ordinal);
        Assert.Contains("6.00", Text, StringComparison.Ordinal);
    }

    // ── 列颜色（红 / 黄 / 绿是可选项，不选则默认色）────────────────────

    /// <summary>
    /// 颜色是<b>可选项</b>：不选（默认）时数据列用正文色、表头用次要色——
    /// 与加这个功能之前的观感完全一致，老配置升级后零变化。
    /// </summary>
    [Fact]
    public void ColumnColor_NotSelectWhenShouldBackAtDefaultColor()
    {
        var Item = new HistoryDataItem { Name = "结果", Address = "D2121" };

        Assert.Equal(HistoryColumnColor.None, Item.Color);           // 默认即"不选"
        Assert.Equal("TextPrimaryBrush", Item.ColorKey);
        Assert.Equal("TextSecondaryBrush", Item.HeaderColorKey);
    }

    /// <summary>选了红 / 黄 / 绿时，数据列与表头用同一个语义色。</summary>
    [Theory]
    [InlineData(HistoryColumnColor.Red, "DangerBrush")]
    [InlineData(HistoryColumnColor.Yellow, "WarningBrush")]
    [InlineData(HistoryColumnColor.Green, "SuccessBrush")]
    public void ColumnColor_ThreeKindOptionalColorShouldMapProjectToThemeLanguageMeaningColor(HistoryColumnColor Color, string expectedKey)
    {
        var Item = new HistoryDataItem { Name = "结果", Address = "D2121", Color = Color };

        Assert.Equal(expectedKey, Item.ColorKey);
        Assert.Equal(expectedKey, Item.HeaderColorKey);
    }

    /// <summary>克隆必须带上新增字段：编辑对话框取消时不丢，保存时也不会静默还原。</summary>
    [Fact]
    public void CloneShouldRetainAsciiAndColorSet()
    {
        var Item = new HistoryDataItem
        {
            Name = "批次号", Address = "D2130", DataType = TagDataType.Int32,
            ShowAsAscii = true, Color = HistoryColumnColor.Yellow,
        };

        var copy = Item.Clone();

        Assert.True(copy.ShowAsAscii);
        Assert.Equal(HistoryColumnColor.Yellow, copy.Color);
    }

    // ── 服务层校验：ASCII 只支持 DINT（界面禁用之外的第二道边界）──────

    /// <summary>
    /// ASCII 显示只支持 DINT：INT 只有 2 字节，勾了也显示不对。
    /// <para>界面已禁用非 DINT 的勾选，但配置还有"手改 JSON"这个入口——
    /// 规则必须两边一致，否则现场手改的文件会安静地显示错值（R5 同源规则）。</para>
    /// </summary>
    [Fact]
    public async Task IntItemCheckedAsciiShouldBeRejected()
    {
        await using var Client = new FakeModbusClient();
        var serviceInstance = NewService(Client);

        var Config = new HistoryRecordConfig
        {
            Title = "非法配置",
            TriggerAddress = "M700",
            DataItemCollection =
            [
                new() { Name = "工位号", Address = "D2100", DataType = TagDataType.Int16, ShowAsAscii = true },
            ],
        };

        var validateItem = serviceInstance.Validate(Config);

        Assert.False(validateItem.IsValid);
        Assert.Contains("ASCII 显示只支持 DINT", validateItem.Summary);
    }

    /// <summary>DINT 勾 ASCII、INT 选颜色都是合法组合，不应被误伤。</summary>
    [Fact]
    public async Task DintItemCheckedAsciiAndSetColorShouldPassValidate()
    {
        await using var Client = new FakeModbusClient();
        var serviceInstance = NewService(Client);

        var Config = new HistoryRecordConfig
        {
            Title = "合法配置",
            TriggerAddress = "M700",
            DataItemCollection =
            [
                new() { Name = "批次号", Address = "D2130", DataType = TagDataType.Int32, ShowAsAscii = true },
                new() { Name = "结果", Address = "D2132", DataType = TagDataType.Int16, Color = HistoryColumnColor.Red },
            ],
        };

        var validateItem = serviceInstance.Validate(Config);

        Assert.True(validateItem.IsValid, validateItem.Summary);
    }

    /// <summary>构造被测服务（替身客户端 + 放行权限的守卫 + 内存存储）。</summary>
    private static HistoryRecordService NewService(FakeModbusClient Client) => new(
        new InovanceAddressParser(PlcSeries.SmallPlc),
        Client,
        new AllowAllPermissionGuard(),
        new HistoryRecordStore(NullLogger<HistoryRecordStore>.Instance),
        NullLogger<HistoryRecordService>.Instance);
}
