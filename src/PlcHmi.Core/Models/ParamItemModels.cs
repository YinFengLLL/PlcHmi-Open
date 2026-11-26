namespace 上位机.核心.模型;

/// <summary>参数页的可配置表格类型。</summary>
public enum 参数表格类别
{
    /// <summary>延时表：动作等待时间，值存在 D 区。</summary>
    延迟 = 0,

    /// <summary>按钮表：动作命令位，绑定 M 区。</summary>
    按钮 = 1,
}

/// <summary>
/// 参数页的一张自定义条目（延时 / 按钮共用）。
///
/// <para>管理员可增删、改名、启停；名称与地址都保存在
/// <c>config/param-tables.json</c>。</para>
/// </summary>
public sealed class 参数条目定义
{
    /// <summary>
    /// 一条延时在 D 区占用的字数。
    /// <para>延时按 <b>32 位整数（DINT）毫秒</b>下发，占 2 个连续寄存器（低字在前）；
    /// 校验层据此拒绝地址重叠（D1000 与 D1001 会互相吃掉高位字）。
    /// 按钮不占寄存器（M 区位），本常量只用于延时。</para>
    /// </summary>
    public const int 延时占字数 = 2;

    /// <summary>唯一标识。</summary>
    public string 标识 { get; set; } = string.Empty;

    /// <summary>显示名称（可自定义）。</summary>
    public string 名称 { get; set; } = string.Empty;

    /// <summary>PLC 地址。延时用 D 区，按钮用 M 区。</summary>
    public string 地址 { get; set; } = string.Empty;

    /// <summary>
    /// 绑定的变量表变量名（可选）。绑定后 <see cref="地址"/> 为该变量的软元件地址回填值，
    /// OPC UA 模式下按变量名做符号寻址。
    /// </summary>
    public string 变量 { get; set; } = string.Empty;

    /// <summary>当前值（延时用，单位由 <see cref="单元"/> 决定）。</summary>
    public double 值 { get; set; }

    /// <summary>
    /// 延时单位。
    /// <para>配置与界面都按<b>秒</b>（显示格式 <c>XX.XXX S</c>）；
    /// 写入 PLC 的 D 区时换算成 <b>32 位整数毫秒（DINT，占 2 字，低字在前）</b>（×1000），
    /// 上限 2147483.647 S（≈24.8 天）。<b>2026-10-16 现场确认由 16 位改为 DINT</b>：
    /// 16 位毫秒只能到 65.535 S，几十秒的工位等待会直接溢出。</para>
    /// </summary>
    public string 单元 { get; set; } = "s";

    /// <summary>取值范围下限。</summary>
    public double 最小 { get; set; }

    /// <summary>
    /// 取值范围上限。
    /// <para>默认按 32 位毫秒（DINT）上限取 2147483.647 秒，与写入 PLC 的 ×1000 换算一致。
    /// 改词条前先确认现场 PLC 侧该寄存器确实按 DINT 解析（占 2 字）。</para>
    /// </summary>
    public double 最大 { get; set; } = 2147483.647;

    /// <summary>是否启用。停用后不参与下发与显示。</summary>
    public bool 启用 { get; set; } = true;

    /// <summary>排序号。</summary>
    public int 顺序 { get; set; }

    /// <summary>备注。</summary>
    public string? 注释 { get; set; }

    /// <summary>深拷贝。</summary>
    public 参数条目定义 克隆() => new()
    {
        标识 = 标识,
        名称 = 名称,
        地址 = 地址,
        变量 = 变量,
        值 = 值,
        单元 = 单元,
        最小 = 最小,
        最大 = 最大,
        启用 = 启用,
        顺序 = 顺序,
        注释 = 注释,
    };
}

/// <summary><c>config/param-tables.json</c> 的顶层结构。</summary>
public sealed class 参数表格配置
{
    /// <summary>延时表。</summary>
    public List<参数条目定义> 延迟集合 { get; set; } = new();

    /// <summary>按钮表。</summary>
    public List<参数条目定义> 按钮集合 { get; set; } = new();
}

/// <summary>条目校验结果。</summary>
public sealed class 参数条目校验
{
    /// <summary>是否通过。</summary>
    public bool 是否有效 { get; init; } = true;

    /// <summary>错误列表。</summary>
    public List<string> 错误集合 { get; init; } = new();

    /// <summary>给操作员看的一句话。</summary>
    public string 摘要 => 是否有效 ? "校验通过" : string.Join("；", 错误集合);
}
