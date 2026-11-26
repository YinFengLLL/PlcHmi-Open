using System.Buffers.Binary;
using PlcHmi.Core.Enums;

namespace PlcHmi.Communication.Modbus;

/// <summary>
/// 寄存器数据编解码器。
/// <para><b>32 位字序是本项目的第二个大坑：</b>汇川 PLC 默认使用 <b>CDAB</b>（低字在前），
/// 即 32 位值的低 16 位存放在低地址寄存器。若不处理，读数会完全错乱。</para>
/// <para>示例：<c>3.14f</c> = <c>0x4048F5C3</c>，在 CDAB 下存为
/// D100=<c>0xF5C3</c>（低字）、D101=<c>0x4048</c>（高字）。</para>
/// </summary>
public static class RegisterCodec
{
    /// <summary>按数据类型解码寄存器。返回原始值（未缩放）。</summary>
    public static object Decode(TagDataType DataType, ReadOnlySpan<ushort> registerList, WordOrder WordOrder = WordOrder.LittleEndian)
    {
        return DataType switch
        {
            TagDataType.Boolean => registerList.Length > 0 && registerList[0] != 0,
            TagDataType.Int16 => unchecked((short)registerList[0]),
            TagDataType.UInt16 => registerList[0],
            TagDataType.Int32 => unchecked((int)MergeAsUInt32(registerList, WordOrder)),
            TagDataType.UInt32 => MergeAsUInt32(registerList, WordOrder),
            TagDataType.Float32 => BitConverter.UInt32BitsToSingle(MergeAsUInt32(registerList, WordOrder)),
            TagDataType.Float64 => DecodeFloat64(registerList, WordOrder),
            _ => throw new NotSupportedException($"不支持的数据类型：{DataType}"),
        };
    }

    /// <summary>把数值编码为寄存器数组。用于写入 PLC。</summary>
    public static ushort[] Encode(TagDataType DataType, double Value, WordOrder WordOrder = WordOrder.LittleEndian)
    {
        return DataType switch
        {
            TagDataType.Boolean => new[] { (ushort)(Value != 0 ? 1 : 0) },
            TagDataType.Int16 => new[] { unchecked((ushort)(short)Value) },
            TagDataType.UInt16 => new[] { (ushort)Value },
            TagDataType.Int32 => SplitAsUInt32(unchecked((uint)(int)Value), WordOrder),
            TagDataType.UInt32 => SplitAsUInt32((uint)Value, WordOrder),
            TagDataType.Float32 => SplitAsUInt32(BitConverter.SingleToUInt32Bits((float)Value), WordOrder),
            TagDataType.Float64 => EncodeFloat64(Value, WordOrder),
            _ => throw new NotSupportedException($"不支持的数据类型：{DataType}"),
        };
    }

    /// <summary>把双精度浮点拆成 4 个寄存器。</summary>
    private static ushort[] EncodeFloat64(double Value, WordOrder WordOrder)
    {
        var byteValue = new byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(byteValue, Value);

        // 大端字节序下：高 32 位在前
        var high = BinaryPrimitives.ReadUInt32BigEndian(byteValue);
        var low = BinaryPrimitives.ReadUInt32BigEndian(byteValue.AsSpan(4));

        var (first, noTwo) = WordOrder == WordOrder.LittleEndian ? (low, high) : (high, low);

        return new[]
        {
            (ushort)(first >> 16), (ushort)(first & 0xFFFF),
            (ushort)(noTwo >> 16), (ushort)(noTwo & 0xFFFF),
        };
    }

    /// <summary>从 4 个寄存器还原双精度浮点。</summary>
    private static double DecodeFloat64(ReadOnlySpan<ushort> registerList, WordOrder WordOrder)
    {
        if (registerList.Length < 4)
        {
            throw new ArgumentException("Float64 需要 4 个寄存器", nameof(registerList));
        }

        var first = ((uint)registerList[0] << 16) | registerList[1];
        var noTwo = ((uint)registerList[2] << 16) | registerList[3];

        var (high, low) = WordOrder == WordOrder.LittleEndian ? (noTwo, first) : (first, noTwo);

        var byteValue = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(byteValue, high);
        BinaryPrimitives.WriteUInt32BigEndian(byteValue.AsSpan(4), low);

        return BinaryPrimitives.ReadDoubleBigEndian(byteValue);
    }

    /// <summary>
    /// 按字序把两个寄存器拼成 32 位值。
    /// <para>大端（ABCD / 高字在前）：第一个寄存器是高 16 位。</para>
    /// <para>小端（CDAB / 低字在前，汇川默认）：第一个寄存器是低 16 位。</para>
    /// </summary>
    private static uint MergeAsUInt32(ReadOnlySpan<ushort> registerList, WordOrder WordOrder)
    {
        if (registerList.Length < 2)
        {
            throw new ArgumentException("32 位数据需要 2 个寄存器", nameof(registerList));
        }

        return WordOrder == WordOrder.LittleEndian
            ? ((uint)registerList[1] << 16) | registerList[0]
            : ((uint)registerList[0] << 16) | registerList[1];
    }

    /// <summary>按字序把 32 位值拆成两个寄存器。</summary>
    private static ushort[] SplitAsUInt32(uint Value, WordOrder WordOrder)
    {
        var high = (ushort)(Value >> 16);
        var low = (ushort)(Value & 0xFFFF);

        return WordOrder == WordOrder.LittleEndian
            ? new[] { low, high }
            : new[] { high, low };
    }

    /// <summary>把寄存器（大端字节序）转为字节数组，便于按位解析。</summary>
    public static byte[] RegisterConvertByte(ReadOnlySpan<ushort> registerList)
    {
        var byteValue = new byte[registerList.Length * 2];
        for (var Entry = 0; Entry < registerList.Length; Entry++)
        {
            byteValue[Entry * 2] = (byte)(registerList[Entry] >> 8);
            byteValue[Entry * 2 + 1] = (byte)(registerList[Entry] & 0xFF);
        }

        return byteValue;
    }

    /// <summary>把字节数组（大端字节序）转为寄存器数组。</summary>
    public static ushort[] ByteConvertRegister(ReadOnlySpan<byte> byteValue)
    {
        var registerList = new ushort[byteValue.Length / 2];
        for (var Entry = 0; Entry < registerList.Length; Entry++)
        {
            registerList[Entry] = (ushort)((byteValue[Entry * 2] << 8) | byteValue[Entry * 2 + 1]);
        }

        return registerList;
    }
}
