using 上位机.核心.枚举;
using 上位机.核心.异常;
using 上位机.核心.模型;
using 上位机.通信.汇川;
using 上位机.通信.OPCUA;

namespace 上位机.上位机测试;

/// <summary>
/// NodeId 推导规则单测。覆盖软元件反解（含 X/Y 八进制还原）、模板占位符与非法地址。
/// </summary>
public class 节点Id推导器测试
{
    private static NodeId推导器 创建推导器(Action<PLC连接选项>? 调整 = null)
    {
        var 选项 = new PLC连接选项();
        调整?.Invoke(选项);
        return new NodeId推导器(选项, new 汇川地址解析器(选项.系列));
    }

    // ── 软元件符号推导 ──────────────────────────────────────────────────

    [Theory]
    [InlineData(Modbus区域.保持寄存器, 100, "ns=2;s=D100")]     // D 区
    [InlineData(Modbus区域.线圈, 100, "ns=2;s=M100")]           // M 区
    [InlineData(Modbus区域.离散输入, 63496, "ns=2;s=X10")]      // X 八进制：0xF800+8 → X10（不是 X8）；X 是输入走 FC02
    [InlineData(Modbus区域.线圈, 64512, "ns=2;s=Y0")]           // Y0 = 0xFC00
    [InlineData(Modbus区域.保持寄存器, 12293, "ns=2;s=R5")]     // R 基址 0x3000
    [InlineData(Modbus区域.线圈, 12295, "ns=2;s=B7")]           // B 基址 0x3000（线圈区）
    [InlineData(Modbus区域.线圈, 9226, "ns=2;s=SM10")]          // SM 基址 0x2400
    [InlineData(Modbus区域.保持寄存器, 9219, "ns=2;s=SD3")]     // SD 基址 0x2400
    [InlineData(Modbus区域.线圈, 61440, "ns=2;s=T0")]           // T 基址 0xF000
    [InlineData(Modbus区域.线圈, 62464, "ns=2;s=C0")]           // C 基址 0xF400
    [InlineData(Modbus区域.线圈, 57344, "ns=2;s=S0")]           // S 基址 0xE000
    public void 推导_常见软元件_应按规则生成节点文本(Modbus区域 区域值, int 地址值, string 期望)
    {
        var 推导器 = 创建推导器();

        Assert.Equal(期望, 推导器.推导文本(区域值, 地址值));
    }

    [Fact]
    public void 推导_命名空间索引可配置_应反映在节点文本()
    {
        var 推导器 = 创建推导器(选项 => 选项.OPCUA命名空间索引 = 5);

        Assert.Equal("ns=5;s=D100", 推导器.推导文本(Modbus区域.保持寄存器, 100));
    }

    [Fact]
    public void 推导_自定义格式模板_应替换全部占位符()
    {
        var 推导器 = 创建推导器(选项 => 选项.OPCUA节点格式 = "{命名空间}|{软元件}|{序号}");

        Assert.Equal("2|SM|10", 推导器.推导文本(Modbus区域.线圈, 9226));
    }

    [Fact]
    public void 推导_返回的NodeId_标识符应为软元件书写形式()
    {
        var 推导器 = 创建推导器();
        var 节点 = 推导器.推导(Modbus区域.保持寄存器, 100);

        Assert.Equal("D100", 节点.Identifier.ToString());
    }

    [Fact]
    public void 推导_同一地址多次调用_应命中缓存返回同一实例()
    {
        var 推导器 = 创建推导器();

        var 第一次 = 推导器.推导(Modbus区域.保持寄存器, 100);
        var 第二次 = 推导器.推导(Modbus区域.保持寄存器, 100);

        Assert.Same(第一次, 第二次);
    }

    // ── 非法地址 ────────────────────────────────────────────────────────

    [Fact]
    public void 推导_地址落在软元件区间空隙_应抛地址解析异常()
    {
        // 0x2000（8192）位于 M 区上限（7999 → 0x1F3F）与 SM 区基址（0x2400）之间的空隙
        var 推导器 = 创建推导器();

        Assert.Throws<地址解析异常>(() => 推导器.推导文本(Modbus区域.线圈, 8192));
    }

    [Fact]
    public void 推导_小型PLC无离散输入区软元件_应抛地址解析异常()
    {
        var 推导器 = 创建推导器();

        Assert.Throws<地址解析异常>(() => 推导器.推导文本(Modbus区域.离散输入, 0));
    }

    [Fact]
    public void 推导_负地址_应抛地址解析异常()
    {
        var 推导器 = 创建推导器();

        Assert.Throws<地址解析异常>(() => 推导器.推导文本(Modbus区域.保持寄存器, -1));
    }
}
