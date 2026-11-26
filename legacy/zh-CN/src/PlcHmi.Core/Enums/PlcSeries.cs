namespace 上位机.核心.枚举;

/// <summary>
/// PLC 机型系列。不同系列使用完全不同的地址体系，寻址前必须先确定系列。
/// </summary>
public enum PLC系列
{
    /// <summary>
    /// 小型 PLC：EASY 系列（EASY320 / EASY52X）、H5U、H3U。
    /// 使用 M / B / S / X / Y 位元件与 D / R 字元件，X、Y 按八进制编号。
    /// </summary>
    小型PLC = 0,

    /// <summary>
    /// 中型 PLC：AM600 / AC800（CODESYS 体系）。
    /// 使用 QX / IX / MW 寻址，软元件含义与小型 PLC 完全不同。
    /// </summary>
    中等PLC = 1,
}
