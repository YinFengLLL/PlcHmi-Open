using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Tests.Fake;

/// <summary>
/// 点位仓储的测试替身：内存字典承载点位定义，记录地址更新与保存次数。
///
/// <para><b>为什么需要它</b>：运行页地址保存链路是「更新点位 → 落盘 → 重建采集计划」，
/// 真仓储会读写 <c>config/tags.json</c>，测试不能碰真实配置文件；
/// 用替身即可断言"保存时确实更新了点位并要求落盘"。</para>
/// </summary>
public sealed class FakeTagRepository : ITagRepository
{
    private readonly Dictionary<string, TagDefinition> _tag = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>异步保存被调用的次数。</summary>
    public int saveCount;

    /// <summary>被补建的点位名称（用于断言"老现场缺点位时确实补了"）。</summary>
    public List<string> BuildMissingRecord { get; } = [];

    public FakeTagRepository(params TagDefinition[] initialPoint)
    {
        foreach (var EntryItem in initialPoint)
        {
            _tag[EntryItem.Name] = EntryItem;
        }
    }

    /// <summary>按 tags.json 的默认结构构建一份替身（运行页 15 个点位）。</summary>
    public static FakeTagRepository RunPageDefaultPoint() => new(
        new TagDefinition { Name = "RunStatus", Address = "D6", DataType = TagDataType.UInt16, AccessQuery = TagAccessQuery.Read, Group = TagGroup.Fast },
        new TagDefinition { Name = "CycleTime", Address = "D1100", DataType = TagDataType.UInt32, AccessQuery = TagAccessQuery.Read, Group = TagGroup.Fast },
        new TagDefinition { Name = "OutputCount", Address = "D1102", DataType = TagDataType.UInt32, AccessQuery = TagAccessQuery.Read, Group = TagGroup.Slow },
        new TagDefinition { Name = "TargetCount", Address = "D1104", DataType = TagDataType.UInt32, AccessQuery = TagAccessQuery.Read, Group = TagGroup.Slow },
        new TagDefinition { Name = "GoodCount", Address = "D1106", DataType = TagDataType.UInt32, AccessQuery = TagAccessQuery.Read, Group = TagGroup.Slow },
        new TagDefinition { Name = "BadCount", Address = "D1108", DataType = TagDataType.UInt32, AccessQuery = TagAccessQuery.Read, Group = TagGroup.Slow },
        new TagDefinition { Name = "ScrewDone", Address = "M490", DataType = TagDataType.Boolean, AccessQuery = TagAccessQuery.Read, Group = TagGroup.Fast },
        new TagDefinition { Name = "GlueDone", Address = "M491", DataType = TagDataType.Boolean, AccessQuery = TagAccessQuery.Read, Group = TagGroup.Fast },
        new TagDefinition { Name = "ProcessDone", Address = "M492", DataType = TagDataType.Boolean, AccessQuery = TagAccessQuery.Read, Group = TagGroup.Fast },
        new TagDefinition { Name = "ScrewCount", Address = "D1116", DataType = TagDataType.Int16, AccessQuery = TagAccessQuery.Read, Group = TagGroup.Fast },
        new TagDefinition { Name = "CmdStart", Address = "M200", DataType = TagDataType.Boolean, AccessQuery = TagAccessQuery.Write, Group = TagGroup.Command },
        new TagDefinition { Name = "CmdStop", Address = "M201", DataType = TagDataType.Boolean, AccessQuery = TagAccessQuery.Write, Group = TagGroup.Command },
        new TagDefinition { Name = "CmdReset", Address = "M202", DataType = TagDataType.Boolean, AccessQuery = TagAccessQuery.Write, Group = TagGroup.Command },
        new TagDefinition { Name = "CmdDataClear", Address = "M1000", DataType = TagDataType.Boolean, AccessQuery = TagAccessQuery.Write, Group = TagGroup.Command },
        new TagDefinition { Name = "CmdAlarmClear", Address = "M203", DataType = TagDataType.Boolean, AccessQuery = TagAccessQuery.Write, Group = TagGroup.Command },
        // 干扰项：用于校验"与其它点位冲突"
        new TagDefinition { Name = "AxisXPosition", Address = "D1221", DataType = TagDataType.Float32, AccessQuery = TagAccessQuery.Read, Group = TagGroup.Fast });

    /// <inheritdoc />
    public IReadOnlyList<TagDefinition> All => _tag.Values.ToList();

    /// <inheritdoc />
    public TagDefinition? Find(string nameValue) =>
        _tag.TryGetValue(nameValue, out var EntryItem) ? EntryItem : null;

    /// <inheritdoc />
    public IReadOnlyList<TagDefinition> GetGroup(TagGroup group) =>
        _tag.Values.Where(tagEntry => tagEntry.Group == group).ToList();

    /// <inheritdoc />
    public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public bool TryAdd(TagDefinition DefinitionItem)
    {
        if (_tag.ContainsKey(DefinitionItem.Name))
        {
            return false;
        }

        _tag[DefinitionItem.Name] = DefinitionItem;
        BuildMissingRecord.Add(DefinitionItem.Name);
        return true;
    }

    /// <inheritdoc />
    public bool TryUpdateAddress(string nameValue, string addressValue)
    {
        if (!_tag.TryGetValue(nameValue, out var EntryItem))
        {
            return false;
        }

        EntryItem.Address = addressValue;
        return true;
    }

    /// <inheritdoc />
    public Task SaveAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref saveCount);
        return Task.CompletedTask;
    }
}
