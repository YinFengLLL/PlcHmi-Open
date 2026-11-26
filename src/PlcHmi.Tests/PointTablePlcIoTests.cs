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
/// 点表的 PLC 侧 IO：2026-10-21 的两次现场改动。
///
/// <para><b>①「读取点表」（按需读取）：</b>机种区段 D2500–D3999 不再参与周期采集，
/// 要看 PLC 现状必须显式读取；读取只刷新内存，不写 PLC、不写机种快照。</para>
///
/// <para><b>②「参数写入」（原「下发点表」）：</b>把上位机当前 D2500–D3999 整段写进 PLC，
/// 并同时触发机种保存（等同「下发机种 + 保存机种」）。</para>
///
/// <para>单独成文件：<see cref="点表服务集成测试"/> 已接近 400 行规模门禁。</para>
/// </summary>
[Collection("config-files")]
public class 点表PLC读写测试
{
    private const int 点胶指令码基址 = 3400;
    private const int 点胶X基址 = 2700;
    private const int 点胶启动Z基址 = 2900;

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
        return (float)寄存器编解码器.解码(
            标签数据类型.Float32, 寄存器列表.AsSpan(0, 2), 字顺序.小字节序)!;
    }

    /// <summary>
    /// 机种区段不再自动回读之后，点表改为按「读取点表」显式读取：
    /// 要能拿到 PLC 里的真实值（含指令码），并且<b>只读不写</b>。
    /// </summary>
    [Fact]
    public async Task 读取点表应读回PLC当前值且不写PLC()
    {
        await using var 提供者 = 构建运行期();
        var 托管 = 提供者.GetRequiredService<IHostedService>();
        await 托管.StartAsync(CancellationToken.None);

        try
        {
            var 客户端 = 提供者.GetRequiredService<Modbus客户端接口>();
            var 点表 = 提供者.GetRequiredService<点表服务接口>();

            // PLC 里放一组"现场实际值"（与机种快照无关）
            await 客户端.异步写入多个寄存器(
                点胶X基址, 寄存器编解码器.编码(标签数据类型.Float32, 12.34, 字顺序.小字节序));
            await 客户端.异步写入多个寄存器(
                点胶启动Z基址, 寄存器编解码器.编码(标签数据类型.Float32, 56.78, 字顺序.小字节序));
            await 客户端.异步写入多个寄存器(点胶指令码基址, [3]);

            // 内存里先放点别的值，读取后必须被 PLC 值覆盖
            点表.设置点值("点胶", 0, "X", 1);
            点表.设置点值("点胶", 0, "指令码", 0);

            await 点表.异步读取点表("点胶");

            var 行项 = 点表.当前点表("点胶")[0];
            Assert.Equal(12.34, 行项.取("X"), 2);
            Assert.Equal(56.78, 行项.取("Z"), 2);
            Assert.Equal(3d, 行项.取("指令码"));

            // 只读不写：PLC 侧的值原封不动
            Assert.Equal(12.34f, await 异步读取浮点(客户端, 点胶X基址), 2);
            Assert.Equal(3, (await 客户端.异步读取保持寄存器(点胶指令码基址, 1))[0]);
        }
        finally
        {
            await 托管.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// 示教页「下发点表」改为「参数写入」：把上位机当前的 D2500–D3999 整段写进 PLC，
    /// 并同时触发机种保存。
    ///
    /// <para>要点：① 界面上刚改（还没下发）的坐标也要跟着写下去；
    /// ② 整段写入会先清零，PLC 里不属于当前机种的残留字必须消失；
    /// ③ 机种号写入 PLC；④ 写完后机种快照已保存（切页/重载仍能看到）。</para>
    /// </summary>
    [Fact]
    public async Task 参数写入应整段写入PLC并保存机种()
    {
        await using var 提供者 = 构建运行期();
        var 托管 = 提供者.GetRequiredService<IHostedService>();
        await 托管.StartAsync(CancellationToken.None);

        try
        {
            var 客户端 = 提供者.GetRequiredService<Modbus客户端接口>();
            var 点表 = 提供者.GetRequiredService<点表服务接口>();
            var 配方 = 提供者.GetRequiredService<配方服务接口>();

            // PLC 里放一段"机种区段内的残留数据"（点表之外的地址）：整段写入前会清零
            await 客户端.异步写入单个寄存器(3600, 999);

            // 只在界面上改内存，不点任何下发
            点表.设置点值("点胶", 1, "X", 111.5);

            await 点表.异步参数写入("点胶");

            // ① 点表值整段写进 PLC（第 2 点 X = D2702）
            Assert.Equal(111.5f, await 异步读取浮点(客户端, 点胶X基址 + 2), 2);

            // ② 机种区段内的残留字被清零（整段下发，而不是只写点表列）
            Assert.Equal(0, (await 客户端.异步读取保持寄存器(3600, 1))[0]);

            // ③ 机种号一并写入
            Assert.Equal(配方.当前配方号, (await 客户端.异步读取保持寄存器(3330, 1))[0]);

            // ④ 已触发机种保存：重新按机种快照装载仍能看到刚写的点
            await 点表.异步加载点表("点胶");
            Assert.Equal(111.5, 点表.当前点表("点胶")[1].取("X"), 2);
        }
        finally
        {
            await 托管.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// 2026-11-12 现场口径钉死：「参数写入」之后触发的保存，顺序必须是
    /// <b>① 先写入配方（整段下发）→ ② 再把 PLC 里的 D2500–D3999 读回来存成机种</b>。
    ///
    /// <para><b>怎么证明是"读回来的"：</b>拿机种快照里的非 0 项逐个回 PLC 核对。
    /// 若实现退回"只存内存快照"或"先读后写"，两边就会对不上（PLC 里会有快照没有的残留、
    /// 或快照里有 PLC 没有的值）。</para>
    /// </summary>
    [Fact]
    public async Task 参数写入后机种快照应与PLC读回逐项一致()
    {
        await using var 提供者 = 构建运行期();
        var 托管 = 提供者.GetRequiredService<IHostedService>();
        await 托管.StartAsync(CancellationToken.None);

        try
        {
            var 客户端 = 提供者.GetRequiredService<Modbus客户端接口>();
            var 点表 = 提供者.GetRequiredService<点表服务接口>();
            var 配方 = 提供者.GetRequiredService<配方服务接口>();

            // 机种区段里先放一段"不属于本机种"的残留：整段下发会清零，快照里就不该有它
            await 客户端.异步写入单个寄存器(3900, 777);

            点表.设置点值("点胶", 2, "X", 222.25);

            await 点表.异步参数写入("点胶");

            var 号 = 配方.当前配方号;
            var 快照 = 配方.区段快照(号);

            Assert.NotEmpty(快照);
            Assert.False(快照.ContainsKey(3900 - 机种区段.起始), "整段下发应先清零残留字");

            // 抽样核对：快照里的每一项都是 PLC 里真实存在的值
            var 抽样 = 快照.OrderBy(项 => 项.Key).Take(12).ToList();

            foreach (var (偏移, 值) in 抽样)
            {
                var PLC值 = await 客户端.异步读取保持寄存器(机种区段.起始 + 偏移, 1);
                Assert.Equal((ushort)值, PLC值[0]);
            }

            // 刚写的坐标在两边都到位（第 3 点 X = D2704）
            Assert.Equal(222.25f, await 异步读取浮点(客户端, 点胶X基址 + 4), 2);
        }
        finally
        {
            await 托管.StopAsync(CancellationToken.None);
        }
    }
}
