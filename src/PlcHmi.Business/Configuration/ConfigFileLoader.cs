using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PlcHmi.Business.Configuration;

/// <summary>一次配置加载的结果。</summary>
public enum LoadResultSplitCategory
{
    /// <summary>主文件正常读取。</summary>
    Loaded,

    /// <summary>主文件损坏或不可读，已用 <c>.bak</c> 副本恢复并继续。</summary>
    RestoredFromBackup,

    /// <summary>文件不存在（首次运行，或该配置尚未部署）。</summary>
    Missing,

    /// <summary>主文件与备份都不可用 —— 相关功能将以空/默认数据降级运行。</summary>
    Failed,
}

/// <summary>
/// 配置加载结果。除了数据本身，还带上"这份数据可不可信"的信息。
///
/// <para><b>为什么要把 outcome 往外传：</b>过去的加载器失败时只写一行日志就返回空集合，
/// 程序看起来一切正常 —— 对报警表而言，那意味着"界面健康、但所有故障位都被无视"。
/// 加载结果必须能被上层看见，才能变成操作员看得见的告警。</para>
/// </summary>
/// <typeparam name="T">反序列化出的配置类型。</typeparam>
/// <param name="Value">解析成功的数据；失败为 <c>null</c>。</param>
/// <param name="Outcome">本次加载的结果分类。</param>
/// <param name="Path">被加载的文件路径。</param>
/// <param name="Error">失败原因（成功时为 <c>null</c>）。</param>
public sealed record ConfigLoadResult<T>(
    T? Value, LoadResultSplitCategory loadResult, string path, string? ErrorInfo)
    where T : class
{
    /// <summary>是否需要向操作员示警（数据丢失或已降级）。</summary>
    public bool NeedCloseNote => loadResult is LoadResultSplitCategory.Failed or LoadResultSplitCategory.RestoredFromBackup;
}

/// <summary>
/// 配置文件的安全读取：主文件解析失败时，自动回退到 <see cref="AtomicFile"/> 留下的 <c>.bak</c>。
///
/// <para><b>为什么必须有这一层：</b><see cref="AtomicFile"/> 每次保存都会留一份 <c>.bak</c>，
/// 但在此之前<b>没有任何代码去读它</b> —— 备份是死的。于是"断电 → 主文件损坏 →
/// 启动时解析失败 → 以空数据运行"这条链依然成立，配方照样全丢。
/// 原子写与备份恢复是同一件事的两半，缺一半等于没做。</para>
///
/// <para><b>恢复顺序：</b></para>
/// <list type="number">
///   <item>读主文件并反序列化 —— 成功即返回 <see cref="配置加载结果枚举.Loaded"/>；</item>
///   <item>主文件不在 → <see cref="配置加载结果枚举.Missing"/>（上层可据此创建默认配置）；</item>
///   <item>主文件在但坏了 → 读 <c>.bak</c>；成功则把 <c>.bak</c> <b>修复回主文件</b>，
///         返回 <see cref="配置加载结果枚举.RestoredFromBackup"/>；</item>
///   <item>两者都坏 → <see cref="配置加载结果枚举.Failed"/>，由上层决定降级策略。</item>
/// </list>
/// </summary>
public static class ConfigFileLoader
{
    /// <summary>同步加载 JSON 配置（用于构造函数等同步路径）。</summary>
    public static ConfigLoadResult<T> LoadJson<T>(
        string filePath, JsonSerializerOptions Options, ILogger? Log = null)
        where T : class
    {
        if (TryRead<T>(filePath, Options, out var Value, out var error))
        {
            return new ConfigLoadResult<T>(Value, LoadResultSplitCategory.Loaded, filePath, null);
        }

        if (!File.Exists(filePath))
        {
            Log?.LogWarning("未找到配置文件：{Path}", filePath);
            return new ConfigLoadResult<T>(null, LoadResultSplitCategory.Missing, filePath, error);
        }

        return Recover<T>(filePath, Options, error, Log);
    }

    /// <summary>异步加载 JSON 配置（用于 async 路径，避免在异步方法里同步阻塞读盘）。</summary>
    public static async Task<ConfigLoadResult<T>> LoadJsonAsync<T>(
        string filePath, JsonSerializerOptions Options, ILogger? Log = null,
        CancellationToken cancellationToken = default)
        where T : class
    {
        if (await TryReadAsync<T>(filePath, Options, cancellationToken).ConfigureAwait(false) is { } Success)
        {
            return new ConfigLoadResult<T>(Success, LoadResultSplitCategory.Loaded, filePath, null);
        }

        if (!File.Exists(filePath))
        {
            Log?.LogWarning("未找到配置文件：{Path}", filePath);
            return new ConfigLoadResult<T>(null, LoadResultSplitCategory.Missing, filePath, null);
        }

        // 主文件存在但读不出来 → 试备份
        if (await TryReadAsync<T>(filePath + ".bak", Options, cancellationToken).ConfigureAwait(false)
            is { } comeSelfBackup)
        {
            var restored = AtomicFile.TryRestoreBackup(filePath);

            Log?.LogError(
                "配置文件损坏，已{mode}：{Path}",
                restored ? "从 .bak 恢复（最近一次改动可能已丢失）" : "读到 .bak 但回写失败，请检查目录权限",
                filePath);

            return new ConfigLoadResult<T>(
                comeSelfBackup,
                LoadResultSplitCategory.RestoredFromBackup,
                filePath,
                restored ? "主文件损坏，已从备份恢复" : "主文件损坏，备份可用但回写失败");
        }

        Log?.LogError("配置文件与备份均不可用：{Path}", filePath);
        return new ConfigLoadResult<T>(null, LoadResultSplitCategory.Failed, filePath, "主文件与备份均损坏");
    }

    private static ConfigLoadResult<T> Recover<T>(
        string filePath, JsonSerializerOptions Options, string? masterError, ILogger? Log)
        where T : class
    {
        if (TryRead<T>(filePath + ".bak", Options, out var comeSelfBackup, out _))
        {
            var restored = AtomicFile.TryRestoreBackup(filePath);

            Log?.LogError(
                "配置文件损坏，已{mode}：{Path}",
                restored ? "从 .bak 恢复（最近一次改动可能已丢失）" : "读到 .bak 但回写失败，请检查目录权限",
                filePath);

            return new ConfigLoadResult<T>(
                comeSelfBackup,
                LoadResultSplitCategory.RestoredFromBackup,
                filePath,
                restored ? "主文件损坏，已从备份恢复" : "主文件损坏，备份可用但回写失败");
        }

        Log?.LogError("配置文件与备份均不可用：{Path}（{Error}）", filePath, masterError);
        return new ConfigLoadResult<T>(null, LoadResultSplitCategory.Failed, filePath, "主文件与备份均损坏");
    }

    private static bool TryRead<T>(
        string filePath, JsonSerializerOptions Options, out T? Value, out string? error)
        where T : class
    {
        Value = null;
        error = null;

        try
        {
            if (!File.Exists(filePath))
            {
                error = "文件不存在";
                return false;
            }

            using var stream = File.OpenRead(filePath);
            Value = JsonSerializer.Deserialize<T>(stream, Options);

            if (Value is null)
            {
                error = "内容为空";
                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            error = exception.Message;
            return false;
        }
    }

    private static async Task<T?> TryReadAsync<T>(
        string filePath, JsonSerializerOptions Options, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            await using var stream = File.OpenRead(filePath);
            return await JsonSerializer
                .DeserializeAsync<T>(stream, Options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
