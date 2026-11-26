namespace 上位机.核心.枚举;

/// <summary>
/// Modbus 数据区。决定使用哪一组功能码。
/// </summary>
public enum Modbus区域
{
    /// <summary>线圈（0x）。读 01 / 写单个 05 / 写多个 0F</summary>
    线圈 = 0,

    /// <summary>离散输入（1x）。只读，功能码 02</summary>
    离散输入 = 1,

    /// <summary>输入寄存器（3x）。只读，功能码 04</summary>
    输入寄存器 = 2,

    /// <summary>保持寄存器（4x）。读 03 / 写单个 06 / 写多个 10</summary>
    保持寄存器 = 3,
}
