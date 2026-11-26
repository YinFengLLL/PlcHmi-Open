using System.Globalization;
using System.Text;

namespace 上位机.核心.模型;

/// <summary>CSV 导入结果。</summary>
public sealed class CSV导入结果
{
    /// <summary>是否成功解析出可用的机种数据。</summary>
    public bool 成功 { get; init; }

    /// <summary>解析出的机种数据（失败时为 <c>null</c>）。</summary>
    public 配方数据? 配方 { get; init; }

    /// <summary>致命错误（会导致导入失败）。</summary>
    public List<string> 错误集合 { get; init; } = new();

    /// <summary>非致命提示（例如某些行被跳过）。</summary>
    public List<string> 警告 { get; init; } = new();

    /// <summary>给操作员看的一句话摘要。</summary>
    public string 摘要 => 成功
        ? $"解析成功：机种 {(配方?.号 ?? 0):00}「{配方?.名称}」，{配方?.区段快照.Count ?? 0} 项机种数据"
          + (警告.Count > 0 ? $"，{警告.Count} 条提示" : string.Empty)
        : $"解析失败：{错误集合.Count} 个错误";
}

/// <summary>
/// 机种数据的 CSV 编解码。
///
/// <para><b>定位：</b>CSV 是<b>交换格式</b>——用于在 Excel 里编辑、在机种之间拷贝、或从别处导入。
/// <b>程序主存仍是 <c>recipes.json</c></b>，CSV 必须经「导入」动作才会生效，
/// 避免出现两个真相源互相覆盖。</para>
///
/// <para><b>一个机种 = 一个 CSV 文件</b>，两段：机种信息（号 / 名称）+ 机种区段寄存器
/// （D2500–D3999 的稀疏地址/值对，按地址升序）。导出用 UTF-8 带 BOM，Excel 双击即可正确显示中文。</para>
///
/// <para><b>为什么导"寄存器"而不是逐列拆点表：</b>现场口径是"整个 D2500–D3999 都是机种区域"，
/// 寄存器快照与这个定义一一对应；若按点表列拆，以后新增一个元件就会让老 CSV 对不上号。
/// 地址列写成 <c>D2500</c> 形式，现场在 Excel 里改值同样直观。</para>
/// </summary>
public static class 配方CSV编解码器
{
    private const string 区段机种 = "机种";
    private const string 区段寄存器 = "寄存器";

    private const string 表头机种 = "号,名称";
    private const string 表头寄存器 = "地址,值";

    /// <summary>把机种数据编码为 CSV 文本。</summary>
    public static string 编码(配方数据 配方项)
    {
        ArgumentNullException.ThrowIfNull(配方项);

        var 文本 = new StringBuilder();

        文本.AppendLine("# 点胶锁付机 · 机种数据交换文件");
        文本.AppendLine($"# 机种区段：D{机种区段.起始} – D{机种区段.结束}（这些数据跟随机种切换）");
        文本.AppendLine();

        文本.AppendLine($"[{区段机种}]");
        文本.AppendLine(表头机种);
        文本.AppendLine($"{配方项.号},{转义(配方项.名称)}");
        文本.AppendLine();

        文本.AppendLine($"[{区段寄存器}]");
        文本.AppendLine(表头寄存器);

        foreach (var (偏移, 值) in 配方项.区段快照.OrderBy(项 => 项.Key))
        {
            文本.AppendLine($"D{机种区段.起始 + 偏移},{值.ToString(CultureInfo.InvariantCulture)}");
        }

        return 文本.ToString();
    }

    /// <summary>解析 CSV 文本（只解析，不写入任何数据）。</summary>
    /// <param name="CSV文本">CSV 内容。</param>
    /// <param name="回退配方号">文件里没写机种号时使用的号。</param>
    public static CSV导入结果 解码(string CSV文本, int 回退配方号)
    {
        if (string.IsNullOrWhiteSpace(CSV文本))
        {
            return new CSV导入结果
            {
                成功 = false,
                错误集合 = { "文件为空。" },
            };
        }

        var 配方项 = new 配方数据 { 号 = 回退配方号 };
        var 错误列表 = new List<string>();
        var 警告列表 = new List<string>();

        var 当前段 = string.Empty;
        var 行号 = 0;

        foreach (var 原行 in CSV文本.Split('\n'))
        {
            行号++;
            var 行 = 原行.TrimEnd('\r').Trim();

            if (行.Length == 0 || 行.StartsWith('#'))
            {
                continue;
            }

            if (行.StartsWith('[') && 行.EndsWith(']'))
            {
                当前段 = 行[1..^1].Trim();
                continue;
            }

            var 单元格 = 拆分(行);

            // 表头行：跳过（兼容各段都带表头）
            if (单元格.Count > 0 && 单元格[0] is "号" or "地址" or "键")
            {
                continue;
            }

            switch (当前段)
            {
                case 区段机种:
                    解析机种行(单元格, 配方项, 警告列表, 行号);
                    break;

                case 区段寄存器:
                    解析寄存器行(单元格, 配方项, 错误列表, 行号);
                    break;

                default:
                    // 段外的散行（老格式或手工添加）不算错误，只提示
                    警告列表.Add($"第 {行号} 行在已知段落之外，已忽略。");
                    break;
            }
        }

        if (错误列表.Count > 0)
        {
            return new CSV导入结果
            {
                成功 = false,
                错误集合 = 错误列表,
                警告 = 警告列表,
            };
        }

        return new CSV导入结果
        {
            成功 = true,
            配方 = 配方项,
            警告 = 警告列表,
        };
    }

    // ── 内部 ────────────────────────────────────────────────────────────

    private static void 解析机种行(
        List<string> 单元格, 配方数据 配方项, List<string> 警告列表, int 行号)
    {
        if (单元格.Count < 2)
        {
            警告列表.Add($"第 {行号} 行格式不正确，期望「号,名称」。");
            return;
        }

        if (int.TryParse(单元格[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var 号)
            && 号 > 0)
        {
            配方项.号 = 号;
        }

        配方项.名称 = 反转义(单元格[1]);
    }

    private static void 解析寄存器行(
        List<string> 单元格, 配方数据 配方项, List<string> 错误列表, int 行号)
    {
        if (单元格.Count < 2)
        {
            错误列表.Add($"第 {行号} 行：寄存器行必须是「地址,值」。");
            return;
        }

        var 地址文本 = 单元格[0].Trim().TrimStart('D', 'd');

        if (!int.TryParse(地址文本, NumberStyles.Integer, CultureInfo.InvariantCulture, out var 地址))
        {
            错误列表.Add($"第 {行号} 行：地址「{单元格[0]}」不是有效的 D 区地址。");
            return;
        }

        if (!机种区段.包含(地址))
        {
            错误列表.Add(
                $"第 {行号} 行：地址 D{地址} 不在机种区段 "
                + $"D{机种区段.起始}–D{机种区段.结束} 内，已拒绝导入。");
            return;
        }

        if (!int.TryParse(单元格[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var 值)
            || 值 is < 0 or > ushort.MaxValue)
        {
            错误列表.Add($"第 {行号} 行：值「{单元格[1]}」不是有效的 16 位无符号整数（0–65535）。");
            return;
        }

        配方项.置(地址, 值);
    }

    /// <summary>CSV 按简单规则拆分（本格式不出现逗号本身）。</summary>
    private static List<string> 拆分(string 行) =>
        行.Split(',').Select(单元格 => 单元格.Trim()).ToList();

    private static string 转义(string 文本值) => 文本值.Replace(',', '，').Replace('\n', ' ');

    private static string 反转义(string 文本值) => 文本值.Trim();
}
