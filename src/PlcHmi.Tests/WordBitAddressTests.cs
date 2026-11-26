using Microsoft.Extensions.Logging.Abstractions;
using 上位机.业务.服务;
using 上位机.通信.仿真;
using 上位机.通信.汇川;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.上位机测试;

/// <summary>
/// D/R 区「字内位」寻址测试（2026-09-17 按 PLC 地址分配表引入）。
///
/// <para><b>背景</b>：汇川位打包约定 BOOL 占 D 字的一位（可连续），
/// 如 X 轴命令字 D1210 的位 2/3 分别是正向/反向点动。Modbus 没有写字内位的功能码，
/// 因此写通道是「读整字 → 改位 → 写回」，读通道是「读整字 → 按位取值」。
/// 本组守住两件事：<b>改位不伤同字其它位</b>、<b>读回的位与实际位一致</b>。</para>
/// <para>读的是测试输出目录里的 <c>config/tags.json</c>，与写配置文件的用例（轴地址服务测试）
/// 归入同一集合，避免并行时读到半途的表。</para>
/// </summary>
[Collection("config-files")]
public class 字位寻址测试
{
    private static 汇川地址解析器 解析器() => new(PLC系列.小型PLC);

    private static 标签仓储 仓储() => new(NullLogger<标签仓储>.Instance);

    // ── 解析 ────────────────────────────────────────────────────────────

    [Fact]
    public void 解析_D区字位_应给出字地址与位号()
    {
        var 地址值 = 解析器().解析("D1210.2");

        Assert.Equal(软元素.D, 地址值.元素);
        Assert.Equal(1210, 地址值.逻辑序号);
        Assert.Equal(1210, 地址值.Modbus地址);
        Assert.Equal(Modbus区域.保持寄存器, 地址值.区域);
        Assert.Equal(2, 地址值.位序号);
        Assert.True(地址值.是字内位);
    }

    [Fact]
    public void 解析_R区字位_应同样支持且大小写不敏感()
    {
        var 地址值 = 解析器().解析("r50.15");

        Assert.Equal(软元素.R, 地址值.元素);
        Assert.Equal(15, 地址值.位序号);
        Assert.True(地址值.是字内位);
    }

    [Theory]
    [InlineData("D1210.16")]   // 位号只允许 0–15
    [InlineData("D1210.100")]
    [InlineData("D1210.")]
    [InlineData("M230.1")]     // 位元件不支持字内位写法
    public void 解析_非法字位写法_应失败(string 地址文本)
    {
        Assert.False(解析器().尝试解析(地址文本, out _));
    }

    [Fact]
    public void 解析_整字地址不应被判为字内位()
    {
        var 地址值 = 解析器().解析("D1210");

        Assert.False(地址值.是字内位);
        Assert.Equal(-1, 地址值.位序号);
    }

    // ── X / Y 的功能码分工（2026-10-17 现场第二次报障后修正）────────────

    /// <summary>
    /// X 是物理输入 → 离散输入区（FC02）；Y 是输出 → 线圈区（FC01）。
    /// <para><b>为什么单独立一个用例：</b>2026-10-17 之前 X 被配在线圈区，等于用 FC01 读输入区，
    /// 现场表现是"Y 一直正确、X（X0–X7 / X10–X17）一直对不上"，且两轮修复都没命中真因。
    /// 这条断言把"输入走 FC02、输出走 FC01"的分工钉死，改规格表前先看它红不红。</para>
    /// </summary>
    [Theory]
    [InlineData("X0", 63488)]
    [InlineData("X10", 63496)]    // 八进制：X10 = 序号 8
    [InlineData("X17", 63503)]    // 八进制：X17 = 序号 15
    public void 解析_X区_应按物理输入落到离散输入区(string 地址文本, int 期望地址)
    {
        var 地址值 = 解析器().解析(地址文本);

        Assert.Equal(软元素.X, 地址值.元素);
        Assert.Equal(期望地址, 地址值.Modbus地址);
        Assert.Equal(Modbus区域.离散输入, 地址值.区域);
    }

    [Theory]
    [InlineData("Y0", 64512)]
    [InlineData("Y10", 64520)]    // 八进制：Y10 = 序号 8
    public void 解析_Y区_应按输出落到线圈区(string 地址文本, int 期望地址)
    {
        var 地址值 = 解析器().解析(地址文本);

        Assert.Equal(软元素.Y, 地址值.元素);
        Assert.Equal(期望地址, 地址值.Modbus地址);
        Assert.Equal(Modbus区域.线圈, 地址值.区域);
    }

    // ── 写通道：读-改-写 ────────────────────────────────────────────────

    [Fact]
    public async Task 写D位_应只改目标位且保留同字其它位()
    {
        await using var 设备 = new 虚拟PLC设备(NullLogger<虚拟PLC设备>.Instance);
        await using var 客户端 = new 仿真Modbus客户端(设备, NullLogger<仿真Modbus客户端>.Instance);
        await 客户端.异步连接();

        var 写入器 = new 标签写入器(客户端, 仓储(), 解析器(), NullLogger<标签写入器>.Instance);

        // 命令字 D1210 预置：位 0（轴使能）与位 2（正向点动）为 1
        设备.内存.不安全设置保持寄存器(1210, 0b0000_0000_0000_0101);

        // 写 JogXForward（D1210.2）为 false：只清位 2，位 0 必须原样保留
        await 写入器.异步写入布尔("JogXForward", false);

        Assert.Equal(
            (ushort)0b0000_0000_0000_0001,
            设备.内存.不安全获取保持寄存器(1210));

        // 再写回 true：位 0 仍保留
        await 写入器.异步写入布尔("JogXForward", true);

        Assert.Equal(
            (ushort)0b0000_0000_0000_0101,
            设备.内存.不安全获取保持寄存器(1210));
    }

    [Fact]
    public async Task 写D位_同字两位互不干扰()
    {
        await using var 设备 = new 虚拟PLC设备(NullLogger<虚拟PLC设备>.Instance);
        await using var 客户端 = new 仿真Modbus客户端(设备, NullLogger<仿真Modbus客户端>.Instance);
        await 客户端.异步连接();

        var 写入器 = new 标签写入器(客户端, 仓储(), 解析器(), NullLogger<标签写入器>.Instance);

        // 正转 D1210.2 → 反转 D1210.3（正反转互锁：置一转必须清另一）
        await 写入器.异步写入布尔("JogXForward", true);
        await 写入器.异步写入布尔("JogXReverse", false);

        Assert.Equal(
            (ushort)0b0000_0000_0000_0100,
            设备.内存.不安全获取保持寄存器(1210));

        await 写入器.异步写入布尔("JogXForward", false);
        await 写入器.异步写入布尔("JogXReverse", true);

        Assert.Equal(
            (ushort)0b0000_0000_0000_1000,
            设备.内存.不安全获取保持寄存器(1210));
    }

    // ── 读通道：轮询按位解码 ────────────────────────────────────────────

    [Fact]
    public async Task 轮询_D位点应按位解码订阅值()
    {
        await using var 设备 = new 虚拟PLC设备(NullLogger<虚拟PLC设备>.Instance);
        await using var 客户端 = new 仿真Modbus客户端(设备, NullLogger<仿真Modbus客户端>.Instance);

        var 点位表 = 仓储();
        var 点动正转 = 点位表.查找("JogXForward");
        Assert.NotNull(点动正转);

        // 点动点是「命令」组（只写不采集），这里改到快组以便轮询引擎把它编进采集计划
        点动正转!.分组 = 标签分组.快;

        var 值列表 = new 标签值服务(NullLogger<标签值服务>.Instance);

        await using var 引擎 = new 轮询引擎(
            客户端,
            点位表,
            new IO点位服务(new 允许全部权限守卫(), NullLogger<IO点位服务>.Instance),
            解析器(),
            值列表,
            new 轮询选项 { 快间隔毫秒 = 50, 慢间隔毫秒 = 100, 事件间隔毫秒 = 100 },
            new PLC连接选项(),
            NullLogger<轮询引擎>.Instance);

        await 客户端.异步连接();
        await 引擎.异步启动();

        // 位 2 置 1 → 采集应发布 true（同字其它位为 0，不能误判成 true）
        设备.内存.不安全设置保持寄存器(1210, 0b0000_0000_0000_0000);
        await 异步等待直到(() => 值列表.尝试获取布尔("JogXForward", out var 值) && !值, "位为 0 时发布 false");

        设备.内存.不安全设置保持寄存器(1210, 0b0000_0000_0000_0100);
        await 异步等待直到(() => 值列表.尝试获取布尔("JogXForward", out var 值) && 值, "位 2 置 1 时发布 true");

        // 位 0 为 1、位 2 为 0：不能因为整字非零就判 true
        设备.内存.不安全设置保持寄存器(1210, 0b0000_0000_0000_0001);
        await 异步等待直到(() => 值列表.尝试获取布尔("JogXForward", out var 值) && !值, "只有其它位为 1 时应发布 false");
    }

    private static async Task 异步等待直到(Func<bool> 条件, string 描述, int 超时毫秒 = 8000)
    {
        var 截止时间 = Environment.TickCount64 + 超时毫秒;

        while (Environment.TickCount64 < 截止时间)
        {
            if (条件())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"等待超时：{描述}");
    }
}
