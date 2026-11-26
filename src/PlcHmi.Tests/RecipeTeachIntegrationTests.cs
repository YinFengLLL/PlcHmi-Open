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
/// 机种（配方）与示教功能的端到端集成测试。
///
/// <para>跑的是<b>真实配置文件</b>（<c>config/recipe-config.json</c>、<c>config/recipes.json</c>）
/// 加虚拟 PLC，覆盖：机种切换 → 参数下发 → 示教写入 → 读回校验 → 面启用拦截。</para>
///
/// <para>这些测试守住的是"地址算对了但落盘落错地方"这类问题 ——
/// 单元测试测不出，只有真跑一遍才会暴露。</para>
///
/// <para><b>本文件只放夹具与「机种 / 参数」侧用例</b>；示教点表侧用例在
/// <c>RecipeTeachIntegrationTests.TeachPoint.cs</c>（同类 partial part）。
/// 拆开是因为合并后单个文件超过了 400 行审查阈值，而 400 行以上很难在审查时维持完整上下文。</para>
/// </summary>
public partial class 配方示教集成测试
{
    // 轴参数区（2026-09-17 按 PLC 地址分配表）：轴 N 基址 = 1200 + 100×(N-1)，定位速度在偏移 2
    private const int 参数X基址 = 1400;
    private const int 参数Y基址 = 1302;
    private const int 参数Z基址 = 1402;
    private const int 参数ZZ基址 = 1502;
    private const int 参数GlueX基址 = 1602;

    /// <summary>用与生产完全同一套编解码解出 Float32（CDAB，低字在前）。</summary>
    private static float 解码浮点(ushort[] 寄存器列表, int 偏移量) =>
        (float)寄存器编解码器.解码(
            标签数据类型.Float32, 寄存器列表.AsSpan(偏移量, 2), 字顺序.小字节序);

    private static ServiceProvider 构建运行期()
    {
        var 设置 = 应用设置加载器.加载(out var 设置错误);
        var 配方配置 = 配方配置加载器.加载(out var 配方错误);

        Assert.Null(设置错误);
        Assert.Null(配方错误);

        // 仓库配置已切到真实 PLC 模式（现场部署），但测试必须保持密封：
        // 与 AlarmScanIntegrationTests 同款做法——无论仓库配置处于哪种模式，
        // 这里一律强制模拟器，集成测试永远不可能碰到真实 PLC。
        设置.PLC连接.使用模拟器 = true;

        设置.轮询.快间隔毫秒 = 50;
        设置.轮询.慢间隔毫秒 = 100;
        设置.轮询.事件间隔毫秒 = 100;

        var 服务集合 = new ServiceCollection();
        服务集合.AddLogging();
        服务集合.添加上位机服务(设置.PLC连接, 设置.轮询, 配方配置);

        // 本组用例不验证权限边界（那由 PermissionGuardTests 覆盖），以受信任身份运行
        服务集合.AddSingleton<权限守卫接口, 允许全部权限守卫>();

        return 服务集合.BuildServiceProvider(validateScopes: true);
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

            await Task.Delay(50);
        }

        Assert.Fail($"等待超时：{描述}");
    }

    /// <summary>读一个 Float32 寄存器对。</summary>
    private static async Task<float> 异步读取浮点(Modbus客户端接口 客户端, int 地址值)
    {
        var 寄存器列表 = await 客户端.异步读取保持寄存器(地址值, 2);
        return (float)寄存器编解码器.解码(标签数据类型.Float32, 寄存器列表, 字顺序.小字节序)!;
    }

    [Fact]
    public async Task 四轴当前位置应以浮点采集()
    {
        await using var 提供者 = 构建运行期();
        var 托管 = 提供者.GetRequiredService<IHostedService>();
        await 托管.StartAsync(CancellationToken.None);

        try
        {
            var 值列表 = 提供者.GetRequiredService<标签值服务接口>();

            await 异步等待直到(() => 值列表.获取新值("AxisXPosition").是否有效, "采到 X 轴位置");

            // 待机状态下四轴都在 0；关键是能正确解码（若字序错了这里会得到荒谬值）
            var 坐标X = 值列表.获取新值("AxisXPosition").缩放后新值;
            var 坐标Z = 值列表.获取新值("AxisZPosition").缩放后新值;

            Assert.InRange(坐标X, -999.99, 9999.99);
            Assert.InRange(坐标Z, -999.99, 9999.99);
        }
        finally
        {
            await 托管.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task 初始化应读到PLC的机种号并载入对应参数()
    {
        await using var 提供者 = 构建运行期();
        var 托管 = 提供者.GetRequiredService<IHostedService>();
        await 托管.StartAsync(CancellationToken.None);

        try
        {
            var 配方项 = 提供者.GetRequiredService<配方服务接口>();

            // 虚拟 PLC 初始机种号为 1
            Assert.Equal(1, 配方项.当前配方号);

            // 参数项来自配置：六轴各一项定位速度（*.*ManualSpeed 已废弃，改由点动档位寄存器决定）
            Assert.Equal(6, 配方项.参数规格.Count);
            Assert.Equal(6, 配方项.当前参数.Count);

            // 每项都应有值（缺失时用配置默认值补齐）
            foreach (var 规格项 in 配方项.参数规格)
            {
                Assert.True(配方项.当前参数.ContainsKey(规格项.键), $"缺少参数 {规格项.键}");
            }
        }
        finally
        {
            await 托管.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task 切换机种应下发该机种的区段快照并写机种号()
    {
        await using var 提供者 = 构建运行期();
        var 托管 = 提供者.GetRequiredService<IHostedService>();
        await 托管.StartAsync(CancellationToken.None);

        try
        {
            var 客户端 = 提供者.GetRequiredService<Modbus客户端接口>();
            var 配方项 = 提供者.GetRequiredService<配方服务接口>();

            // 1. 模拟现场：先在 PLC 的机种区段写入一组点位
            //    锁附第 1 点 X = 300.0（Float32 字序 CDAB，低字在前）+ 点胶指令码 5
            await 客户端.异步写入多个寄存器(2500, [(ushort)0x0000, (ushort)0x4396]);
            await 客户端.异步写入多个寄存器(3400, [(ushort)5]);

            // 2. 「保存机种」：把 PLC 区段读回来存成机种 3
            await 配方项.异步保存机种(3);

            var 快照 = 配方项.区段快照(3);
            Assert.Equal(0x4396, 快照[1]);              // D2501
            Assert.Equal(5, 快照[900]);                 // D3400（3400 - 2500）

            // 3. 清掉 PLC 区段，再切到机种 3 —— 切换应把快照整段写回 PLC
            await 客户端.异步写入多个寄存器(2500, [(ushort)0, (ushort)0]);
            await 客户端.异步写入多个寄存器(3400, [(ushort)0]);

            await 配方项.异步切换(3);

            Assert.Equal(3, 配方项.当前配方号);

            // 字序红线：写反了这里会读成 0
            var 点X = await 客户端.异步读取保持寄存器(2500, 2);
            Assert.Equal((ushort)0x0000, 点X[0]);   // 低字
            Assert.Equal((ushort)0x4396, 点X[1]);   // 高字 → 300.00
            Assert.Equal(300f, 解码浮点(点X, 0));

            var 指令码 = await 客户端.异步读取保持寄存器(3400, 1);
            Assert.Equal((ushort)5, 指令码[0]);

            // 机种号也应写进 PLC（D3330 落在机种区段内，切换末尾以目标机种号覆盖）
            var 配方号寄存器 = await 客户端.异步读取保持寄存器(3330, 1);
            Assert.Equal((ushort)3, 配方号寄存器[0]);
        }
        finally
        {
            await 托管.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task 切换机种不应改动全局参数()
    {
        await using var 提供者 = 构建运行期();
        var 托管 = 提供者.GetRequiredService<IHostedService>();
        await 托管.StartAsync(CancellationToken.None);

        try
        {
            var 客户端 = 提供者.GetRequiredService<Modbus客户端接口>();
            var 配方项 = 提供者.GetRequiredService<配方服务接口>();

            // 先把全局参数下发到 PLC，并记下参数区实际内容
            配方项.设置参数("X.LocateSpeed", 321);
            await 配方项.异步写入参数();

            var 切换前 = await 客户端.异步读取保持寄存器(参数X基址, 2);
            var 原号 = 配方项.当前配方号;

            // 切机种（该机种没有区段数据 → 允许切换、区段清零，但绝不该动参数区）
            await 配方项.异步切换(5);
            Assert.Equal(5, 配方项.当前配方号);

            var 切换后 = await 客户端.异步读取保持寄存器(参数X基址, 2);

            Assert.Equal(切换前, 切换后);
            Assert.Equal(321f, 解码浮点(切换后, 0));

            // 还原当前机种号，避免影响同集合里的其它用例
            await 配方项.异步切换(原号);
        }
        finally
        {
            await 托管.StopAsync(CancellationToken.None);
        }
    }
}
