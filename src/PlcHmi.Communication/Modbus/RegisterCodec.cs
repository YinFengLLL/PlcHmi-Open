using System.Buffers.Binary;
using 上位机.核心.枚举;

namespace 上位机.通信.Modbus;

/// <summary>
/// 寄存器数据编解码器。
/// <para><b>32 位字序是本项目的第二个大坑：</b>汇川 PLC 默认使用 <b>CDAB</b>（低字在前），
/// 即 32 位值的低 16 位存放在低地址寄存器。若不处理，读数会完全错乱。</para>
/// <para>示例：<c>3.14f</c> = <c>0x4048F5C3</c>，在 CDAB 下存为
/// D100=<c>0xF5C3</c>（低字）、D101=<c>0x4048</c>（高字）。</para>
/// </summary>
public static class 寄存器编解码器
{
    /// <summary>按数据类型解码寄存器。返回原始值（未缩放）。</summary>
    public static object 解码(标签数据类型 数据类型, ReadOnlySpan<ushort> 寄存器列表, 字顺序 字顺序 = 字顺序.小字节序)
    {
        return 数据类型 switch
        {
            标签数据类型.布尔 => 寄存器列表.Length > 0 && 寄存器列表[0] != 0,
            标签数据类型.Int16 => unchecked((short)寄存器列表[0]),
            标签数据类型.UInt16 => 寄存器列表[0],
            标签数据类型.Int32 => unchecked((int)合并为UInt32(寄存器列表, 字顺序)),
            标签数据类型.UInt32 => 合并为UInt32(寄存器列表, 字顺序),
            标签数据类型.Float32 => BitConverter.UInt32BitsToSingle(合并为UInt32(寄存器列表, 字顺序)),
            标签数据类型.Float64 => 解码Float64(寄存器列表, 字顺序),
            _ => throw new NotSupportedException($"不支持的数据类型：{数据类型}"),
        };
    }

    /// <summary>把数值编码为寄存器数组。用于写入 PLC。</summary>
    public static ushort[] 编码(标签数据类型 数据类型, double 值, 字顺序 字顺序 = 字顺序.小字节序)
    {
        return 数据类型 switch
        {
            标签数据类型.布尔 => new[] { (ushort)(值 != 0 ? 1 : 0) },
            标签数据类型.Int16 => new[] { unchecked((ushort)(short)值) },
            标签数据类型.UInt16 => new[] { (ushort)值 },
            标签数据类型.Int32 => 拆分为UInt32(unchecked((uint)(int)值), 字顺序),
            标签数据类型.UInt32 => 拆分为UInt32((uint)值, 字顺序),
            标签数据类型.Float32 => 拆分为UInt32(BitConverter.SingleToUInt32Bits((float)值), 字顺序),
            标签数据类型.Float64 => 编码Float64(值, 字顺序),
            _ => throw new NotSupportedException($"不支持的数据类型：{数据类型}"),
        };
    }

    /// <summary>把双精度浮点拆成 4 个寄存器。</summary>
    private static ushort[] 编码Float64(double 值, 字顺序 字顺序)
    {
        var 字节 = new byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(字节, 值);

        // 大端字节序下：高 32 位在前
        var 高 = BinaryPrimitives.ReadUInt32BigEndian(字节);
        var 低 = BinaryPrimitives.ReadUInt32BigEndian(字节.AsSpan(4));

        var (首个, 第二) = 字顺序 == 字顺序.小字节序 ? (低, 高) : (高, 低);

        return new[]
        {
            (ushort)(首个 >> 16), (ushort)(首个 & 0xFFFF),
            (ushort)(第二 >> 16), (ushort)(第二 & 0xFFFF),
        };
    }

    /// <summary>从 4 个寄存器还原双精度浮点。</summary>
    private static double 解码Float64(ReadOnlySpan<ushort> 寄存器列表, 字顺序 字顺序)
    {
        if (寄存器列表.Length < 4)
        {
            throw new ArgumentException("Float64 需要 4 个寄存器", nameof(寄存器列表));
        }

        var 首个 = ((uint)寄存器列表[0] << 16) | 寄存器列表[1];
        var 第二 = ((uint)寄存器列表[2] << 16) | 寄存器列表[3];

        var (高, 低) = 字顺序 == 字顺序.小字节序 ? (第二, 首个) : (首个, 第二);

        var 字节 = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(字节, 高);
        BinaryPrimitives.WriteUInt32BigEndian(字节.AsSpan(4), 低);

        return BinaryPrimitives.ReadDoubleBigEndian(字节);
    }

    /// <summary>
    /// 按字序把两个寄存器拼成 32 位值。
    /// <para>大端（ABCD / 高字在前）：第一个寄存器是高 16 位。</para>
    /// <para>小端（CDAB / 低字在前，汇川默认）：第一个寄存器是低 16 位。</para>
    /// </summary>
    private static uint 合并为UInt32(ReadOnlySpan<ushort> 寄存器列表, 字顺序 字顺序)
    {
        if (寄存器列表.Length < 2)
        {
            throw new ArgumentException("32 位数据需要 2 个寄存器", nameof(寄存器列表));
        }

        return 字顺序 == 字顺序.小字节序
            ? ((uint)寄存器列表[1] << 16) | 寄存器列表[0]
            : ((uint)寄存器列表[0] << 16) | 寄存器列表[1];
    }

    /// <summary>按字序把 32 位值拆成两个寄存器。</summary>
    private static ushort[] 拆分为UInt32(uint 值, 字顺序 字顺序)
    {
        var 高 = (ushort)(值 >> 16);
        var 低 = (ushort)(值 & 0xFFFF);

        return 字顺序 == 字顺序.小字节序
            ? new[] { 低, 高 }
            : new[] { 高, 低 };
    }

    /// <summary>把寄存器（大端字节序）转为字节数组，便于按位解析。</summary>
    public static byte[] 寄存器转字节(ReadOnlySpan<ushort> 寄存器列表)
    {
        var 字节 = new byte[寄存器列表.Length * 2];
        for (var 条目 = 0; 条目 < 寄存器列表.Length; 条目++)
        {
            字节[条目 * 2] = (byte)(寄存器列表[条目] >> 8);
            字节[条目 * 2 + 1] = (byte)(寄存器列表[条目] & 0xFF);
        }

        return 字节;
    }

    /// <summary>把字节数组（大端字节序）转为寄存器数组。</summary>
    public static ushort[] 字节转寄存器(ReadOnlySpan<byte> 字节)
    {
        var 寄存器列表 = new ushort[字节.Length / 2];
        for (var 条目 = 0; 条目 < 寄存器列表.Length; 条目++)
        {
            寄存器列表[条目] = (ushort)((字节[条目 * 2] << 8) | 字节[条目 * 2 + 1]);
        }

        return 寄存器列表;
    }
}
