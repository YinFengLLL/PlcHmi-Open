namespace PlcHmi.Core.Models;

using System.Text.Json.Serialization;

/// <summary>手动页元件类型。</summary>
public enum ManualComponentType
{
    /// <summary>气缸：伸出 / 缩回两个控制位。</summary>
    Cylinder = 0,

    /// <summary>单动按钮：按下置位、松开复位。</summary>
    Button = 1,

    /// <summary>指示灯：只读显示一个位。</summary>
    Indicator = 2,
}

/// <summary>
/// 手动页元件定义。对应 <c>config/manual-widgets.json</c> 的一条记录。
///
/// <para><b>地址硬约束：只允许绑定 M 区（辅助继电器）。</b>
/// 上位机只置位内部继电器，由 PLC 程序决定何时驱动 Y 输出——
/// 这样上位机不会绕过 PLC 的联锁逻辑直接动作执行机构。
/// 直接绑定 Y 点的配置会被拒绝保存。</para>
/// </summary>
public sealed class ManualComponentDefinition
{
    /// <summary>唯一标识，用于增删改定位。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>元件类型。</summary>
    public ManualComponentType Kind { get; set; } = ManualComponentType.Cylinder;

    /// <summary>界面显示名称，如「上料气缸」。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>排序号，小的排前面。</summary>
    public int Order { get; set; }

    /// <summary>
    /// 控制位地址（M 区），如 <c>M220</c>。
    ///
    /// <para><b>气缸为单线圈控制：置位 = 伸出，复位 = 缩回。</b>
    /// 按钮为脉冲触发。指示灯不使用本字段。</para>
    /// </summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>状态监测地址（M 区），指示灯使用。</summary>
    public string StateAddress { get; set; } = string.Empty;

    /// <summary>
    /// 控制位绑定的变量表变量名（可选）。绑定后 <see cref="Address"/> 为该变量的软元件地址回填值，
    /// OPC UA 模式下按变量名做符号寻址。
    /// </summary>
    public string Variable { get; set; } = string.Empty;

    /// <summary>状态监测位绑定的变量表变量名（可选，指示灯使用）。</summary>
    public string StateValueVariable { get; set; } = string.Empty;

    /// <summary>备注。</summary>
    public string? Comment { get; set; }

    /// <summary>是否为气缸（需要两个可写地址）。</summary>
    [JsonIgnore]
    public bool IsCylinder => Kind == ManualComponentType.Cylinder;

    /// <summary>是否为按钮（需要一个可写地址）。</summary>
    [JsonIgnore]
    public bool IsButton => Kind == ManualComponentType.Button;

    /// <summary>是否为指示灯（需要一个只读地址）。</summary>
    [JsonIgnore]
    public bool IsIndicator => Kind == ManualComponentType.Indicator;

    /// <summary>深拷贝。</summary>
    public ManualComponentDefinition Clone() => new()
    {
        Id = Id,
        Kind = Kind,
        Name = Name,
        Order = Order,
        Address = Address,
        StateAddress = StateAddress,
        Variable = Variable,
        StateValueVariable = StateValueVariable,
        Comment = Comment,
    };
}

/// <summary><c>config/manual-widgets.json</c> 的顶层结构。</summary>
public sealed class ManualComponentConfig
{
    /// <summary>元件列表。</summary>
    public List<ManualComponentDefinition> ComponentCollection { get; set; } = new();
}

/// <summary>元件配置校验结果。</summary>
public sealed class ManualComponentValidate
{
    /// <summary>是否通过校验。</summary>
    public bool IsValid { get; init; } = true;

    /// <summary>错误列表（为空表示通过）。</summary>
    public List<string> ErrorCollection { get; init; } = new();

    /// <summary>给操作员看的一句话。</summary>
    public string Summary => IsValid
        ? "校验通过"
        : string.Join("；", ErrorCollection);
}
