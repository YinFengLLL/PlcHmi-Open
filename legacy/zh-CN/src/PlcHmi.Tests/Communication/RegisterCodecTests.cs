using 上位机.通信.Modbus;
using 上位机.核心.枚举;
using Xunit;

namespace 上位机.上位机测试.通信;

/// <summary>
/// 寄存器编解码测试。
/// <para>重点验证汇川默认的 <b>CDAB</b>（低字在前）字序处理。</para>
/// </summary>
public class 寄存器编解码器测试
{
    // ── 16 位 ─────────────────────────────────────────────────────────

    [Fact]
    public void 解码UInt16()
    {
        var 结果值 = 寄存器编解码器.解码(标签数据类型.UInt16, new ushort[] { 1234 });
        Assert.Equal((ushort)1234, Assert.IsType<ushort>(结果值));
    }

    [Fact]
    public void 解码Int16_负数()
    {
        // -100 的补码为 0xFF9C
        var 结果值 = 寄存器编解码器.解码(标签数据类型.Int16, new ushort[] { 0xFF9C });
        Assert.Equal((short)-100, 结果值);
    }

    [Fact]
    public void 编码Int16_负数()
    {
        var 寄存器列表 = 寄存器编解码器.编码(标签数据类型.Int16, -100);
        Assert.Single(寄存器列表);
        Assert.Equal(0xFF9C, 寄存器列表[0]);
    }

    // ── 32 位字序（核心）────────────────────────────────────────────────

    [Fact]
    public void 编码Float32_CDAB低字在前_符合汇川默认()
    {
        // 3.14f = 0x4048F5C3
        // CDAB 下：低字 0xF5C3 在第一个寄存器，高字 0x4048 在第二个
        var 寄存器列表 = 寄存器编解码器.编码(标签数据类型.Float32, 3.14f, 字顺序.小字节序);

        Assert.Equal(2, 寄存器列表.Length);
        Assert.Equal(0xF5C3, 寄存器列表[0]);   // 低字
        Assert.Equal(0x4048, 寄存器列表[1]);   // 高字
    }

    [Fact]
    public void 编码Float32_ABCD高字在前()
    {
        var 寄存器列表 = 寄存器编解码器.编码(标签数据类型.Float32, 3.14f, 字顺序.大字节序);

        Assert.Equal(0x4048, 寄存器列表[0]);   // 高字
        Assert.Equal(0xF5C3, 寄存器列表[1]);   // 低字
    }

    [Fact]
    public void 解码Float32_CDAB低字在前()
    {
        var 值 = 寄存器编解码器.解码(标签数据类型.Float32, new ushort[] { 0xF5C3, 0x4048 }, 字顺序.小字节序);

        Assert.Equal(3.14f, (float)值, precision: 5);
    }

    [Fact]
    public void 解码Float32_ABCD高字在前()
    {
        var 值 = 寄存器编解码器.解码(标签数据类型.Float32, new ushort[] { 0x4048, 0xF5C3 }, 字顺序.大字节序);

        Assert.Equal(3.14f, (float)值, precision: 5);
    }

    [Fact]
    public void 编码解码UInt32_应往返一致()
    {
        const uint 原始 = 123456789u;

        var 寄存器列表 = 寄存器编解码器.编码(标签数据类型.UInt32, 原始, 字顺序.小字节序);
        var 已解码 = 寄存器编解码器.解码(标签数据类型.UInt32, 寄存器列表, 字顺序.小字节序);

        Assert.Equal(原始, 已解码);
    }

    [Fact]
    public void 编码解码Int32_负数应往返一致()
    {
        const int 原始 = -987654;

        var 寄存器列表 = 寄存器编解码器.编码(标签数据类型.Int32, 原始, 字顺序.小字节序);
        var 已解码 = 寄存器编解码器.解码(标签数据类型.Int32, 寄存器列表, 字顺序.小字节序);

        Assert.Equal(原始, 已解码);
    }

    [Fact]
    public void 编码解码Float32_应往返一致()
    {
        const float 原始 = 350.55f;

        var 寄存器列表 = 寄存器编解码器.编码(标签数据类型.Float32, 原始, 字顺序.小字节序);
        var 已解码 = (float)寄存器编解码器.解码(标签数据类型.Float32, 寄存器列表, 字顺序.小字节序);

        Assert.Equal(原始, 已解码, precision: 4);
    }

    // ── 字节与寄存器互转 ───────────────────────────────────────────────

    [Fact]
    public void 寄存器转字节_应按大端拆字节()
    {
        var 字节 = 寄存器编解码器.寄存器转字节(new ushort[] { 0x1234, 0xABCD });

        Assert.Equal(new byte[] { 0x12, 0x34, 0xAB, 0xCD }, 字节);
    }

    [Fact]
    public void 字节转寄存器_应按大端组合()
    {
        var 寄存器列表 = 寄存器编解码器.字节转寄存器(new byte[] { 0x12, 0x34, 0xAB, 0xCD });

        Assert.Equal(new ushort[] { 0x1234, 0xABCD }, 寄存器列表);
    }
}
