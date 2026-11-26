using System.Text;
using 上位机.业务.配置;
using 上位机.业务.服务;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.上位机测试;

/// <summary>
/// 变量表 CSV 解析与变量表服务单测：结构体行跳过、类型映射、编码探测、名称/地址双索引。
/// </summary>
public class 变量表测试
{
    /// <summary>与汇川 AutoShop 导出格式一致的样例（含结构体头行与字.位行）。</summary>
    private const string 样例CSV =
        "序号,变量名,数据类型,隐藏初始值,初始值,掉电保持,网络公开,注释,软元件地址,\n" +
        "1,X轴参数,单轴参数,,...,保持,公有,,D1200,\n" +
        ",X轴参数.r_定位速度,REAL,,0.000000,,,,D1200,\n" +
        ",X轴参数.r_加减速度,REAL,,0.000000,,,,D1202,\n" +
        ",X轴命令.b_轴使能,BOOL,,OFF,,,,D1210.0,\n" +
        ",X轴命令.i_手动速度档位,INT,,0,,,,D1212,\n" +
        ",系统状态.b_运行,BOOL,,OFF,,,,M200,\n";

    [Fact]
    public void 解析_样例CSV_应跳过结构体头行并映射类型()
    {
        var 结果 = 变量表CSV解析器.解析文本(样例CSV);

        // 6 行数据：结构体头行“X轴参数”（类型=单轴参数）被跳过 → 5 条
        Assert.Equal(5, 结果.文档.条目集合.Count);
        Assert.Equal(1, 结果.跳过行数);

        var 定位速度 = 结果.文档.查找("X轴参数.r_定位速度");
        Assert.NotNull(定位速度);
        Assert.Equal("D1200", 定位速度.软元件地址);
        Assert.Equal(标签数据类型.Float32, 定位速度.数据类型);

        var 档位 = 结果.文档.查找("X轴命令.i_手动速度档位");
        Assert.NotNull(档位);
        Assert.Equal(标签数据类型.Int16, 档位.数据类型);

        var 运行 = 结果.文档.查找("系统状态.b_运行");
        Assert.NotNull(运行);
        Assert.Equal("M200", 运行.软元件地址);
        Assert.Equal(标签数据类型.布尔, 运行.数据类型);
    }

    [Fact]
    public void 解析_字位形式地址_应保留条目()
    {
        // D1210.0 是 D 字内位：条目保留，且 2026-09-17 起可寻址（按整字读-改-写）
        var 结果 = 变量表CSV解析器.解析文本(样例CSV);

        var 使能 = 结果.文档.查找("X轴命令.b_轴使能");
        Assert.NotNull(使能);
        Assert.Equal("D1210.0", 使能.软元件地址);
    }

    [Fact]
    public void 解析_空内容_应抛出说明性异常()
    {
        Assert.Throws<InvalidOperationException>(() => 变量表CSV解析器.解析文本("   "));
    }

    [Fact]
    public void 解析_全部行不可识别_应抛出说明性异常()
    {
        var 内容 = "序号,变量名,数据类型,隐藏初始值,初始值,掉电保持,网络公开,注释,软元件地址,\n" +
                   ",结构体A,自定义类型,,0,,,,D100,\n";

        Assert.Throws<InvalidOperationException>(() => 变量表CSV解析器.解析文本(内容));
    }

    [Fact]
    public void 读取文本_GBK编码文件_应正确解码()
    {
        var 路径 = Path.Combine(Path.GetTempPath(), $"变量表_{Guid.NewGuid():N}.csv");

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            File.WriteAllBytes(路径, Encoding.GetEncoding("GBK").GetBytes(样例CSV));

            var 文本 = 变量表CSV解析器.读取文本(路径);

            Assert.Contains("X轴参数.r_定位速度", 文本);
        }
        finally
        {
            File.Delete(路径);
        }
    }

    // ── 变量表服务：名称 / 地址双索引 ──────────────────────────────────

    private static 变量表服务 创建服务(变量表文档 文档) =>
        new(文档, new 上位机.通信.汇川.汇川地址解析器(PLC系列.小型PLC),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<变量表服务>.Instance);

    [Fact]
    public void 服务_按地址反查_起始与数量都须精确匹配()
    {
        var 服务 = 创建服务(变量表CSV解析器.解析文本(样例CSV).文档);

        // REAL 变量占 2 字：只有 (保持寄存器, 1200, 2) 命中
        Assert.NotNull(服务.按地址查找(Modbus区域.保持寄存器, 1200, 2));
        Assert.Null(服务.按地址查找(Modbus区域.保持寄存器, 1200, 1));
        Assert.Null(服务.按地址查找(Modbus区域.保持寄存器, 1201, 1));   // 高字不是首地址

        // 单字 INT 变量
        Assert.NotNull(服务.按地址查找(Modbus区域.保持寄存器, 1212, 1));

        // M 区 BOOL 变量（线圈区）
        Assert.NotNull(服务.按地址查找(Modbus区域.线圈, 200, 1));

        // 字.位变量（D1210.0）：按整字占 1 字寻址，进地址索引
        var 位条目 = 服务.按首地址查找(Modbus区域.保持寄存器, 1210, out var 位数);
        Assert.NotNull(位条目);
        Assert.Equal("X轴命令.b_轴使能", 位条目!.变量名);
        Assert.Equal(1, 位数);
    }

    [Fact]
    public void 服务_同地址整字条目应优先于字位条目()
    {
        // 同一字上既有整字变量（D1210 REAL）又有位变量（D1210.0 BOOL）时，
        // 反查必须回到整字变量：整字写会覆盖位，定位错对象会写错数据
        var 文档 = new 变量表文档
        {
            条目集合 =
            [
                new() { 变量名 = "位变量", 软元件地址 = "D1210.0", 数据类型 = 标签数据类型.布尔 },
                new() { 变量名 = "整字变量", 软元件地址 = "D1210", 数据类型 = 标签数据类型.Int16 },
            ],
        };

        var 服务 = 创建服务(文档);

        var 命中 = 服务.按首地址查找(Modbus区域.保持寄存器, 1210, out var 数量值);
        Assert.NotNull(命中);
        Assert.Equal("整字变量", 命中!.变量名);
        Assert.Equal(1, 数量值);
    }

    [Fact]
    public void 服务_查找_大小写与空白容忍()
    {
        var 服务 = 创建服务(变量表CSV解析器.解析文本(样例CSV).文档);

        Assert.NotNull(服务.查找(" x轴参数.r_定位速度 "));
        Assert.Null(服务.查找("不存在的变量"));
        Assert.Null(服务.查找(""));
    }

    [Fact]
    public async Task 服务_导入CSV_应替换内存表并落盘()
    {
        var 服务 = 创建服务(new 变量表文档());
        var 路径 = Path.Combine(Path.GetTempPath(), $"变量表_{Guid.NewGuid():N}.csv");

        try
        {
            // 导入读取的是 CSV 源文件，落盘目标是 config/variables.json；
            // 测试进程的 config 目录即测试输出目录，允许写入
            await File.WriteAllTextAsync(路径, 样例CSV);

            var 结果 = await 服务.异步导入(路径);

            Assert.Equal(5, 结果.有效条数);
            Assert.Equal(5, 服务.条目集合.Count);
            Assert.NotNull(服务.按地址查找(Modbus区域.保持寄存器, 1200, 2));
        }
        finally
        {
            File.Delete(路径);
            File.Delete(上位机.业务.配置.配置路径.变量表);
        }
    }
}
