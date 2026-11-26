using PlcHmi.Communication.Modbus;
using PlcHmi.Core.Enums;
using Xunit;

namespace PlcHmi.Tests.Communication;

/// <summary>
/// 寄存器编解码测试。
/// <para>重点验证汇川默认的 <b>CDAB</b>（低字在前）字序处理。</para>
/// </summary>
public class RegisterCodecTest
{
    // ── 16 位 ─────────────────────────────────────────────────────────

    [Fact]
    public void DecodeUInt16()
    {
        var resultValue = RegisterCodec.Decode(TagDataType.UInt16, new ushort[] { 1234 });
        Assert.Equal((ushort)1234, Assert.IsType<ushort>(resultValue));
    }

    [Fact]
    public void DecodeInt16_NegativeNumber()
    {
        // -100 的补码为 0xFF9C
        var resultValue = RegisterCodec.Decode(TagDataType.Int16, new ushort[] { 0xFF9C });
        Assert.Equal((short)-100, resultValue);
    }

    [Fact]
    public void EncodeInt16_NegativeNumber()
    {
        var registerList = RegisterCodec.Encode(TagDataType.Int16, -100);
        Assert.Single(registerList);
        Assert.Equal(0xFF9C, registerList[0]);
    }

    // ── 32 位字序（核心）────────────────────────────────────────────────

    [Fact]
    public void EncodeFloat32_CDABLowWordAtBefore_SymbolValidInovanceDefault()
    {
        // 3.14f = 0x4048F5C3
        // CDAB 下：低字 0xF5C3 在第一个寄存器，高字 0x4048 在第二个
        var registerList = RegisterCodec.Encode(TagDataType.Float32, 3.14f, WordOrder.LittleEndian);

        Assert.Equal(2, registerList.Length);
        Assert.Equal(0xF5C3, registerList[0]);   // 低字
        Assert.Equal(0x4048, registerList[1]);   // 高字
    }

    [Fact]
    public void EncodeFloat32_ABCDHighWordAtBefore()
    {
        var registerList = RegisterCodec.Encode(TagDataType.Float32, 3.14f, WordOrder.BigEndian);

        Assert.Equal(0x4048, registerList[0]);   // 高字
        Assert.Equal(0xF5C3, registerList[1]);   // 低字
    }

    [Fact]
    public void DecodeFloat32_CDABLowWordAtBefore()
    {
        var Value = RegisterCodec.Decode(TagDataType.Float32, new ushort[] { 0xF5C3, 0x4048 }, WordOrder.LittleEndian);

        Assert.Equal(3.14f, (float)Value, precision: 5);
    }

    [Fact]
    public void DecodeFloat32_ABCDHighWordAtBefore()
    {
        var Value = RegisterCodec.Decode(TagDataType.Float32, new ushort[] { 0x4048, 0xF5C3 }, WordOrder.BigEndian);

        Assert.Equal(3.14f, (float)Value, precision: 5);
    }

    [Fact]
    public void EncodeDecodeUInt32_ShouldRoundTripConsistent()
    {
        const uint Original = 123456789u;

        var registerList = RegisterCodec.Encode(TagDataType.UInt32, Original, WordOrder.LittleEndian);
        var decoded = RegisterCodec.Decode(TagDataType.UInt32, registerList, WordOrder.LittleEndian);

        Assert.Equal(Original, decoded);
    }

    [Fact]
    public void EncodeDecodeInt32_NegativeNumberShouldRoundTripConsistent()
    {
        const int Original = -987654;

        var registerList = RegisterCodec.Encode(TagDataType.Int32, Original, WordOrder.LittleEndian);
        var decoded = RegisterCodec.Decode(TagDataType.Int32, registerList, WordOrder.LittleEndian);

        Assert.Equal(Original, decoded);
    }

    [Fact]
    public void EncodeDecodeFloat32_ShouldRoundTripConsistent()
    {
        const float Original = 350.55f;

        var registerList = RegisterCodec.Encode(TagDataType.Float32, Original, WordOrder.LittleEndian);
        var decoded = (float)RegisterCodec.Decode(TagDataType.Float32, registerList, WordOrder.LittleEndian);

        Assert.Equal(Original, decoded, precision: 4);
    }

    // ── 字节与寄存器互转 ───────────────────────────────────────────────

    [Fact]
    public void RegisterConvertByte_ShouldByLargeSplitByte()
    {
        var byteValue = RegisterCodec.RegisterConvertByte(new ushort[] { 0x1234, 0xABCD });

        Assert.Equal(new byte[] { 0x12, 0x34, 0xAB, 0xCD }, byteValue);
    }

    [Fact]
    public void ByteConvertRegister_ShouldByLargeGroupValid()
    {
        var registerList = RegisterCodec.ByteConvertRegister(new byte[] { 0x12, 0x34, 0xAB, 0xCD });

        Assert.Equal(new ushort[] { 0x1234, 0xABCD }, registerList);
    }
}
