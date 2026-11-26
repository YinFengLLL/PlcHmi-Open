using System.Text;
using Microsoft.Extensions.Logging;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// 配方服务的「CSV 导入导出」部分。
///
/// <para><b>为什么拆成 partial 文件</b>：主文件已越过项目规模门禁（400 行）的基线，
/// 而 CSV 交换是一条完整的、独立的职责线（编码 / 解码 / 文件读写 / 导入落盘），
/// 拆开后主文件只留"参数 · 示教 · 机种切换"三件事。</para>
///
/// <para><b>导入的边界语义</b>：CSV 解析失败（用户输入错误）返回结果对象而不是抛异常；
/// 权限不足与 IO 失败仍抛异常——调用方按"可预期的输入错误 vs 系统错误"分流。</para>
/// </summary>
public sealed partial class RecipeService
{
    /// <inheritdoc />
    public string ExportCsv(int RecipeNumber)
    {
        ValidateRecipeNumber(RecipeNumber);

        var data = _store.Find(RecipeNumber) ?? new RecipeData { Number = RecipeNumber };
        return RecipeCsvCodec.Encode(data);
    }

    /// <inheritdoc />
    public async Task ExportCsvToFileAsync(
        int RecipeNumber, string filePath, CancellationToken cancellationToken = default)
    {
        var Text = ExportCsv(RecipeNumber);

        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // 必须带 BOM：否则 Excel 双击打开时中文会乱码
        await File.WriteAllTextAsync(
                filePath, Text,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                cancellationToken)
            .ConfigureAwait(false);

        _log.LogInformation("机种 {No} 已导出到 {Path}", RecipeNumber, filePath);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ExportAllCsvToDirectoryAsync(
        string dir, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(dir);

        var written = new List<string>();

        for (var Number = 1; Number <= _config.RecipeCount; Number++)
        {
            var data = _store.Find(Number);

            // 没有数据的机种也导出 —— 作为空白模板，方便先填后导
            var fileName = string.IsNullOrWhiteSpace(data?.Name)
                ? $"recipe-{Number:00}.csv"
                : $"recipe-{Number:00}-{CleanFileName(data!.Name)}.csv";

            var filePath = Path.Combine(dir, fileName);
            await ExportCsvToFileAsync(Number, filePath, cancellationToken).ConfigureAwait(false);
            written.Add(filePath);
        }

        _log.LogInformation("已导出 {Count} 个机种到 {Dir}", written.Count, dir);
        return written;
    }

    /// <inheritdoc />
    public CsvImportResult ParseCsv(string csvText, int fallbackRecipeNumber) =>
        RecipeCsvCodec.Decode(csvText, fallbackRecipeNumber);

    /// <inheritdoc />
    public async Task<CsvImportResult> FromFileImportCsvAsync(
        string filePath, int? targetRecipeNumber = null, CancellationToken cancellationToken = default)
    {
        // 导入会整体覆盖目标机种的机种区段数据并落盘，必须经服务层权限校验
        // （解析阶段不落盘，无需校验；这里在第一次写主存前拦截）。
        _permission.WantRequire(Permission.SwitchRecipe);

        if (!File.Exists(filePath))
        {
            return Failed($"文件不存在：{filePath}");
        }

        string Text;

        try
        {
            Text = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return Failed($"读取文件失败：{exception.Message}");
        }

        var resultValue = ParseCsv(Text, targetRecipeNumber ?? _currentNumber);

        if (!resultValue.Success || resultValue.Recipe is null)
        {
            return resultValue;
        }

        // 目标机种号优先用显式指定的 —— 这样就能实现「把机种 A 拷到机种 B」
        var targetNumber = targetRecipeNumber ?? resultValue.Recipe.Number;

        if (targetNumber < 1 || targetNumber > _config.RecipeCount)
        {
            return new CsvImportResult
            {
                Success = false,
                ErrorCollection = { $"目标机种号 {targetNumber} 超出范围（1 – {_config.RecipeCount}）。" },
                Warning = resultValue.Warning,
            };
        }

        resultValue.Recipe.Number = targetNumber;

        var data = _store.GetOrCreate(targetNumber);
        data.Name = resultValue.Recipe.Name;
        data.SectionSnapshot = new Dictionary<int, int>(resultValue.Recipe.SectionSnapshot);

        await _store.SaveAsync(cancellationToken).ConfigureAwait(false);

        // 导入只改本机数据，不写 PLC —— 要生效仍需「切换机种」或「下发机种」
        _log.LogInformation("已从 {Path} 导入到机种 {No}", filePath, targetNumber);
        recipeChanged?.Invoke(this, EventArgs.Empty);

        return resultValue;
    }

    private static CsvImportResult Failed(string Message) =>
        new() { Success = false, ErrorCollection = { Message } };

    /// <summary>把机种名里不能做文件名的字符替换掉。</summary>
    private static string CleanFileName(string nameValue)
    {
        var Invalid = Path.GetInvalidFileNameChars();
        var Sanitized = new string(nameValue.Select(character => Invalid.Contains(character) ? '_' : character).ToArray()).Trim();

        return Sanitized.Length == 0 ? "未命名" : Sanitized;
    }
}
