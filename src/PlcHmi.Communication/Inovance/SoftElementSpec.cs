using PlcHmi.Core.Enums;

namespace PlcHmi.Communication.Inovance;

/// <summary>
/// 软元件寻址规格。
/// <para>汇川 PLC 的 Modbus 地址 = <see cref="baseAddressValue"/> + 软元件序号，
/// 序号按 <see cref="numberRadix"/> 指定的进制解析（X / Y 为八进制）。</para>
/// </summary>
/// <param name="Element">软元件类型。</param>
/// <param name="Area">所属 Modbus 数据区。</param>
/// <param name="BaseAddress">该区在 Modbus 地址空间中的起始地址。</param>
/// <param name="NumberingRadix">编号进制：8 表示八进制（X/Y），10 表示十进制。</param>
/// <param name="MaxIndex">允许的最大序号（已按十进制表示）。</param>
/// <param name="ReadFunctionCode">专用读功能码（中型 PLC 的 SM/SD 使用），null 表示走标准功能码。</param>
/// <param name="WriteSingleFunctionCode">专用写单个功能码。</param>
/// <param name="WriteMultipleFunctionCode">专用写多个功能码。</param>
internal sealed record SoftElementSpec(
    SoftElement ElementItem,
    ModbusArea Area,
    int baseAddressValue,
    int numberRadix,
    int maxIndex,
    byte? readFunctionCanCode = null,
    byte? writeSingleFunctionCanCode = null,
    byte? writeMultiFunctionCanCode = null);
