using PlcHmi.Communication.Modbus;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Tests;

/// <summary>
/// 32 位浮点（IEEE 754 单精度）CDAB 编解码测试。
///
/// <para><b>这些期望值是独立算出来的，不是从实现里抄回来的。</b>
/// 编码器自己算一遍、测试再抄一遍，只能证明"代码没变"，证明不了"算对了"。</para>
///
/// <para>CDAB 的含义：16 位值先按大端排成 4 字节 <c>A B C D</c>，其中 <c>AB</c> 是高字、
/// <c>CD</c> 是低字；CDAB 即「低字在前」，所以寄存器顺序是 <c>[CD, AB]</c>。</para>
/// </summary>
public class Float32CodecTest
{
    [Theory]
    [InlineData(120.00, 0x42F0, 0x0000)]
    [InlineData(250.50, 0x437A, 0x8000)]
    [InlineData(-999.99, 0xC479, 0xFF5C)]
    [InlineData(9999.99, 0x461C, 0x3FF6)]
    [InlineData(0.00, 0x0000, 0x0000)]
    [InlineData(-50.25, 0xC249, 0x0000)]
    public void EncodeAsCDABWhenLowWordAtBefore(double Value, int expectedHeight, int expectedLow)
    {
        var registerList = RegisterCodec.Encode(TagDataType.Float32, Value, WordOrder.LittleEndian);

        Assert.Equal(2, registerList.Length);
        Assert.Equal((ushort)expectedLow, registerList[0]);    // 第一个寄存器 = 低字 CD
        Assert.Equal((ushort)expectedHeight, registerList[1]);   // 第二个寄存器 = 高字 AB
    }

    [Theory]
    [InlineData(120.00, 0x42F0, 0x0000)]
    [InlineData(250.50, 0x437A, 0x8000)]
    [InlineData(-999.99, 0xC479, 0xFF5C)]
    [InlineData(9999.99, 0x461C, 0x3FF6)]
    [InlineData(-50.25, 0xC249, 0x0000)]
    public void FromCDABDecodeShouldRestoreOriginalValue(double expected, int high, int low)
    {
        ushort[] registerList = [(ushort)low, (ushort)high];

        var decoded = RegisterCodec.Decode(TagDataType.Float32, registerList, WordOrder.LittleEndian);
        var Value = Assert.IsType<float>(decoded);

        Assert.True(
            CoordinateAux.AlmostEqual(expected, Value, 0.01),
            $"期望 {expected}，实际 {Value}");
    }

    [Theory]
    [InlineData(0.00)]
    [InlineData(120.00)]
    [InlineData(250.50)]
    [InlineData(-999.99)]
    [InlineData(9999.99)]
    [InlineData(1234.56)]
    [InlineData(-50.25)]
    public void EncodeDecodeRoundTripAfterShowTextShouldCompleteAllConsistent(double Value)
    {
        var registerList = RegisterCodec.Encode(TagDataType.Float32, Value, WordOrder.LittleEndian);
        var decoded = (float)RegisterCodec.Decode(TagDataType.Float32, registerList, WordOrder.LittleEndian)!;

        // 数值层面允许浮点误差
        Assert.True(
            CoordinateAux.AlmostEqual(Value, decoded, CoordinateAux.defaultTolerance),
            $"{Value} 往返后变成 {decoded}");

        // 但显示层面必须一模一样 —— 这是操作员实际看到的
        Assert.Equal(Value.ToString("F2"), decoded.ToString("F2"));
    }

    [Fact]
    public void WordOrderWriteReverseWill120ReadAs0()
    {
        // 守住这个坑：如果哪天有人把 LittleEndian 改成 BigEndian，
        // 现场表现是"轴就是不动"，极难排查。这条测试必须永远绿。
        var positiveConfirm = RegisterCodec.Encode(TagDataType.Float32, 120.00, WordOrder.LittleEndian);
        var reverseTo = RegisterCodec.Encode(TagDataType.Float32, 120.00, WordOrder.BigEndian);

        Assert.False(positiveConfirm.SequenceEqual(reverseTo));
        Assert.Equal((ushort)0x0000, positiveConfirm[0]);
        Assert.Equal((ushort)0x42F0, positiveConfirm[1]);
        Assert.Equal((ushort)0x42F0, reverseTo[0]);

        // 按错误的字序解，120.00 会变成 0.00
        var hasWrong = (float)RegisterCodec.Decode(TagDataType.Float32, reverseTo, WordOrder.LittleEndian)!;
        Assert.Equal("0.00", hasWrong.ToString("F2"));

        // 正确字序则还原
        var right = (float)RegisterCodec.Decode(TagDataType.Float32, positiveConfirm, WordOrder.LittleEndian)!;
        Assert.Equal("120.00", right.ToString("F2"));
    }

    [Fact]
    public void FloatAnd32BitIntegerUseSameWordOrderSplitRuleThen()
    {
        // 这条守住一个便利事实：两者都是"低字在前"，
        // 所以 RegisterCodec 的框架可以共用，不需要为浮点单开一套。
        var floatRegister = RegisterCodec.Encode(TagDataType.Float32, 120.0, WordOrder.LittleEndian);
        var integerRegister = RegisterCodec.Encode(TagDataType.UInt32, 0x42F00000u, WordOrder.LittleEndian);

        Assert.True(floatRegister.SequenceEqual(integerRegister));
    }

    [Fact]
    public void RegisterNotEnoughWhenShouldThrow()
    {
        ushort[] shortValue = [0x0000];

        Assert.Throws<ArgumentException>(
            () => RegisterCodec.Decode(TagDataType.Float32, shortValue, WordOrder.LittleEndian));
    }
}

/// <summary>坐标格式化与容差比较的测试。</summary>
public class CoordinateAuxTest
{
    [Theory]
    [InlineData(0.0, "0.00")]
    [InlineData(120.0, "120.00")]
    [InlineData(-999.99, "-999.99")]
    [InlineData(9999.99, "9999.99")]
    [InlineData(-50.25, "-50.25")]
    public void FormatShouldFixedTwoBitDecimal(double Value, string expected) =>
        Assert.Equal(expected, CoordinateAux.Format(Value));

    [Fact]
    public void FloatTailNumberShouldNotLeakToUi()
    {
        // float32 装不下精确的 9999.99，实际是 9999.99023...
        var SourceValue = (double)(float)9999.99;

        Assert.NotEqual(9999.99, SourceValue);                          // 误差确实存在
        Assert.Equal("9999.99", CoordinateAux.Format(SourceValue));   // 但界面显示必须干净
    }

    [Fact]
    public void ToleranceCompareShouldTolerateFloatWrongDifference()
    {
        var storedStore = (double)(float)9999.99;

        Assert.NotEqual(9999.99, storedStore);
        Assert.True(CoordinateAux.AlmostEqual(9999.99, storedStore));
    }

    [Fact]
    public void ToleranceCompareShouldDetectOutRealDifference()
    {
        // 相差 0.01 是真实的刻度差异，必须判为"不一致"
        Assert.False(CoordinateAux.AlmostEqual(120.00, 120.01));

        // 相差 0.001 属于浮点噪声，应判为"一致"
        Assert.True(CoordinateAux.AlmostEqual(120.000, 120.001));
    }

    [Fact]
    public void TwoCoordinatePointToleranceCompareShouldPerItemAxisEnterRow()
    {
        var item = new TeachPoint { X = 120.00, Y = 0, Z = -50.00, R = 90.00 };
        var item2 = new TeachPoint { X = 120.00, Y = 0, Z = -50.00, R = 90.00 };

        Assert.True(CoordinateAux.AlmostEqual(item, item2));

        var Client = item2 with { R = 90.01 };
        Assert.False(CoordinateAux.AlmostEqual(item, Client));
    }

    [Theory]
    [InlineData("120.00", 120.0)]
    [InlineData(" 250.5 ", 250.5)]
    [InlineData("120.00 mm", 120.0)]
    [InlineData("90.00°", 90.0)]
    [InlineData("-999.99", -999.99)]
    public void ParseInputShouldTolerateEmptyCaseAndSingleBitSuffix(string Text, double expected)
    {
        Assert.True(CoordinateAux.TryParse(Text, out var Value));
        Assert.Equal(expected, Value, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    public void IllegalInputShouldResolveAnalyzeFailed(string Text) =>
        Assert.False(CoordinateAux.TryParse(Text, out _));

    [Fact]
    public void RangeClampShouldLimitToOnLowerLimit()
    {
        Assert.Equal(9999.99, CoordinateAux.Clamp(20000, -999.99, 9999.99));
        Assert.Equal(-999.99, CoordinateAux.Clamp(-5000, -999.99, 9999.99));
        Assert.Equal(120.0, CoordinateAux.Clamp(120.0, -999.99, 9999.99));
    }
}

/// <summary>定位点模型的测试。</summary>
public class TeachPointTest
{
    [Fact]
    public void ByAxisReadWriteCoordinateShouldBeCorrect()
    {
        var PointItem = TeachPoint.empty.AttachWith(AxisId.Z, -50.25);

        Assert.Equal(-50.25, PointItem.Z);
        Assert.Equal(0.0, PointItem.X);
        Assert.Equal(-50.25, PointItem[AxisId.Z]);
        Assert.True(TeachPoint.empty.IsEmpty);
        Assert.False(PointItem.IsEmpty);
    }

    [Fact]
    public void ZeroValueCompareIsSafety()
    {
        // 0 在浮点中是精确可表示值，所以 IsEmpty 用 == 0 判断不会误判
        var zero = new TeachPoint { X = 0.0, Y = 0.0, Z = 0.0, R = 0.0 };

        Assert.True(zero.IsEmpty);
        Assert.Equal(0.0, (double)(float)0.0);
    }

    [Fact]
    public void RangeValidateShouldDetectBeyondBoundary()
    {
        var Success = new TeachPoint { X = 9999.99, Y = -999.99, Z = 0, R = 100 };
        Assert.True(Success.IsInRange(-999.99, 9999.99));

        var large = new TeachPoint { X = 10000.0 };
        Assert.False(large.IsInRange(-999.99, 9999.99));

        var Small = new TeachPoint { R = -1000.0 };
        Assert.False(Small.IsInRange(-999.99, 9999.99));
    }

    [Fact]
    public void WithShouldReturnNewRealCaseInsteadOfModifyChangeOriginalRealCase()
    {
        var Original = new TeachPoint { X = 1, Y = 2, Z = 3, R = 4 };
        var modified = Original.AttachWith(AxisId.X, 99);

        Assert.Equal(1.0, Original.X);     // 原实例不变
        Assert.Equal(99.0, modified.X);
        Assert.Equal(2.0, modified.Y);
    }
}
