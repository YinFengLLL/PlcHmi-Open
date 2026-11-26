using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace 上位机.业务.配置;

/// <summary>一次配置加载的结果。</summary>
public enum 加载结果分类
{
    /// <summary>主文件正常读取。</summary>
    已加载,

    /// <summary>主文件损坏或不可读，已用 <c>.bak</c> 副本恢复并继续。</summary>
    已从备份恢复,

    /// <summary>文件不存在（首次运行，或该配置尚未部署）。</summary>
    缺失,

    /// <summary>主文件与备份都不可用 —— 相关功能将以空/默认数据降级运行。</summary>
    失败,
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
public sealed record 配置加载结果<T>(
    T? 值, 加载结果分类 加载结果, string 路径, string? 错误信息)
    where T : class
{
    /// <summary>是否需要向操作员示警（数据丢失或已降级）。</summary>
    public bool 需要关注 => 加载结果 is 加载结果分类.失败 or 加载结果分类.已从备份恢复;
}

/// <summary>
/// 配置文件的安全读取：主文件解析失败时，自动回退到 <see cref="原子文件"/> 留下的 <c>.bak</c>。
///
/// <para><b>为什么必须有这一层：</b><see cref="原子文件"/> 每次保存都会留一份 <c>.bak</c>，
/// 但在此之前<b>没有任何代码去读它</b> —— 备份是死的。于是"断电 → 主文件损坏 →
/// 启动时解析失败 → 以空数据运行"这条链依然成立，配方照样全丢。
/// 原子写与备份恢复是同一件事的两半，缺一半等于没做。</para>
///
/// <para><b>恢复顺序：</b></para>
/// <list type="number">
///   <item>读主文件并反序列化 —— 成功即返回 <see cref="配置加载结果枚举.已加载"/>；</item>
///   <item>主文件不在 → <see cref="配置加载结果枚举.缺失"/>（上层可据此创建默认配置）；</item>
///   <item>主文件在但坏了 → 读 <c>.bak</c>；成功则把 <c>.bak</c> <b>修复回主文件</b>，
///         返回 <see cref="配置加载结果枚举.已从备份恢复"/>；</item>
///   <item>两者都坏 → <see cref="配置加载结果枚举.失败"/>，由上层决定降级策略。</item>
/// </list>
/// </summary>
public static class 配置文件加载器
{
    /// <summary>同步加载 JSON 配置（用于构造函数等同步路径）。</summary>
    public static 配置加载结果<T> 加载JSON<T>(
        string 文件路径, JsonSerializerOptions 选项, ILogger? 日志 = null)
        where T : class
    {
        if (尝试读取<T>(文件路径, 选项, out var 值, out var 错误))
        {
            return new 配置加载结果<T>(值, 加载结果分类.已加载, 文件路径, null);
        }

        if (!File.Exists(文件路径))
        {
            日志?.LogWarning("未找到配置文件：{Path}", 文件路径);
            return new 配置加载结果<T>(null, 加载结果分类.缺失, 文件路径, 错误);
        }

        return 恢复<T>(文件路径, 选项, 错误, 日志);
    }

    /// <summary>异步加载 JSON 配置（用于 async 路径，避免在异步方法里同步阻塞读盘）。</summary>
    public static async Task<配置加载结果<T>> 异步加载JSON<T>(
        string 文件路径, JsonSerializerOptions 选项, ILogger? 日志 = null,
        CancellationToken 取消令牌 = default)
        where T : class
    {
        if (await 异步尝试读取<T>(文件路径, 选项, 取消令牌).ConfigureAwait(false) is { } 成功)
        {
            return new 配置加载结果<T>(成功, 加载结果分类.已加载, 文件路径, null);
        }

        if (!File.Exists(文件路径))
        {
            日志?.LogWarning("未找到配置文件：{Path}", 文件路径);
            return new 配置加载结果<T>(null, 加载结果分类.缺失, 文件路径, null);
        }

        // 主文件存在但读不出来 → 试备份
        if (await 异步尝试读取<T>(文件路径 + ".bak", 选项, 取消令牌).ConfigureAwait(false)
            is { } 来自备份)
        {
            var 已恢复 = 原子文件.尝试恢复备份(文件路径);

            日志?.LogError(
                "配置文件损坏，已{mode}：{Path}",
                已恢复 ? "从 .bak 恢复（最近一次改动可能已丢失）" : "读到 .bak 但回写失败，请检查目录权限",
                文件路径);

            return new 配置加载结果<T>(
                来自备份,
                加载结果分类.已从备份恢复,
                文件路径,
                已恢复 ? "主文件损坏，已从备份恢复" : "主文件损坏，备份可用但回写失败");
        }

        日志?.LogError("配置文件与备份均不可用：{Path}", 文件路径);
        return new 配置加载结果<T>(null, 加载结果分类.失败, 文件路径, "主文件与备份均损坏");
    }

    private static 配置加载结果<T> 恢复<T>(
        string 文件路径, JsonSerializerOptions 选项, string? 主错误, ILogger? 日志)
        where T : class
    {
        if (尝试读取<T>(文件路径 + ".bak", 选项, out var 来自备份, out _))
        {
            var 已恢复 = 原子文件.尝试恢复备份(文件路径);

            日志?.LogError(
                "配置文件损坏，已{mode}：{Path}",
                已恢复 ? "从 .bak 恢复（最近一次改动可能已丢失）" : "读到 .bak 但回写失败，请检查目录权限",
                文件路径);

            return new 配置加载结果<T>(
                来自备份,
                加载结果分类.已从备份恢复,
                文件路径,
                已恢复 ? "主文件损坏，已从备份恢复" : "主文件损坏，备份可用但回写失败");
        }

        日志?.LogError("配置文件与备份均不可用：{Path}（{Error}）", 文件路径, 主错误);
        return new 配置加载结果<T>(null, 加载结果分类.失败, 文件路径, "主文件与备份均损坏");
    }

    private static bool 尝试读取<T>(
        string 文件路径, JsonSerializerOptions 选项, out T? 值, out string? 错误)
        where T : class
    {
        值 = null;
        错误 = null;

        try
        {
            if (!File.Exists(文件路径))
            {
                错误 = "文件不存在";
                return false;
            }

            using var 流 = File.OpenRead(文件路径);
            值 = JsonSerializer.Deserialize<T>(流, 选项);

            if (值 is null)
            {
                错误 = "内容为空";
                return false;
            }

            return true;
        }
        catch (Exception 异常) when (异常 is JsonException or IOException or UnauthorizedAccessException)
        {
            错误 = 异常.Message;
            return false;
        }
    }

    private static async Task<T?> 异步尝试读取<T>(
        string 文件路径, JsonSerializerOptions 选项, CancellationToken 取消令牌)
        where T : class
    {
        try
        {
            if (!File.Exists(文件路径))
            {
                return null;
            }

            await using var 流 = File.OpenRead(文件路径);
            return await JsonSerializer
                .DeserializeAsync<T>(流, 选项, 取消令牌)
                .ConfigureAwait(false);
        }
        catch (Exception 异常) when (异常 is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
