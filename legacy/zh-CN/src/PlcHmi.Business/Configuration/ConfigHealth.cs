namespace 上位机.业务.配置;

/// <summary>
/// 一条配置健康问题。
/// </summary>
/// <param name="DisplayName">给操作员看的配置名（如「报警配置」）。</param>
/// <param name="Outcome">加载结果分类。</param>
/// <param name="Detail">补充说明（恢复方式 / 影响范围）。</param>
public sealed record 配置问题(string 显示名称, 加载结果分类 加载结果, string 详情)
{
    /// <summary>数据不可用 —— 相关功能已停摆在空数据上，必须让操作员知道。</summary>
    public bool 是否阻塞 => 加载结果 is 加载结果分类.失败;

    /// <summary>一行摘要，直接用于横幅文案。</summary>
    public string 摘要 => 加载结果 switch
    {
        加载结果分类.失败 => $"{显示名称}：不可用（{详情}）",
        加载结果分类.已从备份恢复 => $"{显示名称}：已从备份恢复（{详情}）",
        _ => $"{显示名称}：{详情}",
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
public interface 配置健康接口
{
    /// <summary>已登记的、需要操作员知晓的问题。</summary>
    IReadOnlyList<配置问题> 问题集合 { get; }

    /// <summary>是否存在需要示警的问题。</summary>
    bool 是否已降级 { get; }

    /// <summary>问题集合变化时触发（可能来自后台线程，订阅方自行切 UI 线程）。</summary>
    event EventHandler? 已变更;

    /// <summary>登记一条问题。</summary>
    void 上报(配置问题 问题项);
}

/// <summary>默认实现：进程内单例。</summary>
public sealed class 配置健康服务 : 配置健康接口
{
    private readonly object _同步 = new();
    private readonly List<配置问题> _问题 = new();

    /// <inheritdoc />
    public event EventHandler? 已变更;

    /// <inheritdoc />
    public IReadOnlyList<配置问题> 问题集合
    {
        get
        {
            lock (_同步)
            {
                return _问题.ToList();
            }
        }
    }

    /// <inheritdoc />
    public bool 是否已降级
    {
        get
        {
            lock (_同步)
            {
                return _问题.Count > 0;
            }
        }
    }

    /// <inheritdoc />
    public void 上报(配置问题 问题项)
    {
        ArgumentNullException.ThrowIfNull(问题项);

        lock (_同步)
        {
            // 同一份配置重复加载（例如手动重载）时不重复登记
            var 现有 = _问题.FindIndex(条目 => 条目.显示名称 == 问题项.显示名称);

            if (现有 >= 0)
            {
                _问题[现有] = 问题项;
            }
            else
            {
                _问题.Add(问题项);
            }
        }

        已变更?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>把加载结果登记到 <see cref="配置健康接口"/> 的便捷扩展。</summary>
public static class 配置健康扩展
{
    /// <summary>
    /// 按加载结果登记问题。<see cref="配置加载结果枚举.已加载"/> 与
    /// <see cref="配置加载结果枚举.缺失"/> 不登记 —— 前者正常，
    /// 后者是首次运行（缺文件时各加载器自有默认值，不算降级）。
    /// </summary>
    public static void 上报<T>(
        this 配置健康接口? 健康, string 显示名称值, 配置加载结果<T> 结果值)
        where T : class
    {
        if (健康 is null || !结果值.需要关注)
        {
            return;
        }

        健康.上报(new 配置问题(
            显示名称值,
            结果值.加载结果,
            结果值.错误信息 ?? 结果值.加载结果.ToString()));
    }

    /// <summary>登记一条与配置文件无关的降级（如启动期配置回退默认值）。</summary>
    public static void 上报回退(this 配置健康接口? 健康, string 显示名称值, string 问题详情)
    {
        健康?.上报(new 配置问题(显示名称值, 加载结果分类.失败, 问题详情));
    }
}
