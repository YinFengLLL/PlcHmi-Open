using PlcHmi.Core.Enums;

namespace PlcHmi.Core.Abstractions;

/// <summary>
/// 运行监控页元件地址。
///
/// <para>显示名与标签名固定（代码按标签名读写），<b>只有地址可由管理员修改</b>；
/// 地址的单一真相是 <c>config/tags.json</c> 中的同名点位。</para>
/// </summary>
public sealed class RunMonitorComponent
{
    /// <summary>界面显示名，如「当前设备状态」。</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>对应的 tags.json 点位名，如 <c>RunStatus</c>。</summary>
    public string TagName { get; init; } = string.Empty;

    /// <summary>出厂默认地址（恢复默认参照，不随编辑变化）。</summary>
    public string DefaultAddress { get; init; } = string.Empty;

    /// <summary>当前地址（管理员可编辑）。</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>是否为命令类元件（true = 启停/清除等按钮，M 区脉冲写；false = 监控量，D 区读取）。</summary>
    public bool IsCommand { get; init; }

    /// <summary>
    /// 是否为 <b>M 区位元件</b>（只读状态位，如工位「锁附 / 点胶 / 加工完成」）。
    ///
    /// <para>与 <see cref="IsCommand"/> 的区别只在"谁写谁读"：命令是上位机<b>写</b> M 位，
    /// 位元件是上位机<b>读</b> M 位。两者的区域约束相同（都必须是 M 区），
    /// 但语义不同——所以单靠 <see cref="IsCommand"/> 一个布尔表达不了三种元件，
    /// 校验与提示也说不清"这一位到底是读还是写"。</para>
    /// </summary>
    public bool IsBitComponent { get; init; }

    /// <summary>给管理员看的地址约束说明。</summary>
    public string Remark { get; init; } = string.Empty;

    /// <summary>
    /// 出厂点位的数据类型与轮询分组。
    /// <para>用途只有一个：<b>老现场升级</b>时现场 <c>tags.json</c> 里没有这个点位，
    /// 保存地址时要按出厂定义把它补进去（类型 / 分组缺一不可，否则读回来的值不对或采集不刷新）。</para>
    /// </summary>
    public TagDataType DataType { get; init; } = TagDataType.UInt16;

    /// <inheritdoc cref="DataType" />
    public TagGroup Group { get; init; } = TagGroup.Fast;

    /// <summary>创建编辑用副本。</summary>
    public RunMonitorComponent Clone() => new()
    {
        DisplayName = DisplayName,
        TagName = TagName,
        DefaultAddress = DefaultAddress,
        Address = Address,
        IsCommand = IsCommand,
        IsBitComponent = IsBitComponent,
        Remark = Remark,
        DataType = DataType,
        Group = Group,
    };
}

/// <summary>运行监控页地址校验结果。</summary>
public sealed class RunMonitorAddressValidate
{
    /// <summary>是否全部通过。</summary>
    public bool IsValid { get; init; }

    /// <summary>全部错误（空集合表示通过）。</summary>
    public IReadOnlyList<string> ErrorCollection { get; init; } = [];

    /// <summary>错误汇总（可直接展示给管理员）。</summary>
    public string Summary => string.Join("\n", ErrorCollection);
}

/// <summary>
/// 运行监控页元件地址管理（仅管理员，<see cref="Permission.EditBindAddress"/>）。
///
/// <para><b>唯一真相</b>：地址存放在 <c>config/tags.json</c> 的同名点位
/// （RunStatus / CycleTime / OutputCount / TargetCount / GoodCount / BadCount /
/// ScrewDone / GlueDone / ProcessDone / ScrewCount /
/// CmdStart / CmdStop / CmdReset / CmdAlarmClear / CmdDataClear）。</para>
///
/// <para><b>校验一律在服务层</b>（界面只负责隐藏入口）：命令与位元件必须 M 区、
/// 其余监控量必须 D 区，且互不重叠、不与 tags.json 其它点位冲突、不落入报警位等系统保留区。</para>
///
/// <para><b>保存后立即生效</b>：更新点位定义 → 原子落盘 → 重建采集计划，无需重启程序。
/// 命令写入按点位名走 <c>标签写入器</c>（每次写都现解析地址），保存后即刻生效。</para>
///
/// <para><b>老现场升级：</b>现场 <c>tags.json</c> 不会被覆盖，新版新增的点位在现场是缺的——
/// 保存时按出厂定义（类型 / 访问 / 分组）补建，随整表落盘，管理员不必手工编辑 JSON。</para>
/// </summary>
public interface IRunMonitorAddressService
{
    /// <summary>当前元件地址快照（15 个元件）。界面编辑前应先 <see cref="RunMonitorComponent.Clone"/> 取副本。</summary>
    IReadOnlyList<RunMonitorComponent> ComponentCollection { get; }

    /// <summary>地址已被保存并生效（界面据此刷新提示文案等）。</summary>
    event EventHandler? AddressChanged;

    /// <summary>校验一组候选地址，返回全部错误（不修改任何状态）。</summary>
    RunMonitorAddressValidate Validate(IReadOnlyList<RunMonitorComponent> candidate);

    /// <summary>
    /// 保存地址改动：更新点位定义 → 原子落盘 → 重建采集计划。
    /// <para>无权限或校验不通过时抛异常，不会写入半个状态。</para>
    /// </summary>
    Task SaveAsync(IReadOnlyList<RunMonitorComponent> candidate, CancellationToken cancellationToken = default);

    /// <summary>按标签名取当前地址（供确认框等文案使用）；未知标签返回空串。</summary>
    string FindAddress(string TagName);
}
