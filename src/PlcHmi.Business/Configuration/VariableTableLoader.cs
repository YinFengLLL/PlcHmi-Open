using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Configuration;

/// <summary>
/// 变量表加载器。读取 <c>config/variables.json</c>（由变量表 CSV 导入生成）。
///
/// <para><b>文件缺失不是错误</b>：变量绑定是可选能力，未导入变量表时程序行为与旧版完全一致。</para>
/// </summary>
public static class VariableTableLoader
{
    /// <summary>加载变量表。文件缺失 / 损坏时回退空表（<paramref name="error"/> 给出原因）。</summary>
    public static VariableTableDocument Load(out string? error, string? filePath = null)
    {
        error = null;
        filePath ??= ConfigPath.VariableTable;

        if (!File.Exists(filePath))
        {
            return new VariableTableDocument();
        }

        try
        {
            using var stream = File.OpenRead(filePath);
            var Document = JsonSerializer.Deserialize<VariableTableDocument>(stream, JsonConfig.Read);

            if (Document is null)
            {
                error = $"变量表文件内容为空：{filePath}。";
                return new VariableTableDocument();
            }

            return Document;
        }
        catch (JsonException exception)
        {
            error = $"解析变量表文件失败（JSON 格式）：{filePath} —— {exception.Message}";
            return new VariableTableDocument();
        }
        catch (IOException exception)
        {
            error = $"读取变量表文件失败：{filePath} —— {exception.Message}";
            return new VariableTableDocument();
        }
        catch (UnauthorizedAccessException exception)
        {
            error = $"读取变量表文件被拒绝：{filePath} —— {exception.Message}";
            return new VariableTableDocument();
        }
    }

    /// <summary>把变量表文档序列化并原子落盘。</summary>
    public static async Task PersistAsync(VariableTableDocument Document, CancellationToken cancellationToken)
    {
        var jsonText = JsonSerializer.Serialize(Document, JsonConfig.Write);
        await AtomicFile.WriteAllTextAsync(ConfigPath.VariableTable, jsonText, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// 变量绑定回填助手。
///
/// <para><b>绑定语义：</b>配置条目填写「变量」后，启动 / 加载时从变量表查软元件地址回填到
/// <c>地址</c> 字段——下游全部服务继续按地址工作（Modbus 模式零改动）；
/// OPC UA 模式再由 NodeId 推导器按变量名做符号寻址。</para>
///
/// <para><b>失败降级</b>：变量不存在 / 地址无法寻址时保留原地址并返回错误说明，
/// 由调用方经配置健康登记——禁止无声降级（红线 R3）。</para>
/// </summary>
public static class VariableBindingBackfill
{
    /// <summary>
    /// 解析变量绑定，返回回填后的软元件地址。
    /// </summary>
    /// <param name="VariableName">配置条目上的变量名；空表示未绑定（返回 <paramref name="currentAddress"/>）。</param>
    /// <param name="Table">变量表服务；未注入时视为未绑定。</param>
    /// <param name="Parser">软元件地址解析器（校验地址可寻址）。</param>
    /// <param name="currentAddress">回填失败时保留的现有地址。</param>
    /// <param name="error">失败原因；成功为空。</param>
    /// <returns>回填后的地址；失败返回 <paramref name="currentAddress"/>。</returns>
    public static string ParseAddress(
        string? VariableName,
        IVariableTableService? Table,
        IAddressParser Parser,
        string currentAddress,
        out string error)
    {
        if (Table is null)
        {
            error = string.Empty;
            return currentAddress;
        }

        return ParseAddress(VariableName, Table.Find(VariableName), Parser, currentAddress, out error);
    }

    /// <summary>按变量条目解析绑定（核心逻辑，其余重载最终都走这里）。</summary>
    public static string ParseAddress(
        string? VariableName,
        VariableTableEntry? Entry,
        IAddressParser Parser,
        string currentAddress,
        out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(VariableName))
        {
            return currentAddress;
        }

        if (Entry is null)
        {
            error = $"变量「{VariableName}」不在变量表中（可能未导入或已改名），保留原地址 {currentAddress}。";
            return currentAddress;
        }

        if (!Parser.TryParse(Entry.SoftElementAddress, out var addressValue) || addressValue is null)
        {
            error = $"变量「{VariableName}」的地址 {Entry.SoftElementAddress} 无法按当前机型寻址"
                   + "（请检查地址格式与机型支持的软元件范围，D/R 区字内位写作 D1210.2），保留原地址。";
            return currentAddress;
        }

        return Entry.SoftElementAddress;
    }

    /// <summary>
    /// 批量回填一组参数条目的变量绑定（延时表 / 按钮表共用）。
    /// 变量失效时保留原地址并经 <paramref name="Health"/> 登记警告——禁止无声降级。
    /// </summary>
    public static void ApplyEntryBinding(
        IEnumerable<ParameterEntryDefinition> EntryCollection, string categoryName,
        IVariableTableService? Table, IAddressParser Parser, IConfigHealth? Health)
    {
        foreach (var EntryItem in EntryCollection)
        {
            EntryItem.Address = ParseAddress(EntryItem.Variable, Table, Parser, EntryItem.Address, out var error);

            if (error.Length > 0)
            {
                Health?.ReportFallback($"{categoryName}「{EntryItem.Name}」", error);
            }
        }
    }

    /// <summary>
    /// 把 recipe-config.json 的变量绑定批量回填到地址字段（App 启动期、DI 建立之前调用）。
    /// </summary>
    /// <param name="Config">已加载的配方配置（就地修改地址）。</param>
    /// <param name="Document">已加载的变量表文档。</param>
    /// <param name="Parser">软元件地址解析器。</param>
    /// <returns>回填失败的说明列表（空表示全部成功或没有绑定）。</returns>
    public static List<string> ApplyRecipeConfig(RecipeConfig Config, VariableTableDocument Document, IAddressParser Parser)
    {
        var warningList = new List<string>();

        foreach (var specItem in Config.Parameter)
        {
            if (string.IsNullOrWhiteSpace(specItem.Variable))
            {
                continue;
            }

            var backfillAddress = ParseAddress(specItem.Variable, Document.Find(specItem.Variable), Parser,
                FormatDArea(specItem.Address), out var error);

            if (error.Length > 0)
            {
                warningList.Add($"参数项「{specItem.DisplayName}」：{error}");
                continue;
            }

            if (TryParseDArea(backfillAddress, out var Sequence))
            {
                specItem.Address = Sequence;
            }
        }

        foreach (var Level in Config.JogSpeedLevel)
        {
            if (string.IsNullOrWhiteSpace(Level.Variable))
            {
                continue;
            }

            var backfillAddress = ParseAddress(Level.Variable, Document.Find(Level.Variable), Parser,
                FormatDArea(Level.Address), out var error);

            if (error.Length > 0)
            {
                warningList.Add($"点动速度档位（{Level.Axis} 轴）：{error}");
                continue;
            }

            if (TryParseDArea(backfillAddress, out var Sequence))
            {
                Level.Address = Sequence;
            }
        }

        var Widget = Config.Widget;
        BackfillWidgetAddress(warningList, "机种号", Widget.RecipeNumberVariable, Widget.RecipeNumberAddress, Document, Parser,
            Sequence => Widget.RecipeNumberAddress = Sequence);
        BackfillWidgetAddress(warningList, "机种总数", Widget.RecipeCountVariable, Widget.RecipeCountAddress, Document, Parser,
            Sequence => Widget.RecipeCountAddress = Sequence);
        BackfillWidgetAddress(warningList, "示教点索引", Widget.TeachIndexVariable, Widget.TeachIndexAddress, Document, Parser,
            Sequence => Widget.TeachIndexAddress = Sequence);
        BackfillWidgetAddress(warningList, "当前面号", Widget.TeachFaceVariable, Widget.TeachFaceAddress, Document, Parser,
            Sequence => Widget.TeachFaceAddress = Sequence);

        return warningList;
    }

    private static void BackfillWidgetAddress(
        List<string> warningList, string TagText, string? VariableName, int currentAddress,
        VariableTableDocument Document, IAddressParser Parser, Action<int> writeBack)
    {
        if (string.IsNullOrWhiteSpace(VariableName))
        {
            return;
        }

        var backfillAddress = ParseAddress(VariableName, Document.Find(VariableName), Parser, FormatDArea(currentAddress), out var error);

        if (error.Length > 0)
        {
            warningList.Add($"控制地址（{TagText}）：{error}");
            return;
        }

        if (TryParseDArea(backfillAddress, out var Sequence))
        {
            writeBack(Sequence);
        }
    }

    private static string FormatDArea(int addressValue) =>
        "D" + addressValue.ToString(CultureInfo.InvariantCulture);

    private static bool TryParseDArea(string Text, out int Value) =>
        int.TryParse(Text.TrimStart('D', 'd'), NumberStyles.Integer, CultureInfo.InvariantCulture, out Value);
}
