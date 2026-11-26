namespace PlcHmi.Core.Enums;

/// <summary>点位数据类型。</summary>
public enum TagDataType
{
    Boolean = 0,
    Int16 = 1,
    UInt16 = 2,
    Int32 = 3,
    UInt32 = 4,
    Float32 = 5,
    /// <summary>32 位整数按 0.1 等比例缩放使用 Float32，此处保留双精度扩展位。</summary>
    Float64 = 6,
}

/// <summary>点位访问权限。</summary>
public enum TagAccessQuery
{
    /// <summary>只读</summary>
    Read = 0,
    /// <summary>只写</summary>
    Write = 1,
    /// <summary>可读可写</summary>
    ReadWrite = 2,
}

/// <summary>点位所属轮询分组。不同分组使用不同轮询周期。</summary>
public enum TagGroup
{
    /// <summary>命令组：不轮询，由写操作触发</summary>
    Command = 0,
    /// <summary>快组：100ms，用于状态、IO、轴位置等实时性要求高的数据</summary>
    Fast = 1,
    /// <summary>慢组：500ms，用于产量、参数等变化缓慢的数据</summary>
    Slow = 2,
    /// <summary>事件组：1000ms，用于报警字、诊断信息</summary>
    Event = 3,
}

/// <summary>32 位数据的字序（大小端组合方式）。</summary>
public enum WordOrder
{
    /// <summary>高字在前（ABCD）：高 16 位存于低地址寄存器</summary>
    BigEndian = 0,
    /// <summary>低字在前（CDAB）：汇川默认</summary>
    LittleEndian = 1,
}
