using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using 上位机.业务;
using 上位机.业务.配置;
using 上位机.业务.服务;
using 上位机.通信.Modbus;
using 上位机.通信.汇川;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;
using 上位机.上位机测试.替身;

namespace 上位机.上位机测试;

/// <summary>
/// 锁附页「当前浮高」（2026-10-24 现场要求）的回归。
///
/// <para><b>守的是什么：</b>浮高来自一个独立的 REAL 地址（缺省 D240），既不在标签表里、
/// 也不参与轮询。它是手动区显示项与「保存坐标点」的共同来源，因此：<br/>
/// ① 读值按 Float32 两字解码；② 未配置地址 / 读不到时返回 <c>null</c>，绝不返回 0 冒充读数；<br/>
/// ③ 地址可由管理员在「编辑地址」里改，保存后立即生效。</para>
/// </summary>
[Collection("config-files")]
public class 当前浮高测试
{
    private static ServiceProvider 构建运行期()
    {
        var 设置 = 应用设置加载器.加载(out var 设置错误);
        var 配方配置 = 配方配置加载器.加载(out var 配方错误);

        Assert.Null(设置错误);
        Assert.Null(配方错误);

        设置.PLC连接.使用模拟器 = true;
        设置.轮询.快间隔毫秒 = 50;
        设置.轮询.慢间隔毫秒 = 100;
        设置.轮询.事件间隔毫秒 = 100;

        var 服务集合 = new ServiceCollection();
        服务集合.AddLogging();
        服务集合.添加上位机服务(设置.PLC连接, 设置.轮询, 配方配置);
        服务集合.AddSingleton<权限守卫接口, 允许全部权限守卫>();

        return 服务集合.BuildServiceProvider(validateScopes: true);
    }

    private static 页面地址服务 新页面地址服务(Modbus客户端接口 连接)
    {
        var 解析器 = new 汇川地址解析器(PLC系列.小型PLC);

        return new 页面地址服务(
            连接,
            new 标签写入器(连接, new 标签仓储(NullLogger<标签仓储>.Instance), 解析器,
                NullLogger<标签写入器>.Instance),
            配方配置加载器.加载(out _),
            new 允许全部权限守卫(),
            NullLogger<页面地址服务>.Instance);
    }

    [Fact]
    public async Task 应能读到REAL型的当前浮高()
    {
        await using var 提供者 = 构建运行期();
        var 托管 = 提供者.GetRequiredService<IHostedService>();
        await 托管.StartAsync(CancellationToken.None);

        try
        {
            var 客户端 = 提供者.GetRequiredService<Modbus客户端接口>();
            var 点表 = 提供者.GetRequiredService<点表服务接口>();

            await 客户端.异步写入多个寄存器(
                240, 寄存器编解码器.编码(标签数据类型.Float32, 3.25, 字顺序.小字节序));

            var 浮高 = await 点表.异步读取当前浮高("锁附");
            Assert.NotNull(浮高);
            Assert.Equal(3.25, 浮高!.Value, 3);

            // 点胶表没有浮高地址 → null（而不是 0）
            Assert.Null(await 点表.异步读取当前浮高("点胶"));
        }
        finally
        {
            await 托管.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// 2026-10-24 现场口径：浮高只属于锁附示教。
    /// <b>点胶示教的「保存坐标点」仍是写当前 X / Y / Z 轴位置</b>，既不读 D240，也不写浮高。
    /// </summary>
    [Fact]
    public async Task 点胶保存坐标点仍写XYZ且不碰浮高()
    {
        await using var 提供者 = 构建运行期();
        var 托管 = 提供者.GetRequiredService<IHostedService>();
        await 托管.StartAsync(CancellationToken.None);

        try
        {
            var 客户端 = 提供者.GetRequiredService<Modbus客户端接口>();
            var 点表 = 提供者.GetRequiredService<点表服务接口>();

            // 点胶表没有浮高地址
            Assert.Equal(0, 点表.表("点胶").当前浮高地址);
            Assert.Null(点表.表("点胶").查找列("浮高"));

            // PLC 里放一个浮高值：点胶示教不应去读它、更不该把它写进点胶点表
            await 客户端.异步写入多个寄存器(
                240, 寄存器编解码器.编码(标签数据类型.Float32, 9.5, 字顺序.小字节序));

            点表.设置点值("点胶", 0, "指令码", 5);
            await 点表.异步示教当前位置("点胶", 0);

            // X / Y / Z 三个坐标列照常按当前位置写入（模拟器各轴 0）
            Assert.Equal(0f, await 异步读取浮点(客户端, 2700), 2);    // X
            Assert.Equal(0f, await 异步读取浮点(客户端, 2800), 2);    // Y
            Assert.Equal(0f, await 异步读取浮点(客户端, 2900), 2);    // Z

            // 指令码保留，D240 原封不动
            Assert.Equal(5d, 点表.当前点表("点胶")[0].取("指令码"));
            Assert.Equal(9.5f, await 异步读取浮点(客户端, 240), 2);
        }
        finally
        {
            await 托管.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<float> 异步读取浮点(Modbus客户端接口 客户端, int 地址值)
    {
        var 寄存器列表 = await 客户端.异步读取保持寄存器(地址值, 2);
        return (float)寄存器编解码器.解码(
            标签数据类型.Float32, 寄存器列表.AsSpan(0, 2), 字顺序.小字节序)!;
    }

    /// <summary>
    /// 「编辑地址」里必须有一行可改的「当前浮高」，保存后地址立即换；
    /// 它是固定元件：不可删、类型固定 REAL。
    /// </summary>
    [Fact]
    public async Task 当前浮高地址应可在编辑地址中修改()
    {
        var 配方路径 = 配置路径.配方配置;
        var 原始 = await File.ReadAllTextAsync(配方路径);

        try
        {
            await using var 客户端 = new 替身Modbus客户端();
            var 服务实例 = 新页面地址服务(客户端);

            var 项集合 = 服务实例.收集点表页("锁附").ToList();
            var 浮高项 = 项集合.First(项 => 项.类别 == "手动区");

            Assert.Equal("当前浮高", 浮高项.键);
            Assert.Equal("D240", 浮高项.地址文本);
            Assert.Equal(标签数据类型.Float32.ToString(), 浮高项.数据类型文本);
            Assert.False(浮高项.可删除);

            浮高项.地址文本 = "D260";

            await 服务实例.异步保存点表页("锁附", new 地址编辑请求
            {
                标题 = "锁附示教",
                默认分组 = "锁附",
                项集合 = 项集合,
            });

            // 落盘后重新加载：地址已改成 D260
            Assert.Equal(260, 配方配置加载器.加载(out _).点表.查找("锁附")!.当前浮高地址);
        }
        finally
        {
            await File.WriteAllTextAsync(配方路径, 原始);
        }
    }
}
