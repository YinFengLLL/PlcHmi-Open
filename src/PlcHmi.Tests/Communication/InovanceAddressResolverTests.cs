using 上位机.通信.汇川;
using 上位机.核心.枚举;
using 上位机.核心.异常;
using Xunit;

namespace 上位机.上位机测试.通信;

/// <summary>
/// 汇川地址解析器测试。
/// <para>重点验证 X / Y 的<b>八进制</b>换算——这是最容易写错、也最难在现场发现的一处。</para>
/// </summary>
public class 汇川地址解析器测试
{
    private readonly 汇川地址解析器 _小型 = new(PLC系列.小型PLC);
    private readonly 汇川地址解析器 _中等 = new(PLC系列.中等PLC);

    // ── 小型 PLC：X 区（八进制）─────────────────────────────────────────

    [Theory]
    [InlineData("X0", 0, 63488)]
    [InlineData("X7", 7, 63495)]
    [InlineData("X10", 8, 63496)]      // 关键：八进制的 10 = 十进制的 8
    [InlineData("X17", 15, 63503)]     // 八进制的 17 = 十进制的 15
    [InlineData("X20", 16, 63504)]
    [InlineData("X1777", 1023, 64511)] // X 区最大值
    public void 小型PLC_X点_应按八进制换算(string 输入项, int 期望序号, int 期望地址)
    {
        var 地址值 = _小型.解析(输入项);

        Assert.Equal(软元素.X, 地址值.元素);
        Assert.Equal(期望序号, 地址值.逻辑序号);
        Assert.Equal(期望地址, 地址值.Modbus地址);

        // X 是物理输入 → 离散输入区（FC02）。2026-10-17 修正：此前是在线圈区，
        // 等于用 FC01 读输入区，现场表现为"Y 一直对、X 一直错"。
        Assert.Equal(Modbus区域.离散输入, 地址值.区域);
    }

    // ── 小型 PLC：Y 区（八进制）─────────────────────────────────────────

    [Theory]
    [InlineData("Y0", 0, 64512)]
    [InlineData("Y7", 7, 64519)]
    [InlineData("Y10", 8, 64520)]      // 关键：八进制
    [InlineData("Y17", 15, 64527)]
    public void 小型PLC_Y点_应按八进制换算(string 输入项, int 期望序号, int 期望地址)
    {
        var 地址值 = _小型.解析(输入项);

        Assert.Equal(软元素.Y, 地址值.元素);
        Assert.Equal(期望序号, 地址值.逻辑序号);
        Assert.Equal(期望地址, 地址值.Modbus地址);
        Assert.Equal(Modbus区域.线圈, 地址值.区域);
    }

    // ── 小型 PLC：十进制区 ─────────────────────────────────────────────

    [Theory]
    [InlineData("M0", 软元素.M, 0, 0, Modbus区域.线圈)]
    [InlineData("M100", 软元素.M, 100, 100, Modbus区域.线圈)]
    [InlineData("M7999", 软元素.M, 7999, 7999, Modbus区域.线圈)]
    [InlineData("B100", 软元素.B, 100, 12388, Modbus区域.线圈)]
    [InlineData("S100", 软元素.S, 100, 57444, Modbus区域.线圈)]
    [InlineData("SM0", 软元素.特殊寄存器, 0, 9216, Modbus区域.线圈)]
    [InlineData("T10", 软元素.T, 10, 61450, Modbus区域.线圈)]
    [InlineData("C10", 软元素.C, 10, 62474, Modbus区域.线圈)]
    [InlineData("D0", 软元素.D, 0, 0, Modbus区域.保持寄存器)]
    [InlineData("D100", 软元素.D, 100, 100, Modbus区域.保持寄存器)]
    [InlineData("D7999", 软元素.D, 7999, 7999, Modbus区域.保持寄存器)]
    [InlineData("R100", 软元素.R, 100, 12388, Modbus区域.保持寄存器)]
    [InlineData("SD0", 软元素.系统寄存器, 0, 9216, Modbus区域.保持寄存器)]
    public void 小型PLC_十进制软元件_应按区起始地址加序号(
        string 输入项, 软元素 期望元素, int 期望序号, int 期望地址, Modbus区域 期望区域)
    {
        var 地址值 = _小型.解析(输入项);

        Assert.Equal(期望元素, 地址值.元素);
        Assert.Equal(期望序号, 地址值.逻辑序号);
        Assert.Equal(期望地址, 地址值.Modbus地址);
        Assert.Equal(期望区域, 地址值.区域);
    }

    // ── 小型 PLC：SM / SD 优先级（不能被 M / D 抢匹配）──────────────────

    [Fact]
    public void 小型PLC_SM与SD_不应被M与D误匹配()
    {
        // SM 必须匹配为 SoftElement.SM（起始 9216），而不是 S + "M0" 之类
        Assert.Equal(软元素.特殊寄存器, _小型.解析("SM1000").元素);
        Assert.Equal(软元素.系统寄存器, _小型.解析("SD500").元素);

        // 对照：M0 与 D0 各自正常
        Assert.Equal(0, _小型.解析("M0").Modbus地址);
        Assert.Equal(0, _小型.解析("D0").Modbus地址);
    }

    // ── 小型 PLC：错误处理 ─────────────────────────────────────────────

    [Theory]
    [InlineData("X8")]      // 8 不是合法的八进制数字
    [InlineData("Y19")]     // 9 不是合法的八进制数字
    [InlineData("X2000")]   // 超出 X 区上限（八进制 2000 = 1024 > 1023）
    [InlineData("M8000")]   // 超出 M 区上限 7999
    [InlineData("Z10")]     // 未知软元件
    [InlineData("X")]       // 缺少序号
    [InlineData("")]        // 空地址
    public void 小型PLC_非法地址_应抛出解析异常(string 输入项)
    {
        Assert.Throws<地址解析异常>(() => _小型.解析(输入项));
    }

    [Fact]
    public void TryResolve_非法地址_应返回false而不抛异常()
    {
        Assert.False(_小型.尝试解析("X8", out var 地址值));
        Assert.Null(地址值);

        Assert.True(_小型.尝试解析("X10", out var 有效));
        Assert.NotNull(有效);
    }

    // ── 中型 PLC（AM600）──────────────────────────────────────────────

    [Theory]
    [InlineData("QX0.0", 0, 0)]
    [InlineData("QX0.3", 3, 3)]
    [InlineData("QX0.7", 7, 7)]
    [InlineData("QX1.0", 8, 8)]        // 字节 1 位 0 = 第 9 个点
    [InlineData("QX2.5", 21, 21)]
    public void 中型PLC_QX位寻址_应按字节乘8加位号(string 输入项, int 期望序号, int 期望地址)
    {
        var 地址值 = _中等.解析(输入项);

        Assert.Equal(软元素.Q, 地址值.元素);
        Assert.Equal(期望序号, 地址值.逻辑序号);
        Assert.Equal(期望地址, 地址值.Modbus地址);
        Assert.Equal(Modbus区域.线圈, 地址值.区域);
    }

    [Theory]
    [InlineData("IX0.0", 0)]
    [InlineData("IX1.7", 15)]
    public void 中型PLC_IX位寻址_应映射到离散输入区(string 输入项, int 期望序号)
    {
        var 地址值 = _中等.解析(输入项);

        Assert.Equal(软元素.接口, 地址值.元素);
        Assert.Equal(期望序号, 地址值.逻辑序号);
        Assert.Equal(Modbus区域.离散输入, 地址值.区域);
    }

    [Fact]
    public void 中型PLC_MW寻址_应映射到保持寄存器()
    {
        var 地址值 = _中等.解析("MW100");

        Assert.Equal(软元素.MW, 地址值.元素);
        Assert.Equal(100, 地址值.Modbus地址);
        Assert.Equal(Modbus区域.保持寄存器, 地址值.区域);
    }

    [Fact]
    public void 中型PLC_SM与SD_应带专用功能码()
    {
        var SM地址 = _中等.解析("SM10");
        Assert.Equal(Modbus区域.线圈, SM地址.区域);
        Assert.Equal<byte?>(0x31, SM地址.功能码覆盖);   // 汇川专用读功能码

        var SD地址 = _中等.解析("SD10");
        Assert.Equal(Modbus区域.保持寄存器, SD地址.区域);
        Assert.Equal<byte?>(0x33, SD地址.功能码覆盖);
    }

    [Fact]
    public void 中型PLC_不支持小型PLC软元件()
    {
        // D 在 AM600 上不存在（应为 MW）
        Assert.Throws<地址解析异常>(() => _中等.解析("D100"));
        Assert.Throws<地址解析异常>(() => _中等.解析("X10"));
    }

    // ── 寄存器数量 ────────────────────────────────────────────────────

    [Theory]
    [InlineData(标签数据类型.布尔, 1)]
    [InlineData(标签数据类型.UInt16, 1)]
    [InlineData(标签数据类型.Int16, 1)]
    [InlineData(标签数据类型.Int32, 2)]
    [InlineData(标签数据类型.UInt32, 2)]
    [InlineData(标签数据类型.Float32, 2)]
    [InlineData(标签数据类型.Float64, 4)]
    public void 寄存器数量_应按数据类型判定(标签数据类型 type, int 期望)
    {
        Assert.Equal(期望, _小型.获取寄存器数量(type));
    }

    // ── 大小写容错 ────────────────────────────────────────────────────

    [Theory]
    [InlineData("x10")]
    [InlineData("X10")]
    [InlineData("m100")]
    [InlineData("d100")]
    public void 地址解析_应忽略大小写(string 输入项)
    {
        var 地址值 = _小型.解析(输入项);
        Assert.True(地址值.Modbus地址 >= 0);
    }

    // ── Explain 诊断文本 ──────────────────────────────────────────────

    [Fact]
    public void Explain_应生成可核对的换算说明()
    {
        var 文本 = _小型.说明("X10");

        Assert.Contains("63496", 文本);
        Assert.Contains("八进制", 文本);
    }
}
