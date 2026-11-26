using System.Globalization;
using System.Text;

namespace PlcHmi.Core.Models;

/// <summary>
/// 历史记录的 CSV 编解码。
///
/// <para><b>为什么导出用"显示值"而不是原始值：</b>导出是给人看的（交班记录、工艺追溯），
/// 与表格里看到的必须一致——600 在界面显示 6.00，CSV 里也应写着 6.00。
/// 原始值仍完整体现在 <c>data/</c> 下的 JSON 里，需要二次分析时取那份。</para>
///
/// <para>分隔符为逗号，字段含逗号 / 引号 / 换行时按 RFC4180 加引号并双写引号；
/// 写出带 UTF-8 BOM（由调用方负责），Excel 双击中文不乱码。</para>
/// </summary>
public static class HistoryRecordCsvCodec
{
    /// <summary>表头第一列：记录时刻。</summary>
    public const string timeColumnName = "时间";

    /// <summary>把某一天的历史记录编码成 CSV 文本。</summary>
    /// <param name="configItem">数据体配置（决定列顺序、列名、单位与小数位）。</param>
    /// <param name="entryList">记录列表（按时间升序）。</param>
    public static string Encode(HistoryRecordConfig configItem, IReadOnlyList<HistoryRecordEntry> entryList)
    {
        ArgumentNullException.ThrowIfNull(configItem);
        ArgumentNullException.ThrowIfNull(entryList);

        var textBuilder = new StringBuilder();

        var header = new List<string> { timeColumnName };

        foreach (var dataItem in configItem.DataItemCollection)
        {
            header.Add(HeaderText(dataItem));
        }

        textBuilder.AppendLine(string.Join(',', header.Select(Exit)));

        foreach (var Entry in entryList)
        {
            var Row = new List<string> { Entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) };

            for (var Sequence = 0; Sequence < configItem.DataItemCollection.Count; Sequence++)
            {
                // 记录数少于当前配置（改过列数、或解码失败）时留空，而不是错位补 0
                Row.Add(Sequence < Entry.NumericValueCollection.Count
                    ? configItem.DataItemCollection[Sequence].ShowText(Entry.NumericValueCollection[Sequence], configItem.WordOrder)
                    : string.Empty);
            }

            textBuilder.AppendLine(string.Join(',', Row.Select(Exit)));
        }

        return textBuilder.ToString();
    }

    /// <summary>表头文本：列名 + 单位（无单位时只写列名）。</summary>
    public static string HeaderText(HistoryDataItem dataItem)
    {
        ArgumentNullException.ThrowIfNull(dataItem);

        var nameValue = string.IsNullOrWhiteSpace(dataItem.Name) ? dataItem.Address : dataItem.Name;

        return string.IsNullOrWhiteSpace(dataItem.Unit) ? nameValue : $"{nameValue}({dataItem.Unit})";
    }

    /// <summary>按 CSV 规则转义字段。</summary>
    private static string Exit(string? Value)
    {
        if (string.IsNullOrEmpty(Value))
        {
            return string.Empty;
        }

        return Value.Contains(',') || Value.Contains('"') || Value.Contains('\n')
            ? $"\"{Value.Replace("\"", "\"\"")}\""
            : Value;
    }
}
