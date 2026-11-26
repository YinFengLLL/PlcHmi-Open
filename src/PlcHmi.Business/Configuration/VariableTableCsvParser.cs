using System.Text;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Configuration;

/// <summary>
/// 汇川变量表 CSV 解析结果。
/// </summary>
/// <param name="Document">解析出的变量表文档。</param>
/// <param name="SkipRowNumber">被跳过的行数（结构体头行 / 不支持的类型 / 缺地址）。</param>
public sealed record VariableTableParseResult(VariableTableDocument Document, int SkipRowNumber);

/// <summary>
/// 汇川变量表 CSV 解析器。
///
/// <para><b>支持的格式</b>（汇川 AutoShop 导出，9 列 + 尾逗号）：</para>
/// <code>
/// 序号,变量名,数据类型,隐藏初始值,初始值,掉电保持,网络公开,注释,软元件地址,
/// 1,X轴参数,单轴参数,,...,保持,公有,,D1200,        ← 结构体头行：类型非基础类型，跳过
/// ,X轴参数.r_定位速度,REAL,,0.000000,,,,D1202,     ← 成员行：完整路径变量名
/// ,X轴命令.b_轴使能,BOOL,,OFF,,,,D1210.0,           ← 字.位形式：可寻址（读写按整字读-改-写）
/// </code>
///
/// <para><b>编码</b>：先按 UTF-8 严格解码，失败（汇川部分版本导出 GBK）回退 GBK。</para>
/// </summary>
public static class VariableTableCsvParser
{
    /// <summary>CSV 数据类型 → 上位机标签数据类型的映射表。</summary>
    private static readonly Dictionary<string, TagDataType> typeMapTable = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BOOL"] = TagDataType.Boolean,
        ["SINT"] = TagDataType.Int16,
        ["INT"] = TagDataType.Int16,
        ["USINT"] = TagDataType.UInt16,
        ["UINT"] = TagDataType.UInt16,
        ["BYTE"] = TagDataType.UInt16,
        ["WORD"] = TagDataType.UInt16,
        ["DINT"] = TagDataType.Int32,
        ["UDINT"] = TagDataType.UInt32,
        ["DWORD"] = TagDataType.UInt32,
        ["TIME"] = TagDataType.UInt32,
        ["REAL"] = TagDataType.Float32,
        ["LREAL"] = TagDataType.Float64,
    };

    static VariableTableCsvParser()
    {
        // GBK 编码在 .NET Core 后需要手动注册 CodePages 提供程序
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>把 CSV 文本解析为变量表文档。文件级失败（空内容 / 全部行无效）抛 <see cref="InvalidOperationException"/>。</summary>
    public static VariableTableParseResult ParseText(string csvText)
    {
        if (string.IsNullOrWhiteSpace(csvText))
        {
            throw new InvalidOperationException("变量表 CSV 内容为空。");
        }

        var entryList = new List<VariableTableEntry>();
        var SkipRowNumber = 0;
        var rowList = csvText.ReplaceLineEndings("\n").Split('\n');

        foreach (var SourceRow in rowList.Skip(1))    // 首行为表头
        {
            var rowText = SourceRow.TrimEnd('\r').Trim();

            if (rowText.Length == 0)
            {
                continue;
            }

            var Column = rowText.Split(',');
            if (Column.Length < 9)
            {
                SkipRowNumber++;
                continue;
            }

            var VariableName = Column[1].Trim();
            var TypeText = Column[2].Trim();
            var AddressText = Column[8].Trim();

            // 变量名为空（纯结构体头行变体）或缺地址：跳过
            if (VariableName.Length == 0 || AddressText.Length == 0)
            {
                SkipRowNumber++;
                continue;
            }

            // 非基础类型（结构体头行如 "单轴参数"、STRING 等）：跳过并计数
            if (!typeMapTable.TryGetValue(TypeText, out var DataType))
            {
                SkipRowNumber++;
                continue;
            }

            entryList.Add(new VariableTableEntry
            {
                VariableName = VariableName,
                SoftElementAddress = AddressText,
                DataType = DataType,
                Comment = Column[7].Trim().Length > 0 ? Column[7].Trim() : null,
            });
        }

        if (entryList.Count == 0)
        {
            throw new InvalidOperationException(
                $"变量表 CSV 中没有可识别的变量行（跳过 {SkipRowNumber} 行）。请确认文件为汇川变量表导出格式。");
        }

        return new VariableTableParseResult(new VariableTableDocument { EntryCollection = entryList }, SkipRowNumber);
    }

    /// <summary>读文件并自动识别编码（UTF-8 优先，失败回退 GBK）。</summary>
    public static string ReadText(string filePath)
    {
        var byteValue = File.ReadAllBytes(filePath);

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(byteValue);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding("GBK").GetString(byteValue);
        }
    }
}
