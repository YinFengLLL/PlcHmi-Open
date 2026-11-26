using System.Text.Json;
using Microsoft.Extensions.Logging;
using 上位机.业务.配置;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// 业务点位仓库。从 <c>config/tags.json</c> 加载点位定义。
/// <para>点位全部配置化，换设备只改 JSON，不动代码。</para>
/// </summary>
public sealed class 标签仓储 : 标签仓储接口
{
    private readonly ILogger<标签仓储> _日志;
    private readonly 配置健康接口? _健康;

    /// <summary>
    /// 点位列表。volatile 引用<b>整体替换</b>：此前重载是原地 Clear()+AddRange，
    /// 与其它线程对 <see cref="全部"/> 的枚举并发会抛 Collection was modified。
    /// </summary>
    private volatile List<标签定义> _标签 = new();

    public 标签仓储(ILogger<标签仓储> 日志, 配置健康接口? 健康 = null)
    {
        _日志 = 日志;
        _健康 = 健康;
        加载();
    }

    /// <inheritdoc />
    public IReadOnlyList<标签定义> 全部 => _标签;

    /// <inheritdoc />
    public 标签定义? 查找(string 名称值)
    {
        var 列表 = _标签;
        return 列表.FirstOrDefault(标签条目 => string.Equals(标签条目.名称, 名称值, StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public IReadOnlyList<标签定义> 获取分组(标签分组 group)
    {
        var 列表 = _标签;
        return 列表.Where(标签条目 => 标签条目.分组 == group).ToList();
    }

    /// <inheritdoc />
    public Task 异步重载(CancellationToken 取消令牌 = default)
    {
        加载();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public bool 尝试更新地址(string 名称值, string 地址值)
    {
        var 标签项 = 查找(名称值);

        if (标签项 is null)
        {
            return false;
        }

        标签项.地址 = 地址值;
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 只在内存里加：调用方改完地址会走 <see cref="异步保存"/> 整表落盘，
    /// 新点位随那次保存一起进现场 <c>tags.json</c>。
    /// </remarks>
    public bool 尝试新增(标签定义 定义项)
    {
        if (查找(定义项.名称) is not null)
        {
            return false;
        }

        _标签.Add(定义项);
        _日志.LogInformation("点位表补建缺失点位 {Name}（{Address}）", 定义项.名称, 定义项.地址);
        return true;
    }

    /// <inheritdoc />
    public async Task 异步保存(CancellationToken 取消令牌 = default)
    {
        var 文档 = new 标签文件 { 标签集合 = _标签.ToList() };

        try
        {
            // 用 camelCase 口径写：tags.json 是人工手写的，别让程序把风格翻成 PascalCase
            var JSON文本 = JsonSerializer.Serialize(文档, JSON配置.写入人工风格);
            await 原子文件.异步写入全部文本(配置路径.标签集合, JSON文本, 取消令牌).ConfigureAwait(false);

            _日志.LogInformation("点位表已保存：{Count} 个业务点位 → {Path}", _标签.Count, 配置路径.标签集合);
        }
        catch (Exception 异常)
        {
            _日志.LogError(异常, "保存点位表失败：{Path}", 配置路径.标签集合);
            throw;
        }
    }

    private void 加载()
    {
        var 结果值 = 配置文件加载器.加载JSON<标签文件>(配置路径.标签集合, JSON配置.读取, _日志);

        _健康.上报("业务点位表", 结果值);

        // 构建新列表后整体替换（volatile 引用发布）：重载与枚举并发时，
        // 读者看到的要么是完整的旧表、要么是完整的新表
        var 新列表 = new List<标签定义>();

        if (结果值.值?.标签集合 is not null)
        {
            新列表.AddRange(结果值.值.标签集合.Where(标签条目 => !string.IsNullOrWhiteSpace(标签条目.名称)));
        }

        _标签 = 新列表;

        if (结果值.加载结果 is 加载结果分类.已加载)
        {
            _日志.LogInformation("已加载 {Count} 个业务点位，来自 {Path}", _标签.Count, 配置路径.标签集合);
        }
    }

    /// <summary>与 tags.json 的顶层结构对应。</summary>
    private sealed class 标签文件
    {
        public List<标签定义>? 标签集合 { get; set; }
    }
}
