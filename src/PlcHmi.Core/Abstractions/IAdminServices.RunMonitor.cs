using 上位机.核心.枚举;

namespace 上位机.核心.抽象;

/// <summary>
/// 运行监控页元件地址。
///
/// <para>显示名与标签名固定（代码按标签名读写），<b>只有地址可由管理员修改</b>；
/// 地址的单一真相是 <c>config/tags.json</c> 中的同名点位。</para>
/// </summary>
public sealed class 运行监控元件
{
    /// <summary>界面显示名，如「当前设备状态」。</summary>
    public string 显示名称 { get; init; } = string.Empty;

    /// <summary>对应的 tags.json 点位名，如 <c>RunStatus</c>。</summary>
    public string 标签名 { get; init; } = string.Empty;

    /// <summary>出厂默认地址（恢复默认参照，不随编辑变化）。</summary>
    public string 默认地址 { get; init; } = string.Empty;

    /// <summary>当前地址（管理员可编辑）。</summary>
    public string 地址 { get; set; } = string.Empty;

    /// <summary>是否为命令类元件（true = 启停/清除等按钮，M 区脉冲写；false = 监控量，D 区读取）。</summary>
    public bool 是命令 { get; init; }

    /// <summary>
    /// 是否为 <b>M 区位元件</b>（只读状态位，如工位「锁附 / 点胶 / 加工完成」）。
    ///
    /// <para>与 <see cref="是命令"/> 的区别只在"谁写谁读"：命令是上位机<b>写</b> M 位，
    /// 位元件是上位机<b>读</b> M 位。两者的区域约束相同（都必须是 M 区），
    /// 但语义不同——所以单靠 <see cref="是命令"/> 一个布尔表达不了三种元件，
    /// 校验与提示也说不清"这一位到底是读还是写"。</para>
    /// </summary>
    public bool 是位元件 { get; init; }

    /// <summary>给管理员看的地址约束说明。</summary>
    public string 说明 { get; init; } = string.Empty;

    /// <summary>
    /// 出厂点位的数据类型与轮询分组。
    /// <para>用途只有一个：<b>老现场升级</b>时现场 <c>tags.json</c> 里没有这个点位，
    /// 保存地址时要按出厂定义把它补进去（类型 / 分组缺一不可，否则读回来的值不对或采集不刷新）。</para>
    /// </summary>
    public 标签数据类型 数据类型 { get; init; } = 标签数据类型.UInt16;

    /// <inheritdoc cref="数据类型" />
    public 标签分组 分组 { get; init; } = 标签分组.快;

    /// <summary>创建编辑用副本。</summary>
    public 运行监控元件 克隆() => new()
    {
        显示名称 = 显示名称,
        标签名 = 标签名,
        默认地址 = 默认地址,
        地址 = 地址,
        是命令 = 是命令,
        是位元件 = 是位元件,
        说明 = 说明,
        数据类型 = 数据类型,
        分组 = 分组,
    };
}

/// <summary>运行监控页地址校验结果。</summary>
public sealed class 运行监控地址校验
{
    /// <summary>是否全部通过。</summary>
    public bool 是否有效 { get; init; }

    /// <summary>全部错误（空集合表示通过）。</summary>
    public IReadOnlyList<string> 错误集合 { get; init; } = [];

    /// <summary>错误汇总（可直接展示给管理员）。</summary>
    public string 摘要 => string.Join("\n", 错误集合);
}

/// <summary>
/// 运行监控页元件地址管理（仅管理员，<see cref="权限.编辑绑定地址"/>）。
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
public interface 运行监控地址服务接口
{
    /// <summary>当前元件地址快照（15 个元件）。界面编辑前应先 <see cref="运行监控元件.克隆"/> 取副本。</summary>
    IReadOnlyList<运行监控元件> 元件集合 { get; }

    /// <summary>地址已被保存并生效（界面据此刷新提示文案等）。</summary>
    event EventHandler? 地址已变更;

    /// <summary>校验一组候选地址，返回全部错误（不修改任何状态）。</summary>
    运行监控地址校验 校验(IReadOnlyList<运行监控元件> 候选);

    /// <summary>
    /// 保存地址改动：更新点位定义 → 原子落盘 → 重建采集计划。
    /// <para>无权限或校验不通过时抛异常，不会写入半个状态。</para>
    /// </summary>
    Task 异步保存(IReadOnlyList<运行监控元件> 候选, CancellationToken 取消令牌 = default);

    /// <summary>按标签名取当前地址（供确认框等文案使用）；未知标签返回空串。</summary>
    string 查找地址(string 标签名);
}
