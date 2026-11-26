using System.Globalization;
using System.Text;

namespace 上位机.核心.模型;

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
public static class 历史记录CSV编解码器
{
    /// <summary>表头第一列：记录时刻。</summary>
    public const string 时间列名 = "时间";

    /// <summary>把某一天的历史记录编码成 CSV 文本。</summary>
    /// <param name="配置项">数据体配置（决定列顺序、列名、单位与小数位）。</param>
    /// <param name="条目列表">记录列表（按时间升序）。</param>
    public static string 编码(历史记录配置 配置项, IReadOnlyList<历史记录条目> 条目列表)
    {
        ArgumentNullException.ThrowIfNull(配置项);
        ArgumentNullException.ThrowIfNull(条目列表);

        var 文本构建器 = new StringBuilder();

        var 表头 = new List<string> { 时间列名 };

        foreach (var 数据项 in 配置项.数据项集合)
        {
            表头.Add(表头文本(数据项));
        }

        文本构建器.AppendLine(string.Join(',', 表头.Select(退出)));

        foreach (var 条目 in 条目列表)
        {
            var 行 = new List<string> { 条目.时刻.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) };

            for (var 序号 = 0; 序号 < 配置项.数据项集合.Count; 序号++)
            {
                // 记录数少于当前配置（改过列数、或解码失败）时留空，而不是错位补 0
                行.Add(序号 < 条目.数值集合.Count
                    ? 配置项.数据项集合[序号].显示文本(条目.数值集合[序号], 配置项.字顺序)
                    : string.Empty);
            }

            文本构建器.AppendLine(string.Join(',', 行.Select(退出)));
        }

        return 文本构建器.ToString();
    }

    /// <summary>表头文本：列名 + 单位（无单位时只写列名）。</summary>
    public static string 表头文本(历史数据项 数据项)
    {
        ArgumentNullException.ThrowIfNull(数据项);

        var 名称值 = string.IsNullOrWhiteSpace(数据项.名称) ? 数据项.地址 : 数据项.名称;

        return string.IsNullOrWhiteSpace(数据项.单元) ? 名称值 : $"{名称值}({数据项.单元})";
    }

    /// <summary>按 CSV 规则转义字段。</summary>
    private static string 退出(string? 值)
    {
        if (string.IsNullOrEmpty(值))
        {
            return string.Empty;
        }

        return 值.Contains(',') || 值.Contains('"') || 值.Contains('\n')
            ? $"\"{值.Replace("\"", "\"\"")}\""
            : 值;
    }
}
