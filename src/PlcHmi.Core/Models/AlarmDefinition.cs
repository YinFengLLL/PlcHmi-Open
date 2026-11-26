using System.Text.Json.Serialization;
using 上位机.核心.枚举;

namespace 上位机.核心.模型;

/// <summary>
/// 报警定义。对应 <c>config/alarms.json</c> 的一条记录。
///
/// <para><b>工作原理：</b>上位机周期性读取 <see cref="地址"/> 指定的 PLC 软元件，
/// 按 <see cref="位"/> 取出状态位——为 1（或整字非 0）时产生报警，恢复为 0 时自动消除。</para>
///
/// <para><b>可编辑：</b>管理员可在「报警记录」页修改报警文本与读取地址，
/// 不用改代码、不用重启。</para>
/// </summary>
public sealed class 报警定义
{
    /// <summary>报警编号，如 <c>A001</c>。确认与历史记录按它定位。</summary>
    public string 标识 { get; set; } = string.Empty;

    /// <summary>报警等级。</summary>
    public 报警等级 等级 { get; set; } = 报警等级.紧急;

    /// <summary>报警文本，显示在报警栏与记录中。</summary>
    public string 消息 { get; set; } = string.Empty;

    /// <summary>处理建议（可选），排查时给操作员看。</summary>
    public string? 建议 { get; set; }

    /// <summary>监测的软元件地址，如 <c>M300</c>、<c>D2000</c>。</summary>
    public string 地址 { get; set; } = string.Empty;

    /// <summary>
    /// 取该地址的第几位。
    /// <para><c>-1</c> = 整字非 0 即报警；<c>0–15</c> = 只取该位。</para>
    /// </summary>
    public int 位 { get; set; } = -1;

    /// <summary>是否启用。停用后不参与扫描。</summary>
    public bool 启用 { get; set; } = true;

    /// <summary>是否为按位取值模式。</summary>
    [JsonIgnore]
    public bool 是否位模式 => 位 is >= 0 and <= 15;

    /// <summary>
    /// 显示用地址。
    /// <para>D 区带位号（<c>D550.1</c>）；M 区本身就是位地址，不再缀位号（<c>M500</c>）。</para>
    /// </summary>
    [JsonIgnore]
    public string 地址文本 => 是否位模式 ? $"{地址}.{位}" : 地址;

    /// <summary>取位模式的显示文本。</summary>
    [JsonIgnore]
    public string 位文本 => 是否位模式 ? $"第 {位} 位" : "整位";

    /// <summary>深拷贝。</summary>
    public 报警定义 克隆() => new()
    {
        标识 = 标识,
        等级 = 等级,
        消息 = 消息,
        建议 = 建议,
        地址 = 地址,
        位 = 位,
        启用 = 启用,
    };
}

/// <summary><c>config/alarms.json</c> 的顶层结构。</summary>
public sealed class 报警配置
{
    /// <summary>报警定义列表。</summary>
    public List<报警定义> 报警集合 { get; set; } = new();
}

/// <summary>报警定义校验结果。</summary>
public sealed class 报警定义校验
{
    /// <summary>是否通过。</summary>
    public bool 是否有效 { get; init; } = true;

    /// <summary>错误列表。</summary>
    public List<string> 错误集合 { get; init; } = new();

    /// <summary>给操作员看的一句话。</summary>
    public string 摘要 => 是否有效 ? "校验通过" : string.Join("；", 错误集合);
}
