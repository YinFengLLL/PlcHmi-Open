namespace PlcHmi.Business.Configuration;

/// <summary>
/// 一条配置健康问题。
/// </summary>
/// <param name="DisplayName">给操作员看的配置名（如「报警配置」）。</param>
/// <param name="Outcome">加载结果分类。</param>
/// <param name="Detail">补充说明（恢复方式 / 影响范围）。</param>
public sealed record ConfigIssue(string DisplayName, LoadResultSplitCategory loadResult, string detail)
{
    /// <summary>数据不可用 —— 相关功能已停摆在空数据上，必须让操作员知道。</summary>
    public bool IsBlocked => loadResult is LoadResultSplitCategory.Failed;

    /// <summary>一行摘要，直接用于横幅文案。</summary>
    public string Summary => loadResult switch
    {
        LoadResultSplitCategory.Failed => $"{DisplayName}：不可用（{detail}）",
        LoadResultSplitCategory.RestoredFromBackup => $"{DisplayName}：已从备份恢复（{detail}）",
        _ => $"{DisplayName}：{detail}",
    };
}

/// <summary>
/// 配置健康登记处。
///
/// <para><b>为什么需要它：</b>本项目刻意做成"配置读不出来也能启动"，这对可用性是对的，
/// 但对一台机床是危险的 —— 报警表损坏时程序照样启动、界面照样清爽，
/// 而操作员面对的是<b>一台所有故障位都被无视的设备</b>。崩溃看得见，失去报警看不见。</para>
///
/// <para>因此每个加载器把结果登记到这里，界面据此显示常驻横幅。
/// 降级可以是"允许的"，但不可以是"无声的"。</para>
/// </summary>
public interface IConfigHealth
{
    /// <summary>已登记的、需要操作员知晓的问题。</summary>
    IReadOnlyList<ConfigIssue> IssueCollection { get; }

    /// <summary>是否存在需要示警的问题。</summary>
    bool IsDegraded { get; }

    /// <summary>问题集合变化时触发（可能来自后台线程，订阅方自行切 UI 线程）。</summary>
    event EventHandler? changed;

    /// <summary>登记一条问题。</summary>
    void Report(ConfigIssue issueItem);
}

/// <summary>默认实现：进程内单例。</summary>
public sealed class ConfigHealthService : IConfigHealth
{
    private readonly object _sync = new();
    private readonly List<ConfigIssue> _issue = new();

    /// <inheritdoc />
    public event EventHandler? changed;

    /// <inheritdoc />
    public IReadOnlyList<ConfigIssue> IssueCollection
    {
        get
        {
            lock (_sync)
            {
                return _issue.ToList();
            }
        }
    }

    /// <inheritdoc />
    public bool IsDegraded
    {
        get
        {
            lock (_sync)
            {
                return _issue.Count > 0;
            }
        }
    }

    /// <inheritdoc />
    public void Report(ConfigIssue issueItem)
    {
        ArgumentNullException.ThrowIfNull(issueItem);

        lock (_sync)
        {
            // 同一份配置重复加载（例如手动重载）时不重复登记
            var currentHas = _issue.FindIndex(Entry => Entry.DisplayName == issueItem.DisplayName);

            if (currentHas >= 0)
            {
                _issue[currentHas] = issueItem;
            }
            else
            {
                _issue.Add(issueItem);
            }
        }

        changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>把加载结果登记到 <see cref="IConfigHealth"/> 的便捷扩展。</summary>
public static class ConfigHealthExtensions
{
    /// <summary>
    /// 按加载结果登记问题。<see cref="配置加载结果枚举.Loaded"/> 与
    /// <see cref="配置加载结果枚举.Missing"/> 不登记 —— 前者正常，
    /// 后者是首次运行（缺文件时各加载器自有默认值，不算降级）。
    /// </summary>
    public static void Report<T>(
        this IConfigHealth? Health, string displayNameValue, ConfigLoadResult<T> resultValue)
        where T : class
    {
        if (Health is null || !resultValue.NeedCloseNote)
        {
            return;
        }

        Health.Report(new ConfigIssue(
            displayNameValue,
            resultValue.loadResult,
            resultValue.ErrorInfo ?? resultValue.loadResult.ToString()));
    }

    /// <summary>登记一条与配置文件无关的降级（如启动期配置回退默认值）。</summary>
    public static void ReportFallback(this IConfigHealth? Health, string displayNameValue, string issueDetail)
    {
        Health?.Report(new ConfigIssue(displayNameValue, LoadResultSplitCategory.Failed, issueDetail));
    }
}
