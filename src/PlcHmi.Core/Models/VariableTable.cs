using 上位机.核心.枚举;

namespace 上位机.核心.模型;

/// <summary>
/// 汇川变量表条目。对应变量表 CSV（或 <c>config/variables.json</c>）的一行。
///
/// <para><b>变量名是完整书写路径</b>，如 <c>X轴命令.b_轴使能</c>、<c>X轴参数.r_定位速度</c>——
/// 它同时是 OPC UA 模式下的符号寻址依据（NodeId 标识符）。</para>
/// </summary>
public sealed class 变量表条目
{
    /// <summary>变量完整路径名，如 <c>X轴命令.b_轴使能</c>。</summary>
    public string 变量名 { get; set; } = string.Empty;

    /// <summary>软元件地址书写形式，如 <c>D1200</c>、<c>M220</c>、<c>D1210.0</c>。</summary>
    public string 软元件地址 { get; set; } = string.Empty;

    /// <summary>数据类型（由 CSV 类型映射而来，如 REAL → Float32）。</summary>
    public 标签数据类型 数据类型 { get; set; } = 标签数据类型.UInt16;

    /// <summary>注释（来自 CSV 注释列）。</summary>
    public string? 注释 { get; set; }
}

/// <summary>
/// <c>config/variables.json</c> 的顶层结构——导入汇川变量表后的常驻内存真相。
/// </summary>
public sealed class 变量表文档
{
    /// <summary>变量条目列表（按导入顺序）。</summary>
    public List<变量表条目> 条目集合 { get; set; } = new();

    /// <summary>按变量名查找（大小写不敏感）。</summary>
    public 变量表条目? 查找(string? 变量名) =>
        string.IsNullOrWhiteSpace(变量名) ? null
            : 条目集合.FirstOrDefault(条目 => string.Equals(条目.变量名, 变量名.Trim(), StringComparison.OrdinalIgnoreCase));
}
