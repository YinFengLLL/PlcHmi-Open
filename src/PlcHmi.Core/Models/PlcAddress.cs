using PlcHmi.Core.Enums;

namespace PlcHmi.Core.Models;

/// <summary>
/// 解析后的 PLC 地址。由地址解析器把软元件符号（如 <c>X10</c>、<c>D100</c>）换算为 Modbus 寻址信息。
/// </summary>
public sealed record PlcAddress
{
    /// <summary>软元件类型。</summary>
    public required SoftElement ElementItem { get; init; }

    /// <summary>
    /// 软元件逻辑序号。<b>X / Y 已按八进制解析</b>，因此 <c>X10</c> 的 LogicalIndex 为 8 而非 10。
    /// </summary>
    public required int LogicIndex { get; init; }

    /// <summary>换算后的 Modbus 起始地址。</summary>
    public required int ModbusAddress { get; init; }

    /// <summary>所属 Modbus 数据区，决定使用哪组功能码。</summary>
    public required ModbusArea Area { get; init; }

    /// <summary>占用的寄存器数量：位元件与 16 位为 1，32 位为 2。</summary>
    public int RegisterCount { get; init; } = 1;

    /// <summary>
    /// 位偏移。
    /// <para>两种形态使用：中型 PLC（AM600）的 <c>QX0.3</c> / <c>IX0.3</c> 位寻址，
    /// 以及小型 PLC 的 D/R 区<b>字内位</b>（如 <c>D1210.2</c>，占 D 字的第 2 位，0–15）。
    /// 其余为 -1。</para>
    /// </summary>
    public int BitIndex { get; init; } = -1;

    /// <summary>
    /// 是否为 D/R 区「字内位」地址（小型 PLC 的 <c>D1210.2</c> 形式）：
    /// 区域是保持寄存器但 <see cref="BitIndex"/> 生效，读写都需按「整字 → 取/改位」处理。
    /// </summary>
    public bool IsIntraWordBit => Area == ModbusArea.HoldingRegister && BitIndex >= 0;

    /// <summary>
    /// 功能码覆盖。中型 PLC 的 SM / SD 需使用汇川专用功能码（0x31/0x33 等），其余为 null 表示走标准功能码。
    /// </summary>
    public byte? FunctionCodeOverwrite { get; init; }

    /// <summary>原始书写形式，便于日志与错误提示，如 <c>"X10"</c>。</summary>
    public string Source { get; init; } = string.Empty;

    public override string ToString() =>
        $"{Source} → {Area}@{ModbusAddress} (index={LogicIndex}, regs={RegisterCount})";
}
