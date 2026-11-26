using 上位机.核心.枚举;

namespace 上位机.通信.汇川;

/// <summary>
/// 软元件寻址规格。
/// <para>汇川 PLC 的 Modbus 地址 = <see cref="基址地址"/> + 软元件序号，
/// 序号按 <see cref="编号进制"/> 指定的进制解析（X / Y 为八进制）。</para>
/// </summary>
/// <param name="Element">软元件类型。</param>
/// <param name="Area">所属 Modbus 数据区。</param>
/// <param name="BaseAddress">该区在 Modbus 地址空间中的起始地址。</param>
/// <param name="NumberingRadix">编号进制：8 表示八进制（X/Y），10 表示十进制。</param>
/// <param name="MaxIndex">允许的最大序号（已按十进制表示）。</param>
/// <param name="ReadFunctionCode">专用读功能码（中型 PLC 的 SM/SD 使用），null 表示走标准功能码。</param>
/// <param name="WriteSingleFunctionCode">专用写单个功能码。</param>
/// <param name="WriteMultipleFunctionCode">专用写多个功能码。</param>
internal sealed record 软元素规格(
    软元素 元素,
    Modbus区域 区域,
    int 基址地址,
    int 编号进制,
    int 最大序号,
    byte? 读取功能代码 = null,
    byte? 写入单个功能代码 = null,
    byte? 写入多个功能代码 = null);
