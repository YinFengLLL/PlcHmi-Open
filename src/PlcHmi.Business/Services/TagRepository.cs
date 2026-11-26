using System.Text.Json;
using Microsoft.Extensions.Logging;
using PlcHmi.Business.Configuration;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// 业务点位仓库。从 <c>config/tags.json</c> 加载点位定义。
/// <para>点位全部配置化，换设备只改 JSON，不动代码。</para>
/// </summary>
public sealed class TagRepository : ITagRepository
{
    private readonly ILogger<TagRepository> _log;
    private readonly IConfigHealth? _health;

    /// <summary>
    /// 点位列表。volatile 引用<b>整体替换</b>：此前重载是原地 Clear()+AddRange，
    /// 与其它线程对 <see cref="All"/> 的枚举并发会抛 Collection was modified。
    /// </summary>
    private volatile List<TagDefinition> _tag = new();

    public TagRepository(ILogger<TagRepository> Log, IConfigHealth? Health = null)
    {
        _log = Log;
        _health = Health;
        Load();
    }

    /// <inheritdoc />
    public IReadOnlyList<TagDefinition> All => _tag;

    /// <inheritdoc />
    public TagDefinition? Find(string nameValue)
    {
        var list = _tag;
        return list.FirstOrDefault(tagEntry => string.Equals(tagEntry.Name, nameValue, StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public IReadOnlyList<TagDefinition> GetGroup(TagGroup group)
    {
        var list = _tag;
        return list.Where(tagEntry => tagEntry.Group == group).ToList();
    }

    /// <inheritdoc />
    public Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        Load();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public bool TryUpdateAddress(string nameValue, string addressValue)
    {
        var tagItem = Find(nameValue);

        if (tagItem is null)
        {
            return false;
        }

        tagItem.Address = addressValue;
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 只在内存里加：调用方改完地址会走 <see cref="SaveAsync"/> 整表落盘，
    /// 新点位随那次保存一起进现场 <c>tags.json</c>。
    /// </remarks>
    public bool TryAdd(TagDefinition DefinitionItem)
    {
        if (Find(DefinitionItem.Name) is not null)
        {
            return false;
        }

        _tag.Add(DefinitionItem);
        _log.LogInformation("点位表补建缺失点位 {Name}（{Address}）", DefinitionItem.Name, DefinitionItem.Address);
        return true;
    }

    /// <inheritdoc />
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        var Document = new TagFile { TagCollection = _tag.ToList() };

        try
        {
            // 用 camelCase 口径写：tags.json 是人工手写的，别让程序把风格翻成 PascalCase
            var jsonText = JsonSerializer.Serialize(Document, JsonConfig.writeHumanStyle);
            await AtomicFile.WriteAllTextAsync(ConfigPath.TagCollection, jsonText, cancellationToken).ConfigureAwait(false);

            _log.LogInformation("点位表已保存：{Count} 个业务点位 → {Path}", _tag.Count, ConfigPath.TagCollection);
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "保存点位表失败：{Path}", ConfigPath.TagCollection);
            throw;
        }
    }

    private void Load()
    {
        var resultValue = ConfigFileLoader.LoadJson<TagFile>(ConfigPath.TagCollection, JsonConfig.Read, _log);

        _health.Report("业务点位表", resultValue);

        // 构建新列表后整体替换（volatile 引用发布）：重载与枚举并发时，
        // 读者看到的要么是完整的旧表、要么是完整的新表
        var NewList = new List<TagDefinition>();

        if (resultValue.Value?.TagCollection is not null)
        {
            NewList.AddRange(resultValue.Value.TagCollection.Where(tagEntry => !string.IsNullOrWhiteSpace(tagEntry.Name)));
        }

        _tag = NewList;

        if (resultValue.loadResult is LoadResultSplitCategory.Loaded)
        {
            _log.LogInformation("已加载 {Count} 个业务点位，来自 {Path}", _tag.Count, ConfigPath.TagCollection);
        }
    }

    /// <summary>与 tags.json 的顶层结构对应。</summary>
    private sealed class TagFile
    {
        public List<TagDefinition>? TagCollection { get; set; }
    }
}
