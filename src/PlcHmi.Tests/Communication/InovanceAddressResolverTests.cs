using PlcHmi.Communication.Inovance;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Exceptions;
using Xunit;

namespace PlcHmi.Tests.Communication;

/// <summary>
/// 汇川地址解析器测试。
/// <para>重点验证 X / Y 的<b>八进制</b>换算——这是最容易写错、也最难在现场发现的一处。</para>
/// </summary>
public class InovanceAddressParserTest
{
    private readonly InovanceAddressParser _small = new(PlcSeries.SmallPlc);
    private readonly InovanceAddressParser _medium = new(PlcSeries.MediumPlc);

    // ── 小型 PLC：X 区（八进制）─────────────────────────────────────────

    [Theory]
    [InlineData("X0", 0, 63488)]
    [InlineData("X7", 7, 63495)]
    [InlineData("X10", 8, 63496)]      // 关键：八进制的 10 = 十进制的 8
    [InlineData("X17", 15, 63503)]     // 八进制的 17 = 十进制的 15
    [InlineData("X20", 16, 63504)]
    [InlineData("X1777", 1023, 64511)] // X 区最大值
    public void SmallPLC_XPoint_ShouldByOctalConvert(string inputItem, int expectedIndex, int expectedAddress)
    {
        var addressValue = _small.Parse(inputItem);

        Assert.Equal(SoftElement.X, addressValue.ElementItem);
        Assert.Equal(expectedIndex, addressValue.LogicIndex);
        Assert.Equal(expectedAddress, addressValue.ModbusAddress);

        // X 是物理输入 → 离散输入区（FC02）。2026-10-17 修正：此前是在线圈区，
        // 等于用 FC01 读输入区，现场表现为"Y 一直对、X 一直错"。
        Assert.Equal(ModbusArea.DiscreteInput, addressValue.Area);
    }

    // ── 小型 PLC：Y 区（八进制）─────────────────────────────────────────

    [Theory]
    [InlineData("Y0", 0, 64512)]
    [InlineData("Y7", 7, 64519)]
    [InlineData("Y10", 8, 64520)]      // 关键：八进制
    [InlineData("Y17", 15, 64527)]
    public void SmallPLC_YPoint_ShouldByOctalConvert(string inputItem, int expectedIndex, int expectedAddress)
    {
        var addressValue = _small.Parse(inputItem);

        Assert.Equal(SoftElement.Y, addressValue.ElementItem);
        Assert.Equal(expectedIndex, addressValue.LogicIndex);
        Assert.Equal(expectedAddress, addressValue.ModbusAddress);
        Assert.Equal(ModbusArea.Coil, addressValue.Area);
    }

    // ── 小型 PLC：十进制区 ─────────────────────────────────────────────

    [Theory]
    [InlineData("M0", SoftElement.M, 0, 0, ModbusArea.Coil)]
    [InlineData("M100", SoftElement.M, 100, 100, ModbusArea.Coil)]
    [InlineData("M7999", SoftElement.M, 7999, 7999, ModbusArea.Coil)]
    [InlineData("B100", SoftElement.B, 100, 12388, ModbusArea.Coil)]
    [InlineData("S100", SoftElement.S, 100, 57444, ModbusArea.Coil)]
    [InlineData("SM0", SoftElement.SpecialRegister, 0, 9216, ModbusArea.Coil)]
    [InlineData("T10", SoftElement.T, 10, 61450, ModbusArea.Coil)]
    [InlineData("C10", SoftElement.C, 10, 62474, ModbusArea.Coil)]
    [InlineData("D0", SoftElement.D, 0, 0, ModbusArea.HoldingRegister)]
    [InlineData("D100", SoftElement.D, 100, 100, ModbusArea.HoldingRegister)]
    [InlineData("D7999", SoftElement.D, 7999, 7999, ModbusArea.HoldingRegister)]
    [InlineData("R100", SoftElement.R, 100, 12388, ModbusArea.HoldingRegister)]
    [InlineData("SD0", SoftElement.SystemUnifiedRegister, 0, 9216, ModbusArea.HoldingRegister)]
    public void SmallPLC_DecimalSoftElement_ShouldByAreaStartAddressAddIndex(
        string inputItem, SoftElement expectedElement, int expectedIndex, int expectedAddress, ModbusArea expectedArea)
    {
        var addressValue = _small.Parse(inputItem);

        Assert.Equal(expectedElement, addressValue.ElementItem);
        Assert.Equal(expectedIndex, addressValue.LogicIndex);
        Assert.Equal(expectedAddress, addressValue.ModbusAddress);
        Assert.Equal(expectedArea, addressValue.Area);
    }

    // ── 小型 PLC：SM / SD 优先级（不能被 M / D 抢匹配）──────────────────

    [Fact]
    public void SmallPLC_SMAndSD_ShouldNotBeMAndDWrongMatch()
    {
        // SM 必须匹配为 SoftElement.SM（起始 9216），而不是 S + "M0" 之类
        Assert.Equal(SoftElement.SpecialRegister, _small.Parse("SM1000").ElementItem);
        Assert.Equal(SoftElement.SystemUnifiedRegister, _small.Parse("SD500").ElementItem);

        // 对照：M0 与 D0 各自正常
        Assert.Equal(0, _small.Parse("M0").ModbusAddress);
        Assert.Equal(0, _small.Parse("D0").ModbusAddress);
    }

    // ── 小型 PLC：错误处理 ─────────────────────────────────────────────

    [Theory]
    [InlineData("X8")]      // 8 不是合法的八进制数字
    [InlineData("Y19")]     // 9 不是合法的八进制数字
    [InlineData("X2000")]   // 超出 X 区上限（八进制 2000 = 1024 > 1023）
    [InlineData("M8000")]   // 超出 M 区上限 7999
    [InlineData("Z10")]     // 未知软元件
    [InlineData("X")]       // 缺少序号
    [InlineData("")]        // 空地址
    public void SmallPLC_IllegalAddress_ShouldThrowParseException(string inputItem)
    {
        Assert.Throws<AddressParseException>(() => _small.Parse(inputItem));
    }

    [Fact]
    public void TryResolve_IllegalAddress_ShouldReturnfalseInsteadOfThrowException()
    {
        Assert.False(_small.TryParse("X8", out var addressValue));
        Assert.Null(addressValue);

        Assert.True(_small.TryParse("X10", out var Valid));
        Assert.NotNull(Valid);
    }

    // ── 中型 PLC（AM600）──────────────────────────────────────────────

    [Theory]
    [InlineData("QX0.0", 0, 0)]
    [InlineData("QX0.3", 3, 3)]
    [InlineData("QX0.7", 7, 7)]
    [InlineData("QX1.0", 8, 8)]        // 字节 1 位 0 = 第 9 个点
    [InlineData("QX2.5", 21, 21)]
    public void MediumPLC_QXBitAddressing_ShouldByByteMultiply8AddBitNumber(string inputItem, int expectedIndex, int expectedAddress)
    {
        var addressValue = _medium.Parse(inputItem);

        Assert.Equal(SoftElement.Q, addressValue.ElementItem);
        Assert.Equal(expectedIndex, addressValue.LogicIndex);
        Assert.Equal(expectedAddress, addressValue.ModbusAddress);
        Assert.Equal(ModbusArea.Coil, addressValue.Area);
    }

    [Theory]
    [InlineData("IX0.0", 0)]
    [InlineData("IX1.7", 15)]
    public void MediumPLC_IXBitAddressing_ShouldMapProjectToDiscreteInputArea(string inputItem, int expectedIndex)
    {
        var addressValue = _medium.Parse(inputItem);

        Assert.Equal(SoftElement.Interface, addressValue.ElementItem);
        Assert.Equal(expectedIndex, addressValue.LogicIndex);
        Assert.Equal(ModbusArea.DiscreteInput, addressValue.Area);
    }

    [Fact]
    public void MediumPLC_MWAddressing_ShouldMapProjectToHoldingRegister()
    {
        var addressValue = _medium.Parse("MW100");

        Assert.Equal(SoftElement.MW, addressValue.ElementItem);
        Assert.Equal(100, addressValue.ModbusAddress);
        Assert.Equal(ModbusArea.HoldingRegister, addressValue.Area);
    }

    [Fact]
    public void MediumPLC_SMAndSD_ShouldCarryDedicatedUseFunctionCode()
    {
        var sMAddress = _medium.Parse("SM10");
        Assert.Equal(ModbusArea.Coil, sMAddress.Area);
        Assert.Equal<byte?>(0x31, sMAddress.FunctionCodeOverwrite);   // 汇川专用读功能码

        var sDAddress = _medium.Parse("SD10");
        Assert.Equal(ModbusArea.HoldingRegister, sDAddress.Area);
        Assert.Equal<byte?>(0x33, sDAddress.FunctionCodeOverwrite);
    }

    [Fact]
    public void MediumPLC_UnsupportedSmallPlcSoftElement()
    {
        // D 在 AM600 上不存在（应为 MW）
        Assert.Throws<AddressParseException>(() => _medium.Parse("D100"));
        Assert.Throws<AddressParseException>(() => _medium.Parse("X10"));
    }

    // ── 寄存器数量 ────────────────────────────────────────────────────

    [Theory]
    [InlineData(TagDataType.Boolean, 1)]
    [InlineData(TagDataType.UInt16, 1)]
    [InlineData(TagDataType.Int16, 1)]
    [InlineData(TagDataType.Int32, 2)]
    [InlineData(TagDataType.UInt32, 2)]
    [InlineData(TagDataType.Float32, 2)]
    [InlineData(TagDataType.Float64, 4)]
    public void RegisterCount_ShouldByDataTypeJudge(TagDataType type, int expected)
    {
        Assert.Equal(expected, _small.GetRegisterCount(type));
    }

    // ── 大小写容错 ────────────────────────────────────────────────────

    [Theory]
    [InlineData("x10")]
    [InlineData("X10")]
    [InlineData("m100")]
    [InlineData("d100")]
    public void AddressParse_ShouldIgnoreSizeWrite(string inputItem)
    {
        var addressValue = _small.Parse(inputItem);
        Assert.True(addressValue.ModbusAddress >= 0);
    }

    // ── Explain 诊断文本 ──────────────────────────────────────────────

    [Fact]
    public void Explain_ShouldGenerateCanCheckPairConvertDescription()
    {
        var Text = _small.Remark("X10");

        Assert.Contains("63496", Text);
        Assert.Contains("八进制", Text);
    }
}
