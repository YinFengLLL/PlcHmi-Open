namespace PlcHmi.Core.Models;

/// <summary>机种与示教控制寄存器配置。</summary>
public sealed class RecipeWidgetConfig
{
    /// <summary>当前机种号寄存器。</summary>
    public int RecipeNumberAddress { get; set; } = 3330;

    /// <summary>机种总数寄存器（只读）。</summary>
    public int RecipeCountAddress { get; set; } = 3331;

    /// <summary>示教点索引寄存器（0–99）。</summary>
    public int TeachIndexAddress { get; set; } = 3332;

    /// <summary>当前面号寄存器（0–9）。</summary>
    public int TeachFaceAddress { get; set; } = 3333;

    // ── 变量表绑定（可选：填写后 OPC UA 模式按变量名符号寻址）──────────

    /// <summary>机种号寄存器绑定的变量名。</summary>
    public string? RecipeNumberVariable { get; set; }

    /// <summary>机种总数寄存器绑定的变量名。</summary>
    public string? RecipeCountVariable { get; set; }

    /// <summary>示教点索引寄存器绑定的变量名。</summary>
    public string? TeachIndexVariable { get; set; }

    /// <summary>当前面号寄存器绑定的变量名。</summary>
    public string? TeachFaceVariable { get; set; }
}

/// <summary>
/// 地址允许范围。
///
/// <para>用于约束管理员可编辑的地址区间——例如<b>轴参数地址</b>只允许落在 D1200 – D1799，
/// 避免误填到别的设备的控制区。</para>
///
/// <para><b>地址分区（2026-09-17 按 PLC 地址分配表更新）</b>：D1000–D1099 为延时表专属区，
/// D1100–D1199 为上位机预留段，D1200–D1799 为轴 1–轴 6 参数区（每轴 100 字），
/// D2500–D3499 为默认机种配方区，D3500–D3599 为气缸区。
/// 本区间只约束轴参数类地址（定位速度、点动档位等），不约束延时表与示教点表。</para>
/// </summary>
public sealed class AddressRange
{
    /// <summary>下限（含）。</summary>
    public int Min { get; set; }

    /// <summary>上限（含）。</summary>
    public int Max { get; set; }

    /// <summary>地址是否在允许范围内。</summary>
    public bool Contains(int addressValue) => addressValue >= Min && addressValue <= Max;

    /// <summary>范围提示文本，如 <c>D1200 – D1799</c>。</summary>
    public string Text => $"D{Min} – D{Max}";
}
