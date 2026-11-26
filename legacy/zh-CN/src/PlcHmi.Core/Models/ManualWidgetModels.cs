namespace 上位机.核心.模型;

using System.Text.Json.Serialization;

/// <summary>手动页元件类型。</summary>
public enum 手动元件类型
{
    /// <summary>气缸：伸出 / 缩回两个控制位。</summary>
    气缸 = 0,

    /// <summary>单动按钮：按下置位、松开复位。</summary>
    按钮 = 1,

    /// <summary>指示灯：只读显示一个位。</summary>
    指示灯 = 2,
}

/// <summary>
/// 手动页元件定义。对应 <c>config/manual-widgets.json</c> 的一条记录。
///
/// <para><b>地址硬约束：只允许绑定 M 区（辅助继电器）。</b>
/// 上位机只置位内部继电器，由 PLC 程序决定何时驱动 Y 输出——
/// 这样上位机不会绕过 PLC 的联锁逻辑直接动作执行机构。
/// 直接绑定 Y 点的配置会被拒绝保存。</para>
/// </summary>
public sealed class 手动元件定义
{
    /// <summary>唯一标识，用于增删改定位。</summary>
    public string 标识 { get; set; } = string.Empty;

    /// <summary>元件类型。</summary>
    public 手动元件类型 类型 { get; set; } = 手动元件类型.气缸;

    /// <summary>界面显示名称，如「上料气缸」。</summary>
    public string 名称 { get; set; } = string.Empty;

    /// <summary>排序号，小的排前面。</summary>
    public int 顺序 { get; set; }

    /// <summary>
    /// 控制位地址（M 区），如 <c>M220</c>。
    ///
    /// <para><b>气缸为单线圈控制：置位 = 伸出，复位 = 缩回。</b>
    /// 按钮为脉冲触发。指示灯不使用本字段。</para>
    /// </summary>
    public string 地址 { get; set; } = string.Empty;

    /// <summary>状态监测地址（M 区），指示灯使用。</summary>
    public string 状态地址 { get; set; } = string.Empty;

    /// <summary>
    /// 控制位绑定的变量表变量名（可选）。绑定后 <see cref="地址"/> 为该变量的软元件地址回填值，
    /// OPC UA 模式下按变量名做符号寻址。
    /// </summary>
    public string 变量 { get; set; } = string.Empty;

    /// <summary>状态监测位绑定的变量表变量名（可选，指示灯使用）。</summary>
    public string 状态值变量 { get; set; } = string.Empty;

    /// <summary>备注。</summary>
    public string? 注释 { get; set; }

    /// <summary>是否为气缸（需要两个可写地址）。</summary>
    [JsonIgnore]
    public bool 是否气缸 => 类型 == 手动元件类型.气缸;

    /// <summary>是否为按钮（需要一个可写地址）。</summary>
    [JsonIgnore]
    public bool 是否按钮 => 类型 == 手动元件类型.按钮;

    /// <summary>是否为指示灯（需要一个只读地址）。</summary>
    [JsonIgnore]
    public bool 是否指示灯 => 类型 == 手动元件类型.指示灯;

    /// <summary>深拷贝。</summary>
    public 手动元件定义 克隆() => new()
    {
        标识 = 标识,
        类型 = 类型,
        名称 = 名称,
        顺序 = 顺序,
        地址 = 地址,
        状态地址 = 状态地址,
        变量 = 变量,
        状态值变量 = 状态值变量,
        注释 = 注释,
    };
}

/// <summary><c>config/manual-widgets.json</c> 的顶层结构。</summary>
public sealed class 手动元件配置
{
    /// <summary>元件列表。</summary>
    public List<手动元件定义> 元件集合 { get; set; } = new();
}

/// <summary>元件配置校验结果。</summary>
public sealed class 手动元件校验
{
    /// <summary>是否通过校验。</summary>
    public bool 是否有效 { get; init; } = true;

    /// <summary>错误列表（为空表示通过）。</summary>
    public List<string> 错误集合 { get; init; } = new();

    /// <summary>给操作员看的一句话。</summary>
    public string 摘要 => 是否有效
        ? "校验通过"
        : string.Join("；", 错误集合);
}
