using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using 上位机.业务;
using 上位机.业务.配置;
using 上位机.业务.服务;
using 上位机.通信.Modbus;
using 上位机.核心.抽象;
using 上位机.核心.枚举;

namespace 上位机.上位机测试;

/// <summary>
/// 机种区段（D2500–D3999）不参与周期采集（2026-10-21 现场要求）。
///
/// <para><b>为什么要钉住：</b>点表 / 单点工艺参数 / 机种快照都落在这个区段里。
/// 若轮询仍把它们读进 <c>标签值服务</c>，示教页的数据会"跟着 PLC 实时跳"，
/// 与操作员正在编辑的值互相踩——现场看到的正是"我填的值自己变了"。</para>
/// </summary>
[Collection("config-files")]
public class 机种区段轮询测试
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
    public async Task 机种区段的点位不应被周期采集()
    {
        await using var 提供者 = 构建运行期();
        var 托管 = 提供者.GetRequiredService<IHostedService>();
        await 托管.StartAsync(CancellationToken.None);

        try
        {
            var 客户端 = 提供者.GetRequiredService<Modbus客户端接口>();
            var 值列表 = 提供者.GetRequiredService<标签值服务接口>();

            // 区段外：D1104 目标产量（慢分组；模拟器只在初始化时写一次，循环里不改它）
            // 区段内：D3331 机种总数（慢分组）
            await 客户端.异步写入多个寄存器(1104, [(ushort)4321, (ushort)0]);
            await 客户端.异步写入单个寄存器(3331, 7);

            await Task.Delay(600);

            // 区段外照常采集
            Assert.True(值列表.尝试获取双精度("TargetCount", out var 目标产量));
            Assert.Equal(4321, 目标产量);

            // 区段内一个都不读：标签值服务里根本没有这个点（不是"读到 0"）
            Assert.False(值列表.尝试获取双精度("RecipeCount", out _));
            Assert.False(值列表.尝试获取双精度("TeachIndex", out _));
            Assert.False(值列表.尝试获取双精度("RecipeNo", out _));
        }
        finally
        {
            await 托管.StopAsync(CancellationToken.None);
        }
    }
}
