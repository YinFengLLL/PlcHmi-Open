using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using 上位机.业务;
using 上位机.业务.配置;
using 上位机.业务.服务;
using 上位机.核心.抽象;
using 上位机.核心.模型;

namespace 上位机.上位机测试;

/// <summary>
/// 手动使能信号（M666）测试。
///
/// <para><b>现场口径（2026-10-15）：</b>手动使能开关打开时<b>持续</b>把 M666 置位，
/// 关闭时<b>持续</b>复位。所以这里不只断言"切换时写一次"，还断言
/// <b>PLC 程序把该位复位后会被自动补回</b>——只写一次的写法在这种场景下会与界面状态分叉：
/// 界面显示"已使能"、PLC 侧却认为"未使能"，操作员按点动没反应却查不出原因。</para>
/// </summary>
public class 手动使能信号测试
{
    private static ServiceProvider 构建运行期()
    {
        var 设置 = 应用设置加载器.加载(out _);
        var 配方配置 = 配方配置加载器.加载(out _);

        设置.PLC连接.使用模拟器 = true;
        设置.轮询.快间隔毫秒 = 50;
        设置.轮询.慢间隔毫秒 = 100;
        设置.轮询.事件间隔毫秒 = 100;

        var 服务集合 = new ServiceCollection();
        服务集合.AddLogging();
        服务集合.添加上位机服务(设置.PLC连接, 设置.轮询, 配方配置);
        服务集合.AddSingleton<权限守卫接口>(new 允许全部权限守卫());

        return 服务集合.BuildServiceProvider(validateScopes: true);
    }

    /// <summary>启动全部宿主服务（与真实程序一致：连接、轮询、报警扫描、使能保持……都跑起来）。</summary>
    private static async Task<IReadOnlyList<IHostedService>> 启动全部宿主服务(ServiceProvider 提供者)
    {
        var 服务列表 = 提供者.GetServices<IHostedService>().ToList();

        foreach (var 服务项 in 服务列表)
        {
            await 服务项.StartAsync(CancellationToken.None);
        }

        return 服务列表;
    }

    private static async Task 等待直到(Func<Task<bool>> 条件, string 描述, int 超时毫秒 = 4000)
    {
        var 截止时间 = Environment.TickCount64 + 超时毫秒;

        while (Environment.TickCount64 < 截止时间)
        {
            if (await 条件())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"等待超时：{描述}");
    }

    [Fact]
    public async Task 使能开关切换时应写入M666()
    {
        await using var 提供者 = 构建运行期();
        var 宿主服务 = await 启动全部宿主服务(提供者);

        try
        {
            var 客户端 = 提供者.GetRequiredService<Modbus客户端接口>();
            var 手动 = 提供者.GetRequiredService<手动控件服务接口>();

            await 等待直到(() => Task.FromResult(客户端.是否已连接), "PLC 连接建立");

            // 打开 → 置位
            手动.设置启用(true);
            await 手动.异步同步启用();

            var 开启 = await 客户端.异步读取线圈(手动使能寄存器.线圈, 1);
            Assert.True(开启[0], $"手动使能打开后 M{手动使能寄存器.线圈} 应为 ON");

            // 关闭 → 复位
            手动.设置启用(false);
            await 手动.异步同步启用();

            var 关闭 = await 客户端.异步读取线圈(手动使能寄存器.线圈, 1);
            Assert.False(关闭[0], $"手动使能关闭后 M{手动使能寄存器.线圈} 应为 OFF");
        }
        finally
        {
            foreach (var 服务项 in 宿主服务)
            {
                await 服务项.StopAsync(CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task 持续保持应把PLC侧复位的手动使能补回()
    {
        await using var 提供者 = 构建运行期();
        var 宿主服务 = await 启动全部宿主服务(提供者);

        try
        {
            var 客户端 = 提供者.GetRequiredService<Modbus客户端接口>();
            var 手动 = 提供者.GetRequiredService<手动控件服务接口>();

            await 等待直到(() => Task.FromResult(客户端.是否已连接), "PLC 连接建立");

            // 打开使能，但**不**主动同步：只有常驻保持服务在周期写
            手动.设置启用(true);

            await 等待直到(
                async () => (await 客户端.异步读取线圈(手动使能寄存器.线圈, 1))[0],
                "使能保持服务把 M666 置位");

            // 模拟 PLC 程序自己复位了该位（急停 / 模式切换 / 上电初始化）
            await 客户端.异步写入单个线圈(手动使能寄存器.线圈, false);

            await 等待直到(
                async () => (await 客户端.异步读取线圈(手动使能寄存器.线圈, 1))[0],
                "PLC 侧复位后由保持服务补回置位");

            // 关闭使能后同样要保持复位
            手动.设置启用(false);

            await 等待直到(
                async () => !(await 客户端.异步读取线圈(手动使能寄存器.线圈, 1))[0],
                "关闭使能后 M666 保持复位");
        }
        finally
        {
            foreach (var 服务项 in 宿主服务)
            {
                await 服务项.StopAsync(CancellationToken.None);
            }
        }
    }
}
