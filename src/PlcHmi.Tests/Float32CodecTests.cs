using 上位机.通信.Modbus;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.上位机测试;

/// <summary>
/// 32 位浮点（IEEE 754 单精度）CDAB 编解码测试。
///
/// <para><b>这些期望值是独立算出来的，不是从实现里抄回来的。</b>
/// 编码器自己算一遍、测试再抄一遍，只能证明"代码没变"，证明不了"算对了"。</para>
///
/// <para>CDAB 的含义：16 位值先按大端排成 4 字节 <c>A B C D</c>，其中 <c>AB</c> 是高字、
/// <c>CD</c> 是低字；CDAB 即「低字在前」，所以寄存器顺序是 <c>[CD, AB]</c>。</para>
/// </summary>
public class Float32编解码器测试
{
    [Theory]
    [InlineData(120.00, 0x42F0, 0x0000)]
    [InlineData(250.50, 0x437A, 0x8000)]
    [InlineData(-999.99, 0xC479, 0xFF5C)]
    [InlineData(9999.99, 0x461C, 0x3FF6)]
    [InlineData(0.00, 0x0000, 0x0000)]
    [InlineData(-50.25, 0xC249, 0x0000)]
    public void 编码为CDAB时低字在前(double 值, int 期望高, int 期望低)
    {
        var 寄存器列表 = 寄存器编解码器.编码(标签数据类型.Float32, 值, 字顺序.小字节序);

        Assert.Equal(2, 寄存器列表.Length);
        Assert.Equal((ushort)期望低, 寄存器列表[0]);    // 第一个寄存器 = 低字 CD
        Assert.Equal((ushort)期望高, 寄存器列表[1]);   // 第二个寄存器 = 高字 AB
    }

    [Theory]
    [InlineData(120.00, 0x42F0, 0x0000)]
    [InlineData(250.50, 0x437A, 0x8000)]
    [InlineData(-999.99, 0xC479, 0xFF5C)]
    [InlineData(9999.99, 0x461C, 0x3FF6)]
    [InlineData(-50.25, 0xC249, 0x0000)]
    public void 从CDAB解码应还原原值(double 期望, int 高, int 低)
    {
        ushort[] 寄存器列表 = [(ushort)低, (ushort)高];

        var 已解码 = 寄存器编解码器.解码(标签数据类型.Float32, 寄存器列表, 字顺序.小字节序);
        var 值 = Assert.IsType<float>(已解码);

        Assert.True(
            坐标辅助.近乎相等(期望, 值, 0.01),
            $"期望 {期望}，实际 {值}");
    }

    [Theory]
    [InlineData(0.00)]
    [InlineData(120.00)]
    [InlineData(250.50)]
    [InlineData(-999.99)]
    [InlineData(9999.99)]
    [InlineData(1234.56)]
    [InlineData(-50.25)]
    public void 编码解码往返后显示文本应完全一致(double 值)
    {
        var 寄存器列表 = 寄存器编解码器.编码(标签数据类型.Float32, 值, 字顺序.小字节序);
        var 已解码 = (float)寄存器编解码器.解码(标签数据类型.Float32, 寄存器列表, 字顺序.小字节序)!;

        // 数值层面允许浮点误差
        Assert.True(
            坐标辅助.近乎相等(值, 已解码, 坐标辅助.默认容差),
            $"{值} 往返后变成 {已解码}");

        // 但显示层面必须一模一样 —— 这是操作员实际看到的
        Assert.Equal(值.ToString("F2"), 已解码.ToString("F2"));
    }

    [Fact]
    public void 字序写反会把120读成0()
    {
        // 守住这个坑：如果哪天有人把 LittleEndian 改成 BigEndian，
        // 现场表现是"轴就是不动"，极难排查。这条测试必须永远绿。
        var 正确 = 寄存器编解码器.编码(标签数据类型.Float32, 120.00, 字顺序.小字节序);
        var 反向 = 寄存器编解码器.编码(标签数据类型.Float32, 120.00, 字顺序.大字节序);

        Assert.False(正确.SequenceEqual(反向));
        Assert.Equal((ushort)0x0000, 正确[0]);
        Assert.Equal((ushort)0x42F0, 正确[1]);
        Assert.Equal((ushort)0x42F0, 反向[0]);

        // 按错误的字序解，120.00 会变成 0.00
        var 有误 = (float)寄存器编解码器.解码(标签数据类型.Float32, 反向, 字顺序.小字节序)!;
        Assert.Equal("0.00", 有误.ToString("F2"));

        // 正确字序则还原
        var 右 = (float)寄存器编解码器.解码(标签数据类型.Float32, 正确, 字顺序.小字节序)!;
        Assert.Equal("120.00", 右.ToString("F2"));
    }

    [Fact]
    public void 浮点与32位整数使用相同的字序拆分规则()
    {
        // 这条守住一个便利事实：两者都是"低字在前"，
        // 所以 RegisterCodec 的框架可以共用，不需要为浮点单开一套。
        var 浮点寄存器 = 寄存器编解码器.编码(标签数据类型.Float32, 120.0, 字顺序.小字节序);
        var 整数寄存器 = 寄存器编解码器.编码(标签数据类型.UInt32, 0x42F00000u, 字顺序.小字节序);

        Assert.True(浮点寄存器.SequenceEqual(整数寄存器));
    }

    [Fact]
    public void 寄存器不足时应抛异常()
    {
        ushort[] 短 = [0x0000];

        Assert.Throws<ArgumentException>(
            () => 寄存器编解码器.解码(标签数据类型.Float32, 短, 字顺序.小字节序));
    }
}

/// <summary>坐标格式化与容差比较的测试。</summary>
public class 坐标辅助测试
{
    [Theory]
    [InlineData(0.0, "0.00")]
    [InlineData(120.0, "120.00")]
    [InlineData(-999.99, "-999.99")]
    [InlineData(9999.99, "9999.99")]
    [InlineData(-50.25, "-50.25")]
    public void 格式化应固定两位小数(double 值, string 期望) =>
        Assert.Equal(期望, 坐标辅助.格式化(值));

    [Fact]
    public void 浮点尾数不应泄漏到界面()
    {
        // float32 装不下精确的 9999.99，实际是 9999.99023...
        var 原始值 = (double)(float)9999.99;

        Assert.NotEqual(9999.99, 原始值);                          // 误差确实存在
        Assert.Equal("9999.99", 坐标辅助.格式化(原始值));   // 但界面显示必须干净
    }

    [Fact]
    public void 容差比较应容忍浮点误差()
    {
        var 已存储 = (double)(float)9999.99;

        Assert.NotEqual(9999.99, 已存储);
        Assert.True(坐标辅助.近乎相等(9999.99, 已存储));
    }

    [Fact]
    public void 容差比较应识别出真实差异()
    {
        // 相差 0.01 是真实的刻度差异，必须判为"不一致"
        Assert.False(坐标辅助.近乎相等(120.00, 120.01));

        // 相差 0.001 属于浮点噪声，应判为"一致"
        Assert.True(坐标辅助.近乎相等(120.000, 120.001));
    }

    [Fact]
    public void 两个坐标点的容差比较应逐轴进行()
    {
        var 甲 = new 示教点位 { X = 120.00, Y = 0, Z = -50.00, R = 90.00 };
        var 乙 = new 示教点位 { X = 120.00, Y = 0, Z = -50.00, R = 90.00 };

        Assert.True(坐标辅助.近乎相等(甲, 乙));

        var 客户端 = 乙 with { R = 90.01 };
        Assert.False(坐标辅助.近乎相等(甲, 客户端));
    }

    [Theory]
    [InlineData("120.00", 120.0)]
    [InlineData(" 250.5 ", 250.5)]
    [InlineData("120.00 mm", 120.0)]
    [InlineData("90.00°", 90.0)]
    [InlineData("-999.99", -999.99)]
    public void 解析输入应容忍空格与单位后缀(string 文本, double 期望)
    {
        Assert.True(坐标辅助.尝试解析(文本, out var 值));
        Assert.Equal(期望, 值, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    public void 非法输入应解析失败(string 文本) =>
        Assert.False(坐标辅助.尝试解析(文本, out _));

    [Fact]
    public void 范围钳制应限制到上下限()
    {
        Assert.Equal(9999.99, 坐标辅助.钳制(20000, -999.99, 9999.99));
        Assert.Equal(-999.99, 坐标辅助.钳制(-5000, -999.99, 9999.99));
        Assert.Equal(120.0, 坐标辅助.钳制(120.0, -999.99, 9999.99));
    }
}

/// <summary>定位点模型的测试。</summary>
public class 示教点位测试
{
    [Fact]
    public void 按轴读写坐标应正确()
    {
        var 点位项 = 示教点位.空.附带(轴标识.Z, -50.25);

        Assert.Equal(-50.25, 点位项.Z);
        Assert.Equal(0.0, 点位项.X);
        Assert.Equal(-50.25, 点位项[轴标识.Z]);
        Assert.True(示教点位.空.是否空);
        Assert.False(点位项.是否空);
    }

    [Fact]
    public void 零值比较是安全的()
    {
        // 0 在浮点中是精确可表示值，所以 IsEmpty 用 == 0 判断不会误判
        var 零 = new 示教点位 { X = 0.0, Y = 0.0, Z = 0.0, R = 0.0 };

        Assert.True(零.是否空);
        Assert.Equal(0.0, (double)(float)0.0);
    }

    [Fact]
    public void 范围校验应识别越界()
    {
        var 成功 = new 示教点位 { X = 9999.99, Y = -999.99, Z = 0, R = 100 };
        Assert.True(成功.是否在范围内(-999.99, 9999.99));

        var 大 = new 示教点位 { X = 10000.0 };
        Assert.False(大.是否在范围内(-999.99, 9999.99));

        var 小型 = new 示教点位 { R = -1000.0 };
        Assert.False(小型.是否在范围内(-999.99, 9999.99));
    }

    [Fact]
    public void With应返回新实例而不修改原实例()
    {
        var 原始 = new 示教点位 { X = 1, Y = 2, Z = 3, R = 4 };
        var 已修改 = 原始.附带(轴标识.X, 99);

        Assert.Equal(1.0, 原始.X);     // 原实例不变
        Assert.Equal(99.0, 已修改.X);
        Assert.Equal(2.0, 已修改.Y);
    }
}
