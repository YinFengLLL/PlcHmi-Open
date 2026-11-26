using Microsoft.Extensions.Logging.Abstractions;
using 上位机.业务.配置;
using 上位机.业务.服务;
using 上位机.核心.枚举;
using 上位机.核心.模型;
using 上位机.通信.仿真;
using 上位机.通信.汇川;
using 上位机.通信.Modbus;
using 上位机.通信.OPCUA;
using 上位机.核心.抽象;

namespace 上位机.上位机测试;

/// <summary>
/// 变量绑定回填与 OPC UA 变量符号寻址单测：
/// 地址回填、失效降级、NodeId 命中/回退、仿真客户端的类型化读写往返。
/// </summary>
public class 变量绑定与寻址测试
{
    /// <summary>与样例变量表对应的文档（D1200 REAL / D1212 INT / M200 BOOL）。</summary>
    private static 变量表文档 创建文档() => new()
    {
        条目集合 =
        [
            new() { 变量名 = "X轴参数.r_定位速度", 软元件地址 = "D1200", 数据类型 = 标签数据类型.Float32 },
            new() { 变量名 = "X轴命令.i_手动速度档位", 软元件地址 = "D1212", 数据类型 = 标签数据类型.Int16 },
            new() { 变量名 = "系统状态.b_运行", 软元件地址 = "M200", 数据类型 = 标签数据类型.布尔 },
            new() { 变量名 = "X轴命令.b_轴使能", 软元件地址 = "D1210.0", 数据类型 = 标签数据类型.布尔 },
        ],
    };

    private static 地址解析器接口 创建解析器() => new 汇川地址解析器(PLC系列.小型PLC);

    private static (变量表服务 表, NodeId推导器 推导器) 创建推导器(变量表文档? 文档 = null)
    {
        var 表 = new 变量表服务(文档 ?? 创建文档(), 创建解析器(), NullLogger<变量表服务>.Instance);
        var 推导器 = new NodeId推导器(new PLC连接选项(), new 汇川地址解析器(PLC系列.小型PLC), 表);
        return (表, 推导器);
    }

    // ── 绑定回填 ────────────────────────────────────────────────────────

    [Fact]
    public void 回填_变量存在_应返回变量地址()
    {
        var (表, _) = 创建推导器();

        var 地址 = 变量绑定回填.解析地址("X轴参数.r_定位速度", 表, 创建解析器(), "D1600", out var 错误);

        Assert.Equal(string.Empty, 错误);
        Assert.Equal("D1200", 地址);
    }

    [Fact]
    public void 回填_变量不存在_应保留原地址并说明原因()
    {
        var (表, _) = 创建推导器();

        var 地址 = 变量绑定回填.解析地址("不存在的变量", 表, 创建解析器(), "D1600", out var 错误);

        Assert.Contains("不在变量表中", 错误);
        Assert.Equal("D1600", 地址);
    }

    [Fact]
    public void 回填_字位地址_应接受并原样回填()
    {
        // D1210.0 是 D 字内位（汇川位打包）：2026-09-17 起按整字读-改-写可寻址
        var (表, _) = 创建推导器();

        var 地址 = 变量绑定回填.解析地址("X轴命令.b_轴使能", 表, 创建解析器(), "M220", out var 错误);

        Assert.Equal(string.Empty, 错误);
        Assert.Equal("D1210.0", 地址);
    }

    [Fact]
    public void 回填_未绑定_应保持原地址无错误()
    {
        var (表, _) = 创建推导器();

        var 地址 = 变量绑定回填.解析地址(null, 表, 创建解析器(), "M220", out var 错误);

        Assert.Equal(string.Empty, 错误);
        Assert.Equal("M220", 地址);
    }

    [Fact]
    public void 回填_配方配置_应批量回填参数项与控件地址()
    {
        var 配置 = new 配方配置
        {
            参数 =
            [
                new 配方参数规格 { 键 = "X.LocateSpeed", 名称 = "X 轴定位速度", 地址 = 1600, 变量 = "X轴参数.r_定位速度" },
            ],
            控件 = new 配方控件配置 { 配方号地址 = 3330, 配方号变量 = "X轴命令.i_手动速度档位" },
        };

        var 警告 = 变量绑定回填.应用配方配置(配置, 创建文档(), 创建解析器());

        Assert.Empty(警告);
        Assert.Equal(1200, 配置.参数[0].地址);       // D1200 → 1200
        Assert.Equal(1212, 配置.控件.配方号地址);    // D1212 → 1212
    }

    [Fact]
    public void 回填_配方配置_变量失效应给出警告且不改地址()
    {
        var 配置 = new 配方配置
        {
            参数 =
            [
                new 配方参数规格 { 键 = "X.LocateSpeed", 名称 = "X 轴定位速度", 地址 = 1600, 变量 = "已删除的变量" },
            ],
        };

        var 警告 = 变量绑定回填.应用配方配置(配置, 创建文档(), 创建解析器());

        Assert.Single(警告);
        Assert.Equal(1600, 配置.参数[0].地址);
    }

    // ── OPC UA 变量符号寻址 ─────────────────────────────────────────────

    [Fact]
    public void 推导器_变量命中_应按变量名生成NodeId()
    {
        var (_, 推导器) = 创建推导器();

        Assert.True(推导器.尝试匹配变量(Modbus区域.保持寄存器, 1200, out _, out var 数量));
        Assert.Equal(2, 数量);   // REAL 占 2 字
        Assert.Equal("ns=2;s=X轴参数.r_定位速度", 推导器.推导变量文本("X轴参数.r_定位速度"));
    }

    [Fact]
    public void 推导器_变量未命中_应回退软元件反解模板()
    {
        var (_, 推导器) = 创建推导器();

        Assert.False(推导器.尝试匹配变量(Modbus区域.保持寄存器, 1600, out _, out _));
        Assert.Equal("ns=2;s=D1600", 推导器.推导文本(Modbus区域.保持寄存器, 1600));
    }

    [Fact]
    public async Task 仿真客户端_变量绑定段_应按变量节点类型化读写往返()
    {
        // REAL 变量占 D1200–D1201：读(1200,2) 命中变量段 → 读 Float32 节点 →
        // 经寄存器编解码器（CDAB）拆为 2 个寄存器，与业务侧解码语义一致
        var 文档 = 创建文档();
        var 表 = new 变量表服务(文档, 创建解析器(), NullLogger<变量表服务>.Instance);
        var 推导器 = new NodeId推导器(new PLC连接选项(), new 汇川地址解析器(PLC系列.小型PLC), 表);
        var 设备 = new 虚拟PLC设备(NullLogger<虚拟PLC设备>.Instance);
        var 客户端 = new 仿真OPCUA客户端(设备, 推导器, NullLogger<仿真OPCUA客户端>.Instance);
        try
        {
            await 客户端.异步连接();

            // 写入 Float32 = 3.14 的寄存器对（CDAB：D1200=低字 0xF5C3，D1201=高字 0x4048）
            await 客户端.异步写入多个寄存器(1200, 寄存器编解码器.编码(标签数据类型.Float32, 3.14));

            var 读回 = await 客户端.异步读取保持寄存器(1200, 2);
            var 值 = 寄存器编解码器.解码(标签数据类型.Float32, 读回);

            Assert.Equal(3.14, (float)值, 0.001);

            // 单字 INT 变量：按变量节点读写
            await 客户端.异步写入单个寄存器(1212, 2);
            var 档位 = await 客户端.异步读取保持寄存器(1212, 1);
            Assert.Equal((ushort)2, 档位[0]);
        }
        finally
        {
            await 客户端.DisposeAsync();
            await 设备.DisposeAsync();
        }
    }
}
