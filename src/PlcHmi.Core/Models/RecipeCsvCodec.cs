using System.Globalization;
using System.Text;

namespace PlcHmi.Core.Models;

/// <summary>CSV 导入结果。</summary>
public sealed class CsvImportResult
{
    /// <summary>是否成功解析出可用的机种数据。</summary>
    public bool Success { get; init; }

    /// <summary>解析出的机种数据（失败时为 <c>null</c>）。</summary>
    public RecipeData? Recipe { get; init; }

    /// <summary>致命错误（会导致导入失败）。</summary>
    public List<string> ErrorCollection { get; init; } = new();

    /// <summary>非致命提示（例如某些行被跳过）。</summary>
    public List<string> Warning { get; init; } = new();

    /// <summary>给操作员看的一句话摘要。</summary>
    public string Summary => Success
        ? $"解析成功：机种 {(Recipe?.Number ?? 0):00}「{Recipe?.Name}」，{Recipe?.SectionSnapshot.Count ?? 0} 项机种数据"
          + (Warning.Count > 0 ? $"，{Warning.Count} 条提示" : string.Empty)
        : $"解析失败：{ErrorCollection.Count} 个错误";
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
public static class RecipeCsvCodec
{
    private const string sectionMachineModel = "机种";
    private const string sectionRegister = "寄存器";

    private const string headerMachineModel = "号,名称";
    private const string headerRegister = "地址,值";

    /// <summary>把机种数据编码为 CSV 文本。</summary>
    public static string Encode(RecipeData recipeItem)
    {
        ArgumentNullException.ThrowIfNull(recipeItem);

        var Text = new StringBuilder();

        Text.AppendLine("# 点胶锁付机 · 机种数据交换文件");
        Text.AppendLine($"# 机种区段：D{MachineModelSection.StartAt} – D{MachineModelSection.end}（这些数据跟随机种切换）");
        Text.AppendLine();

        Text.AppendLine($"[{sectionMachineModel}]");
        Text.AppendLine(headerMachineModel);
        Text.AppendLine($"{recipeItem.Number},{Escape(recipeItem.Name)}");
        Text.AppendLine();

        Text.AppendLine($"[{sectionRegister}]");
        Text.AppendLine(headerRegister);

        foreach (var (Offset, Value) in recipeItem.SectionSnapshot.OrderBy(Item => Item.Key))
        {
            Text.AppendLine($"D{MachineModelSection.StartAt + Offset},{Value.ToString(CultureInfo.InvariantCulture)}");
        }

        return Text.ToString();
    }

    /// <summary>解析 CSV 文本（只解析，不写入任何数据）。</summary>
    /// <param name="csvText">CSV 内容。</param>
    /// <param name="fallbackRecipeNumber">文件里没写机种号时使用的号。</param>
    public static CsvImportResult Decode(string csvText, int fallbackRecipeNumber)
    {
        if (string.IsNullOrWhiteSpace(csvText))
        {
            return new CsvImportResult
            {
                Success = false,
                ErrorCollection = { "文件为空。" },
            };
        }

        var recipeItem = new RecipeData { Number = fallbackRecipeNumber };
        var errorList = new List<string>();
        var warningList = new List<string>();

        var currentSection = string.Empty;
        var rowNumber = 0;

        foreach (var OriginRow in csvText.Split('\n'))
        {
            rowNumber++;
            var Row = OriginRow.TrimEnd('\r').Trim();

            if (Row.Length == 0 || Row.StartsWith('#'))
            {
                continue;
            }

            if (Row.StartsWith('[') && Row.EndsWith(']'))
            {
                currentSection = Row[1..^1].Trim();
                continue;
            }

            var cell = Split(Row);

            // 表头行：跳过（兼容各段都带表头）
            if (cell.Count > 0 && cell[0] is "号" or "地址" or "键")
            {
                continue;
            }

            switch (currentSection)
            {
                case sectionMachineModel:
                    ParseMachineModelRow(cell, recipeItem, warningList, rowNumber);
                    break;

                case sectionRegister:
                    ParseRegisterRow(cell, recipeItem, errorList, rowNumber);
                    break;

                default:
                    // 段外的散行（老格式或手工添加）不算错误，只提示
                    warningList.Add($"第 {rowNumber} 行在已知段落之外，已忽略。");
                    break;
            }
        }

        if (errorList.Count > 0)
        {
            return new CsvImportResult
            {
                Success = false,
                ErrorCollection = errorList,
                Warning = warningList,
            };
        }

        return new CsvImportResult
        {
            Success = true,
            Recipe = recipeItem,
            Warning = warningList,
        };
    }

    // ── 内部 ────────────────────────────────────────────────────────────

    private static void ParseMachineModelRow(
        List<string> cell, RecipeData recipeItem, List<string> warningList, int rowNumber)
    {
        if (cell.Count < 2)
        {
            warningList.Add($"第 {rowNumber} 行格式不正确，期望「号,名称」。");
            return;
        }

        if (int.TryParse(cell[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var Number)
            && Number > 0)
        {
            recipeItem.Number = Number;
        }

        recipeItem.Name = Unescape(cell[1]);
    }

    private static void ParseRegisterRow(
        List<string> cell, RecipeData recipeItem, List<string> errorList, int rowNumber)
    {
        if (cell.Count < 2)
        {
            errorList.Add($"第 {rowNumber} 行：寄存器行必须是「地址,值」。");
            return;
        }

        var AddressText = cell[0].Trim().TrimStart('D', 'd');

        if (!int.TryParse(AddressText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var Address))
        {
            errorList.Add($"第 {rowNumber} 行：地址「{cell[0]}」不是有效的 D 区地址。");
            return;
        }

        if (!MachineModelSection.Contains(Address))
        {
            errorList.Add(
                $"第 {rowNumber} 行：地址 D{Address} 不在机种区段 "
                + $"D{MachineModelSection.StartAt}–D{MachineModelSection.end} 内，已拒绝导入。");
            return;
        }

        if (!int.TryParse(cell[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var Value)
            || Value is < 0 or > ushort.MaxValue)
        {
            errorList.Add($"第 {rowNumber} 行：值「{cell[1]}」不是有效的 16 位无符号整数（0–65535）。");
            return;
        }

        recipeItem.put(Address, Value);
    }

    /// <summary>CSV 按简单规则拆分（本格式不出现逗号本身）。</summary>
    private static List<string> Split(string Row) =>
        Row.Split(',').Select(cell => cell.Trim()).ToList();

    private static string Escape(string textValue) => textValue.Replace(',', '，').Replace('\n', ' ');

    private static string Unescape(string textValue) => textValue.Trim();
}
