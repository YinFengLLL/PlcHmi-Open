using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using 上位机.业务;
using 上位机.业务.配置;
using 上位机.业务.服务;
using 上位机.通信.Modbus;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.上位机测试;

/// <summary>
/// 点表服务的端到端集成测试（真实配置 + 虚拟 PLC）。
///
/// <para>覆盖：锁附/点胶点表按列分区写入 → 读回校验 → 指令码同步 → 越界拦截 →
/// 坐标示教只写选中点 → <b>下发/示教后自动并入机种快照</b>。</para>
///
/// <para>归入 <c>config-files</c> 集合：下发与示教现在会写 <c>recipes.json</c>（机种快照），
/// 与其它动配置文件（含备份/还原）的用例并行会互相踩。</para>
/// </summary>
[Collection("config-files")]
public class 点表服务集成测试
{
    private const int 锁附X基址 = 2500;
    private const int 锁附Y基址 = 2560;
    private const int 点胶指令码基址 = 3400;
    private const int 点胶X基址 = 2700;
    private const int 点胶启动Z基址 = 2900;

    private static float 解码浮点(ushort[] 寄存器列表) =>
        (float)寄存器编解码器.解码(
            标签数据类型.Float32, 寄存器列表.AsSpan(0, 2), 字顺序.小字节序)!;

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

    private static async Task<float> 异步读取浮点(Modbus客户端接口 客户端, int 地址值)
    {
        var 寄存器列表 = await 客户端.异步读取保持寄存器(地址值, 2);
        return 解码浮点(寄存器列表);
    }

    [Fact]
    public async Task 锁附点表应按约定地址写入并能读回()
    {
        await using var 提供者 = 构建运行期();
        var 托管 = 提供者.GetRequiredService<IHostedService>();
        await 托管.StartAsync(CancellationToken.None);

        try
        {
            var 客户端 = 提供者.GetRequiredService<Modbus客户端接口>();
            var 点表 = 提供者.GetRequiredService<点表服务接口>();

            Assert.Equal(30, 点表.点数("锁附"));

            // 2026-11-18 现场要求：点胶扩到 50 点（锁附仍 30 点）
            Assert.Equal(50, 点表.点数("点胶"));

            点表.设置点值("锁附", 1, "X", 123.45);
            点表.设置点值("锁附", 1, "Y", -67.89);

            await 点表.异步写入点表("锁附");

            Assert.Equal(123.45f, await 异步读取浮点(客户端, 锁附X基址 + 2), 2);
            Assert.Equal(-67.89f, await 异步读取浮点(客户端, 锁附Y基址 + 2), 2);

            // 未示教的第 1 点保持 0
            Assert.Equal(0f, await 异步读取浮点(客户端, 锁附X基址), 2);
        }
        finally
        {
            await 托管.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task 点胶点表应含指令码并写到达约定地址()
    {
        await using var 提供者 = 构建运行期();
        var 托管 = 提供者.GetRequiredService<IHostedService>();
        await 托管.StartAsync(CancellationToken.None);

        try
        {
            var 客户端 = 提供者.GetRequiredService<Modbus客户端接口>();
            var 点表 = 提供者.GetRequiredService<点表服务接口>();

            点表.设置点值("点胶", 0, "指令码", 7);
            点表.设置点值("点胶", 0, "X", 111.5);
            点表.设置点值("点胶", 0, "Y", 222.5);
            点表.设置点值("点胶", 0, "Z", 333.5);

            await 点表.异步写入点表("点胶");

            var 指令码 = await 客户端.异步读取保持寄存器(点胶指令码基址, 1);
            Assert.Equal((ushort)7, 指令码[0]);

            Assert.Equal(111.5f, await 异步读取浮点(客户端, 点胶X基址), 2);
            Assert.Equal(222.5f, await 异步读取浮点(客户端, 点胶X基址 + 100), 2);    // Y = 2800
            Assert.Equal(333.5f, await 异步读取浮点(客户端, 点胶启动Z基址), 2);

            // 第二点的指令码应落在 D3401（步长 1 字）
            点表.设置点值("点胶", 1, "指令码", 9);
            await 点表.异步写入点表("点胶");
            var 第二指令码 = await 客户端.异步读取保持寄存器(点胶指令码基址 + 1, 1);
            Assert.Equal((ushort)9, 第二指令码[0]);
        }
        finally
        {
            await 托管.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// 2026-10-21 现场要求：点胶页指令码改为下拉选单，选中即写该点的指令码字
    /// （第 2 点选「直线」= 码值 2 → D3401），点表显示文字，且切页回来仍在。
    /// </summary>
    [Fact]
    public async Task 选中指令码应立即写入对应点地址并保存到机种()
    {
        await using var 提供者 = 构建运行期();
        var 托管 = 提供者.GetRequiredService<IHostedService>();
        await 托管.StartAsync(CancellationToken.None);

        try
        {
            var 客户端 = 提供者.GetRequiredService<Modbus客户端接口>();
            var 点表 = 提供者.GetRequiredService<点表服务接口>();

            await 点表.异步写入指令码("点胶", 1, 2);

            // 第 2 点（0 基序号 1）→ D3401 = 2
            var 寄存器 = await 客户端.异步读取保持寄存器(点胶指令码基址 + 1, 1);
            Assert.Equal((ushort)2, 寄存器[0]);

            // 内存点表同步（界面「指令码」列显示「直线」）
            var 码 = 点表.当前点表("点胶")[1].取("指令码");
            Assert.Equal(2d, 码);
            Assert.Equal("直线", 指令码定义.显示文本(码));

            // 只动指令码：第 2 点的坐标没被顺带写下去
            Assert.Equal(0f, await 异步读取浮点(客户端, 点胶X基址 + 2), 2);

            // 并入机种快照：切页再进入仍能看到这个指令码
            await 点表.异步加载点表("点胶");
            Assert.Equal(2d, 点表.当前点表("点胶")[1].取("指令码"));
        }
        finally
        {
            await 托管.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task 坐标示教应只写选中点并保留指令码()
    {
        await using var 提供者 = 构建运行期();
        var 托管 = 提供者.GetRequiredService<IHostedService>();
        await 托管.StartAsync(CancellationToken.None);

        try
        {
            var 客户端 = 提供者.GetRequiredService<Modbus客户端接口>();
            var 点表 = 提供者.GetRequiredService<点表服务接口>();

            // 第 6 点预置指令码 9；示教位置为 0，不应把指令码抹掉
            点表.设置点值("点胶", 5, "指令码", 9);
            await 点表.异步示教当前位置("点胶", 5);

            var 指令码 = await 客户端.异步读取保持寄存器(点胶指令码基址 + 5, 1);
            Assert.Equal((ushort)9, 指令码[0]);

            // 第 1 点的指令码应保持 0（没被误写）
            var 首个指令码 = await 客户端.异步读取保持寄存器(点胶指令码基址, 1);
            Assert.Equal((ushort)0, 首个指令码[0]);
        }
        finally
        {
            await 托管.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// 2026-10-24 现场要求：锁附页「保存坐标点」写 X / Y 轴当前位置，<b>以及当前浮高</b>
    /// （浮高来自测高地址 D240，不是 Z 轴位置，也不是上一行的人工填写值）。
    /// </summary>
    [Fact]
    public async Task 锁附保存坐标点应写XY与当前浮高()
    {
        await using var 提供者 = 构建运行期();
        var 托管 = 提供者.GetRequiredService<IHostedService>();
        await 托管.StartAsync(CancellationToken.None);

        try
        {
            var 客户端 = 提供者.GetRequiredService<Modbus客户端接口>();
            var 点表 = 提供者.GetRequiredService<点表服务接口>();

            // 旧值是 12.5：示教后必须被"当前浮高"覆盖，而不是保留
            点表.设置点值("锁附", 0, "浮高", 12.5);
            点表.设置点值("锁附", 0, "X", 99);

            // PLC 里的当前浮高 = 6.75（REAL 两字，CDAB）
            await 客户端.异步写入多个寄存器(
                240, 寄存器编解码器.编码(标签数据类型.Float32, 6.75, 字顺序.小字节序));

            // 模拟器里各轴位置都是 0
            await 点表.异步示教当前位置("锁附", 0);

            var 行项 = 点表.当前点表("锁附")[0];
            Assert.Equal(6.75, 行项.取("浮高"), 2);   // 当前浮高写进该点
            Assert.Equal(0, 行项.取("X"), 2);          // X 由当前位置覆盖

            // 浮高落到该点的浮高列（第 1 点 = D2620）
            Assert.Equal(6.75f, await 异步读取浮点(客户端, 2620), 2);
        }
        finally
        {
            await 托管.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task 点胶保存坐标点应写XYZ并保留指令码()
    {
        await using var 提供者 = 构建运行期();
        var 托管 = 提供者.GetRequiredService<IHostedService>();
        await 托管.StartAsync(CancellationToken.None);

        try
        {
            var 客户端 = 提供者.GetRequiredService<Modbus客户端接口>();
            var 点表 = 提供者.GetRequiredService<点表服务接口>();

            点表.设置点值("点胶", 0, "指令码", 5);
            await 点表.异步示教当前位置("点胶", 0);

            // 指令码不受影响，X/Y/Z 三个坐标列都参与了写入
            Assert.Equal(5d, 点表.当前点表("点胶")[0].取("指令码"));

            var 指令码 = await 客户端.异步读取保持寄存器(点胶指令码基址, 1);
            Assert.Equal((ushort)5, 指令码[0]);

            Assert.Equal(0f, await 异步读取浮点(客户端, 点胶X基址), 2);
            Assert.Equal(0f, await 异步读取浮点(客户端, 点胶X基址 + 100), 2);    // Y = 2800
            Assert.Equal(0f, await 异步读取浮点(客户端, 点胶启动Z基址), 2);
        }
        finally
        {
            await 托管.StopAsync(CancellationToken.None);
        }
    }

    // ── 2026-10-16 现场问题回归：下发/示教后切页再进入数据必须还在 ──────

    /// <summary>
    /// 现场报障：锁附/点胶示教页「下发点表」后切换页面再回来，点表数据不见了，
    /// 必须先去机种管理页点「保存机种」才显示。
    ///
    /// <para><b>真因</b>：页面进入时是从<b>机种快照</b>（recipes.json）装载的，
    /// 而旧实现"下发点表"只写 PLC、不动机种快照——于是先切页再回来读到的还是旧快照。
    /// 现在下发/示教写完 PLC 后会把本表数据并入当前机种快照。</para>
    /// </summary>
    [Fact]
    public async Task 下发点表后重新进入页面应仍能看到数据()
    {
        await using var 提供者 = 构建运行期();
        var 托管 = 提供者.GetRequiredService<IHostedService>();
        await 托管.StartAsync(CancellationToken.None);

        try
        {
            var 点表 = 提供者.GetRequiredService<点表服务接口>();

            点表.设置点值("锁附", 2, "X", 111.11);
            点表.设置点值("锁附", 2, "Y", -222.22);

            await 点表.异步写入点表("锁附");

            // 复刻"切页 → 再进入"：页面进入会按机种快照重新装载
            await 点表.异步加载点表("锁附");

            var 行项 = 点表.当前点表("锁附")[2];
            Assert.Equal(111.11, 行项.取("X"), 2);
            Assert.Equal(-222.22, 行项.取("Y"), 2);
        }
        finally
        {
            await 托管.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task 坐标示教后重新进入页面也应仍能看到该点()
    {
        await using var 提供者 = 构建运行期();
        var 托管 = 提供者.GetRequiredService<IHostedService>();
        await 托管.StartAsync(CancellationToken.None);

        try
        {
            var 客户端 = 提供者.GetRequiredService<Modbus客户端接口>();
            var 点表 = 提供者.GetRequiredService<点表服务接口>();

            // 用"当前浮高"验证这一点确实存进了快照（示教后会写浮高列）
            await 客户端.异步写入多个寄存器(
                240, 寄存器编解码器.编码(标签数据类型.Float32, 7.5, 字顺序.小字节序));

            await 点表.异步示教当前位置("锁附", 3);

            await 点表.异步加载点表("锁附");

            Assert.Equal(7.5, 点表.当前点表("锁附")[3].取("浮高"), 2);
        }
        finally
        {
            await 托管.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task 超出范围的坐标应拒绝下发()
    {
        await using var 提供者 = 构建运行期();
        var 托管 = 提供者.GetRequiredService<IHostedService>();
        await 托管.StartAsync(CancellationToken.None);

        try
        {
            var 点表 = 提供者.GetRequiredService<点表服务接口>();

            点表.设置点值("锁附", 0, "X", 99999);
            点表.设置点值("锁附", 0, "Y", 10);

            var 异常 = await Assert.ThrowsAsync<InvalidOperationException>(() => 点表.异步写入点表("锁附"));
            Assert.Contains("超出坐标范围", 异常.Message);
        }
        finally
        {
            await 托管.StopAsync(CancellationToken.None);
        }
    }
}
