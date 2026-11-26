using System.Text.Json.Serialization;
using PlcHmi.Core.Enums;

namespace PlcHmi.Core.Models;

/// <summary>
/// 报警定义。对应 <c>config/alarms.json</c> 的一条记录。
///
/// <para><b>工作原理：</b>上位机周期性读取 <see cref="Address"/> 指定的 PLC 软元件，
/// 按 <see cref="Bit"/> 取出状态位——为 1（或整字非 0）时产生报警，恢复为 0 时自动消除。</para>
///
/// <para><b>可编辑：</b>管理员可在「报警记录」页修改报警文本与读取地址，
/// 不用改代码、不用重启。</para>
/// </summary>
public sealed class AlarmDefinition
{
    /// <summary>报警编号，如 <c>A001</c>。确认与历史记录按它定位。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>报警等级。</summary>
    public AlarmLevel Level { get; set; } = AlarmLevel.Critical;

    /// <summary>报警文本，显示在报警栏与记录中。</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>处理建议（可选），排查时给操作员看。</summary>
    public string? Suggestion { get; set; }

    /// <summary>监测的软元件地址，如 <c>M300</c>、<c>D2000</c>。</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// 取该地址的第几位。
    /// <para><c>-1</c> = 整字非 0 即报警；<c>0–15</c> = 只取该位。</para>
    /// </summary>
    public int Bit { get; set; } = -1;

    /// <summary>是否启用。停用后不参与扫描。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>是否为按位取值模式。</summary>
    [JsonIgnore]
    public bool IsBitMode => Bit is >= 0 and <= 15;

    /// <summary>
    /// 显示用地址。
    /// <para>D 区带位号（<c>D550.1</c>）；M 区本身就是位地址，不再缀位号（<c>M500</c>）。</para>
    /// </summary>
    [JsonIgnore]
    public string AddressText => IsBitMode ? $"{Address}.{Bit}" : Address;

    /// <summary>取位模式的显示文本。</summary>
    [JsonIgnore]
    public string BitText => IsBitMode ? $"第 {Bit} 位" : "整位";

    /// <summary>深拷贝。</summary>
    public AlarmDefinition Clone() => new()
    {
        Id = Id,
        Level = Level,
        Message = Message,
        Suggestion = Suggestion,
        Address = Address,
        Bit = Bit,
        Enabled = Enabled,
    };
}

/// <summary><c>config/alarms.json</c> 的顶层结构。</summary>
public sealed class AlarmConfig
{
    /// <summary>报警定义列表。</summary>
    public List<AlarmDefinition> AlarmCollection { get; set; } = new();
}

/// <summary>报警定义校验结果。</summary>
public sealed class AlarmDefinitionValidate
{
    /// <summary>是否通过。</summary>
    public bool IsValid { get; init; } = true;

    /// <summary>错误列表。</summary>
    public List<string> ErrorCollection { get; init; } = new();

    /// <summary>给操作员看的一句话。</summary>
    public string Summary => IsValid ? "校验通过" : string.Join("；", ErrorCollection);
}
