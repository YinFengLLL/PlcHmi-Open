namespace 上位机.核心.枚举;

/// <summary>
/// 软元件类型。涵盖小型 PLC（EASY/H5U/H3U）与中型 PLC（AM600/AC800）两套体系。
/// </summary>
public enum 软元素
{
    /// <summary>输入继电器（小型 PLC），八进制编号。EASY320 / EASY52X / H5U / H3U</summary>
    X = 0,

    /// <summary>输出继电器（小型 PLC），八进制编号。</summary>
    Y = 1,

    /// <summary>辅助继电器（小型 PLC），十进制编号。</summary>
    M = 2,

    /// <summary>链接继电器（小型 PLC），十进制编号。</summary>
    B = 3,

    /// <summary>状态继电器（小型 PLC），十进制编号。</summary>
    S = 4,

    /// <summary>数据寄存器（小型 PLC），16 位，十进制编号。</summary>
    D = 5,

    /// <summary>文件寄存器（小型 PLC），16 位，十进制编号。</summary>
    R = 6,

    /// <summary>特殊辅助继电器，十进制编号。两种机型均存在，但地址基准不同。</summary>
    特殊寄存器 = 7,

    /// <summary>特殊数据寄存器，十进制编号。两种机型均存在，但地址基准与功能码不同。</summary>
    系统寄存器 = 8,

    /// <summary>定时器触点（小型 PLC），十进制编号。</summary>
    T = 9,

    /// <summary>计数器触点（小型 PLC），十进制编号。</summary>
    C = 10,

    /// <summary>输出线圈（中型 PLC / AM600），按 <c>QX字节.位</c> 寻址。</summary>
    Q = 20,

    /// <summary>输入接点（中型 PLC / AM600），按 <c>IX字节.位</c> 寻址。</summary>
    接口 = 21,

    /// <summary>字寄存器（中型 PLC / AM600），16 位，十进制编号。</summary>
    MW = 22,
}
