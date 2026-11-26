using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using 上位机.业务;
using 上位机.业务.配置;
using 上位机.业务.服务;
using 上位机.通信.Modbus;
using 上位机.核心.抽象;
using 上位机.核心.模型;

namespace 上位机.上位机测试;

/// <summary>
/// 机种切换回归（2026-10-21 现场报障）。
///
/// <para><b>现场现象：</b>点「切换」到一个刚建、还没保存过数据的机种，弹"机种 N 还没有数据。
/// 请先在示教页调好点位…"，<b>永远切不过去</b>。空机种本来就是新建机种的常态，
/// 切换时必须能落地：清空机种区段 + 写机种号，然后去示教。</para>
///
/// <para>手动「下发机种」仍然拒绝空机种——那等于一声不响把区段清空，必须走「切换」这条确认路径。</para>
/// </summary>
[Collection("config-files")]
public class 机种切换测试
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

    [Fact]
    public async Task 切到没有数据的机种应成功并清空机种区段()
    {
        await using var 提供者 = 构建运行期();
        var 托管 = 提供者.GetRequiredService<IHostedService>();
        await 托管.StartAsync(CancellationToken.None);

        var 客户端 = 提供者.GetRequiredService<Modbus客户端接口>();
        var 配方 = 提供者.GetRequiredService<配方服务接口>();

        var 原号 = 配方.当前配方号;
        var 目标号 = 原号 == 20 ? 19 : 20;

        try
        {
            // 目标机种确保是"没有数据"的空机种
            await 配方.异步写入机种快照(目标号, new Dictionary<int, int>());
            Assert.Empty(配方.区段快照(目标号));

            // PLC 里先放旧机种的残留数据（机种区段内）
            await 客户端.异步写入多个寄存器(2500, [1, 2, 3]);

            await 配方.异步切换(目标号);

            // 切换生效：内存与 PLC 的机种号都变了
            Assert.Equal(目标号, 配方.当前配方号);
            Assert.Equal(目标号, (await 客户端.异步读取保持寄存器(3330, 1))[0]);

            // 空机种下发 = 区段整段清零（不留上一机种的点位）
            var 区段首字 = await 客户端.异步读取保持寄存器(2500, 3);
            Assert.All(区段首字, 值 => Assert.Equal(0, 值));
        }
        finally
        {
            // 还原当前机种号，避免影响同集合里的其它用例
            if (配方.当前配方号 != 原号)
            {
                await 配方.异步切换(原号);
            }

            await 托管.StopAsync(CancellationToken.None);
        }
    }
}
