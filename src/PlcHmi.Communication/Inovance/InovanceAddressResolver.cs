using System.Globalization;
using System.Text.RegularExpressions;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Exceptions;
using PlcHmi.Core.Models;

namespace PlcHmi.Communication.Inovance;

/// <summary>
/// 汇川软元件地址解析器。
///
/// <para><b>为什么需要它：</b>汇川 PLC 的 Modbus 地址不是标准 4xxxx 顺序编号，
/// 而是“功能区起始地址 + 软元件序号”。例如 X0 对应 63488 而不是 0。
/// 搞错这一层，所有数据都会错位。</para>
///
/// <para><b>最易踩的坑：</b>X / Y 的序号是<b>八进制</b>——<c>X10</c> 表示第 8 个点，
/// 换算后 Modbus 地址是 63496，不是 63498。</para>
///
/// <para><b>机型差异：</b>小型 PLC（EASY320 / EASY52X / H5U / H3U）使用 M/B/S/X/Y/D/R 体系；
/// 中型 PLC（AM600 / AC800）使用 QX/IX/MW 体系，且 SM/SD 需专用功能码。</para>
/// </summary>
public sealed class InovanceAddressParser : IAddressParser
{
    // 基址表与地址前缀对照表已抽到 汇川软元件规格表（数据与算法分离）。
    // 注意：SM 必须排在 M 之前，SD 必须排在 D 之前，否则会被短前缀抢先匹配
    private static readonly Regex smallPlcMode =
        new(@"^(?<elem>SM|SD|X|Y|M|B|S|D|R|T|C)(?<index>\d+)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 小型 PLC 的 D/R 区「字内位」：D1210.2 = D 字第 2 位（0–15）。
    // 汇川位打包约定（PLC 地址分配表）：BOOL 占 D 的一位，可连续；
    // Modbus 无「字内位」功能码，读写须按整字处理（见 TagWriter 的读-改-写）。
    private static readonly Regex smallWordBitMode =
        new(@"^(?<elem>D|R)(?<word>\d+)\.(?<bit>1[0-5]|[0-9])$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex mediumBitMode =
        new(@"^(?<elem>QX|IX)(?<byte>\d+)\.(?<bit>[0-7])$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex mediumWordMode =
        new(@"^(?<elem>MW|SM|SD)(?<index>\d+)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Dictionary<SoftElement, SoftElementSpec> _spec;

    public InovanceAddressParser(PlcSeries seriesValue)
    {
        Series = seriesValue;
        _spec = InovanceSoftElementSpecTable.ByMachineModel(seriesValue);
    }

    /// <inheritdoc />
    public PlcSeries Series { get; }

    /// <inheritdoc />
    public PlcAddress Parse(string SoftElement)
    {
        if (string.IsNullOrWhiteSpace(SoftElement))
        {
            throw AddressParseException.Input(SoftElement ?? "(null)", "地址为空");
        }

        var inputItem = SoftElement.Trim();

        return Series == PlcSeries.SmallPlc
            ? ParseSmallPlc(inputItem)
            : ParseMediumPlc(inputItem);
    }

    /// <inheritdoc />
    public bool TryParse(string SoftElement, out PlcAddress? addressValue)
    {
        try
        {
            addressValue = Parse(SoftElement);
            return true;
        }
        catch (AddressParseException)
        {
            addressValue = null;
            return false;
        }
    }

    /// <inheritdoc />
    public int GetRegisterCount(TagDataType DataType) => DataType switch
    {
        TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float32 => 2,
        TagDataType.Float64 => 4,
        _ => 1,
    };

    /// <summary>
    /// 把 Modbus 地址<b>反解</b>回软元件符号。OPC UA 模式专用：
    /// 客户端从业务层只拿到“区域 + Modbus 地址”，必须先还原软元件符号才能推导 NodeId。
    ///
    /// <para>反解规则与 <see cref="Parse"/> 的基址表严格互逆。同一区域内区间重叠时
    /// （中型 PLC 的 Q 覆盖 SM / SD 覆盖 R），取序号上限更小、更具体的那一个。</para>
    /// </summary>
    /// <param name="areaValue">Modbus 数据区（由调用方接口方法隐含给出）。</param>
    /// <param name="modbusAddressValue">换算后的 Modbus 地址。</param>
    /// <exception cref="AddressParseException">地址不属于当前机型任何软元件区间时抛出。</exception>
    public PlcAddress Reverse(ModbusArea areaValue, int modbusAddressValue)
    {
        if (modbusAddressValue is < 0 or > ushort.MaxValue)
        {
            throw AddressParseException.Input(modbusAddressValue.ToString(CultureInfo.InvariantCulture),
                "地址超出 Modbus 地址空间范围 0-65535");
        }

        var candidateList = _spec.Values
            .Where(specItem => specItem.Area == areaValue)
            .Where(specItem => modbusAddressValue >= specItem.baseAddressValue &&
                             modbusAddressValue - specItem.baseAddressValue <= specItem.maxIndex)
            .OrderBy(specItem => specItem.maxIndex)          // 范围小者优先——更具体的软元件
            .ThenBy(specItem => specItem.baseAddressValue)
            .ToList();

        if (candidateList.Count == 0)
        {
            throw AddressParseException.Input(
                $"{areaValue}@{modbusAddressValue}",
                $"该 Modbus 地址不属于当前机型的任何软元件区间，无法推导 OPC UA NodeId");
        }

        var specItem = candidateList[0];
        var Sequence = modbusAddressValue - specItem.baseAddressValue;
        var symbolNumberText = GenerateSoftElementText(specItem.ElementItem, Sequence);

        return new PlcAddress
        {
            ElementItem = specItem.ElementItem,
            LogicIndex = Sequence,
            ModbusAddress = modbusAddressValue,
            Area = areaValue,
            RegisterCount = 1,
            Source = symbolNumberText,
        };
    }

    /// <summary>
    /// 软元件 + 序号 → 书写形式文本。X / Y 的序号是八进制语义，反解时必须按八进制还原
    /// （序号 8 还原为 <c>X10</c> 而不是 <c>X8</c>），否则与 PLC 服务端节点名对不上。
    /// </summary>
    public static string GenerateSoftElementText(SoftElement ElementItem, int Sequence)
    {
        // 前缀是现场书写约定，与枚举成员名解耦（理由同 前缀对照表）
        var prefix = ElementItem switch
        {
            SoftElement.SpecialRegister => "SM",
            SoftElement.SystemUnifiedRegister => "SD",
            SoftElement.Q => "QX",
            SoftElement.Interface => "IX",
            _ => ElementItem.ToString(),
        };

        return ElementItem is SoftElement.X or SoftElement.Y
            ? prefix + Convert.ToString(Sequence, 8)
            : prefix + Sequence.ToString(CultureInfo.InvariantCulture);
    }

    // ── 小型 PLC 解析 ───────────────────────────────────────────────────

    private PlcAddress ParseSmallPlc(string inputItem)
    {
        // D1210.2 / R50.15 —— D/R 字内位（汇川位打包：BOOL 占 D 的一位，0–15）
        var bitMatch = smallWordBitMode.Match(inputItem);
        if (bitMatch.Success)
        {
            var bitElement = ParseElement(bitMatch.Groups["elem"].Value, inputItem);
            var bitSpec = GetSpec(bitElement, inputItem);
            var wordOrderNumber = ParseIndex(bitMatch.Groups["word"].Value, 10, inputItem, bitElement);
            var bitNumber = int.Parse(bitMatch.Groups["bit"].Value, CultureInfo.InvariantCulture);

            ValidateRange(wordOrderNumber, bitSpec, inputItem);

            return new PlcAddress
            {
                ElementItem = bitElement,
                LogicIndex = wordOrderNumber,
                ModbusAddress = bitSpec.baseAddressValue + wordOrderNumber,
                Area = bitSpec.Area,          // 保持寄存器——读写走整字，再按位取/改
                RegisterCount = 1,
                BitIndex = bitNumber,
                Source = inputItem,
            };
        }

        var Match = smallPlcMode.Match(inputItem);
        if (!Match.Success)
        {
            throw AddressParseException.Input(inputItem,
                "格式不正确。小型 PLC 应形如 X10、Y0、M100、D100、R200、D1210.2（软元件前缀 + 数字，D/R 支持字内位 .0–.15）");
        }

        var ElementItem = ParseElement(Match.Groups["elem"].Value, inputItem);
        var specItem = GetSpec(ElementItem, inputItem);
        var Sequence = ParseIndex(Match.Groups["index"].Value, specItem.numberRadix, inputItem, ElementItem);
        ValidateRange(Sequence, specItem, inputItem);

        return new PlcAddress
        {
            ElementItem = ElementItem,
            LogicIndex = Sequence,
            ModbusAddress = specItem.baseAddressValue + Sequence,
            Area = specItem.Area,
            RegisterCount = 1,
            Source = inputItem,
        };
    }

    // ── 中型 PLC 解析 ───────────────────────────────────────────────────

    private PlcAddress ParseMediumPlc(string inputItem)
    {
        // QX0.3 / IX1.7 —— 按位寻址
        var bitMatch = mediumBitMode.Match(inputItem);
        if (bitMatch.Success)
        {
            var ElementItem = ParseElement(bitMatch.Groups["elem"].Value, inputItem);
            var specItem = GetSpec(ElementItem, inputItem);
            var byteIndex = ParseIndex(bitMatch.Groups["byte"].Value, 10, inputItem, ElementItem);
            var BitIndex = bitMatch.Groups["bit"].Value[0] - '0';
            var Sequence = byteIndex * 8 + BitIndex;

            ValidateRange(Sequence, specItem, inputItem);

            return new PlcAddress
            {
                ElementItem = ElementItem,
                LogicIndex = Sequence,
                ModbusAddress = specItem.baseAddressValue + Sequence,
                Area = specItem.Area,
                RegisterCount = 1,
                BitIndex = BitIndex,
                Source = inputItem,
            };
        }

        // MW100 / SM10 / SD10 —— 按字寻址
        var wordMatch = mediumWordMode.Match(inputItem);
        if (wordMatch.Success)
        {
            var ElementItem = ParseElement(wordMatch.Groups["elem"].Value, inputItem);
            var specItem = GetSpec(ElementItem, inputItem);
            var Sequence = ParseIndex(wordMatch.Groups["index"].Value, 10, inputItem, ElementItem);

            ValidateRange(Sequence, specItem, inputItem);

            return new PlcAddress
            {
                ElementItem = ElementItem,
                LogicIndex = Sequence,
                ModbusAddress = specItem.baseAddressValue + Sequence,
                Area = specItem.Area,
                RegisterCount = 1,
                FunctionCodeOverwrite = specItem.readFunctionCanCode,
                Source = inputItem,
            };
        }

        throw AddressParseException.Input(inputItem,
            "格式不正确。中型 PLC(AM600) 应形如 QX0.3、IX1.0、MW100、SM10、SD10");
    }

    // ── 辅助方法 ────────────────────────────────────────────────────────

    private SoftElementSpec GetSpec(SoftElement ElementItem, string inputItem)
    {
        if (_spec.TryGetValue(ElementItem, out var specItem))
        {
            return specItem;
        }

        var support = string.Join(" / ", _spec.Keys.Select(Key => Key.ToString()));
        var seriesName = Series == PlcSeries.SmallPlc ? "小型 PLC(EASY/H5U/H3U)" : "中型 PLC(AM600/AC800)";
        throw AddressParseException.Input(inputItem,
            $"{seriesName} 不支持软元件 {ElementItem}。当前机型支持的软元件：{support}");
    }

    /// <summary>前缀对照表见 <see cref="InovanceSoftElementSpecTable.prefixPairPer"/>（数据与算法分离）。</summary>
    private static SoftElement ParseElement(string token, string completeWholeInput)
    {
        return InovanceSoftElementSpecTable.prefixPairPer.TryGetValue(token, out var ElementItem)
            ? ElementItem
            : throw AddressParseException.Input(completeWholeInput, $"未知的软元件前缀 \"{token}\"");
    }

    /// <summary>
    /// 按指定进制解析序号。<b>这里是八进制坑的所在</b>：
    /// X / Y 的 "10" 必须解析为 8，而不是 10。
    /// </summary>
    private static int ParseIndex(string numberList, int radix, string inputItem, SoftElement ElementItem)
    {
        long Value = 0;
        foreach (var channel in numberList)
        {
            var numberItem = channel - '0';
            if (numberItem < 0 || numberItem >= radix)
            {
                var radixName = radix == 8 ? "八" : "十";
                throw AddressParseException.Input(inputItem,
                    $"序号中的字符 '{channel}' 不是合法的{radixName}进制数字，" +
                    $"{(radix == 8 ? "八进制只允许 0-7" : "十进制只允许 0-9")}");
            }

            Value = Value * radix + numberItem;

            if (Value > int.MaxValue)
            {
                throw AddressParseException.Input(inputItem, "序号过大，超出整数范围");
            }
        }

        return (int)Value;
    }

    private static void ValidateRange(int Sequence, SoftElementSpec specItem, string inputItem)
    {
        if (Sequence < 0 || Sequence > specItem.maxIndex)
        {
            throw AddressParseException.OutOfRange(inputItem, Sequence, specItem.maxIndex);
        }
    }

    /// <summary>
    /// 生成便于人工核对地址换算的说明文本，用于诊断界面。
    /// </summary>
    public string Remark(string SoftElement)
    {
        var addressValue = Parse(SoftElement);
        var specItem = _spec[addressValue.ElementItem];
        var radixRemark = specItem.numberRadix == 8 ? "（八进制解析）" : string.Empty;

        return string.Create(CultureInfo.InvariantCulture,
            $"{SoftElement} → {specItem.ElementItem} 序号 {addressValue.LogicIndex}{radixRemark} " +
            $"= {specItem.baseAddressValue} + {addressValue.LogicIndex} = {addressValue.ModbusAddress} " +
            $"[{addressValue.Area}]");
    }
}
