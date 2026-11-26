namespace PlcHmi.Core.Models;

/// <summary>参数页的可配置表格类型。</summary>
public enum ParameterTableCategory
{
    /// <summary>延时表：动作等待时间，值存在 D 区。</summary>
    Delay = 0,

    /// <summary>按钮表：动作命令位，绑定 M 区。</summary>
    Button = 1,
}

/// <summary>
/// 参数页的一张自定义条目（延时 / 按钮共用）。
///
/// <para>管理员可增删、改名、启停；名称与地址都保存在
/// <c>config/param-tables.json</c>。</para>
/// </summary>
public sealed class ParameterEntryDefinition
{
    /// <summary>
    /// 一条延时在 D 区占用的字数。
    /// <para>延时按 <b>32 位整数（DINT）毫秒</b>下发，占 2 个连续寄存器（低字在前）；
    /// 校验层据此拒绝地址重叠（D1000 与 D1001 会互相吃掉高位字）。
    /// 按钮不占寄存器（M 区位），本常量只用于延时。</para>
    /// </summary>
    public const int delayOccupyWordCount = 2;

    /// <summary>唯一标识。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>显示名称（可自定义）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>PLC 地址。延时用 D 区，按钮用 M 区。</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// 绑定的变量表变量名（可选）。绑定后 <see cref="Address"/> 为该变量的软元件地址回填值，
    /// OPC UA 模式下按变量名做符号寻址。
    /// </summary>
    public string Variable { get; set; } = string.Empty;

    /// <summary>当前值（延时用，单位由 <see cref="Unit"/> 决定）。</summary>
    public double Value { get; set; }

    /// <summary>
    /// 延时单位。
    /// <para>配置与界面都按<b>秒</b>（显示格式 <c>XX.XXX S</c>）；
    /// 写入 PLC 的 D 区时换算成 <b>32 位整数毫秒（DINT，占 2 字，低字在前）</b>（×1000），
    /// 上限 2147483.647 S（≈24.8 天）。<b>2026-10-16 现场确认由 16 位改为 DINT</b>：
    /// 16 位毫秒只能到 65.535 S，几十秒的工位等待会直接溢出。</para>
    /// </summary>
    public string Unit { get; set; } = "s";

    /// <summary>取值范围下限。</summary>
    public double Min { get; set; }

    /// <summary>
    /// 取值范围上限。
    /// <para>默认按 32 位毫秒（DINT）上限取 2147483.647 秒，与写入 PLC 的 ×1000 换算一致。
    /// 改词条前先确认现场 PLC 侧该寄存器确实按 DINT 解析（占 2 字）。</para>
    /// </summary>
    public double Max { get; set; } = 2147483.647;

    /// <summary>是否启用。停用后不参与下发与显示。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>排序号。</summary>
    public int Order { get; set; }

    /// <summary>备注。</summary>
    public string? Comment { get; set; }

    /// <summary>深拷贝。</summary>
    public ParameterEntryDefinition Clone() => new()
    {
        Id = Id,
        Name = Name,
        Address = Address,
        Variable = Variable,
        Value = Value,
        Unit = Unit,
        Min = Min,
        Max = Max,
        Enabled = Enabled,
        Order = Order,
        Comment = Comment,
    };
}

/// <summary><c>config/param-tables.json</c> 的顶层结构。</summary>
public sealed class ParameterTableConfig
{
    /// <summary>延时表。</summary>
    public List<ParameterEntryDefinition> DelayCollection { get; set; } = new();

    /// <summary>按钮表。</summary>
    public List<ParameterEntryDefinition> ButtonCollection { get; set; } = new();
}

/// <summary>条目校验结果。</summary>
public sealed class ParameterEntryValidate
{
    /// <summary>是否通过。</summary>
    public bool IsValid { get; init; } = true;

    /// <summary>错误列表。</summary>
    public List<string> ErrorCollection { get; init; } = new();

    /// <summary>给操作员看的一句话。</summary>
    public string Summary => IsValid ? "校验通过" : string.Join("；", ErrorCollection);
}
