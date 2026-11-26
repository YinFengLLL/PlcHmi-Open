using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using 上位机.业务;
using 上位机.业务.配置;
using 上位机.业务.服务;
using 上位机.核心.抽象;
using 上位机.核心.模型;

namespace 上位机.上位机测试;

/// <summary>
/// 回零 / 点动安全与点表权限边界的测试。
///
/// <para>回零与点动都是真实机械动作：未使能时必须拒绝；停止点动则必须无条件生效
/// （否则轴会一直动下去）。点表示教属工程师及以上，越权必须被服务层拦下。</para>
/// </summary>
public class 点表安全与权限测试
{
    /// <summary>全部拒绝的权限守卫（仅测试用）。</summary>
    private sealed class 拒绝全部权限守卫 : 权限守卫接口
    {
        public void 要求(权限 权限项) =>
            throw new UnauthorizedAccessException($"测试：缺少「{权限项}」权限。");
    }

    private static ServiceProvider 构建运行期(权限守卫接口 守卫)
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
        服务集合.AddSingleton(守卫);

        return 服务集合.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public async Task 未使能时回零应被拒绝()
    {
        await using var 提供者 = 构建运行期(new 允许全部权限守卫());
        var 托管 = 提供者.GetRequiredService<IHostedService>();
        await 托管.StartAsync(CancellationToken.None);

        try
        {
            var 手动 = 提供者.GetRequiredService<手动控件服务接口>();

            // 默认未使能
            手动.设置启用(false);

            await Assert.ThrowsAsync<InvalidOperationException>(() => 手动.异步回零("X"));
        }
        finally
        {
            await 托管.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task 未使能时点动应被拒绝()
    {
        await using var 提供者 = 构建运行期(new 允许全部权限守卫());
        var 托管 = 提供者.GetRequiredService<IHostedService>();
        await 托管.StartAsync(CancellationToken.None);

        try
        {
            var 手动 = 提供者.GetRequiredService<手动控件服务接口>();
            手动.设置启用(false);

            await Assert.ThrowsAsync<InvalidOperationException>(() => 手动.异步点动轴("X", 1));
        }
        finally
        {
            await 托管.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task 停止点动应无条件生效()
    {
        await using var 提供者 = 构建运行期(new 允许全部权限守卫());
        var 托管 = 提供者.GetRequiredService<IHostedService>();
        await 托管.StartAsync(CancellationToken.None);

        try
        {
            var 手动 = 提供者.GetRequiredService<手动控件服务接口>();

            // 刻意不使能：松开即停是安全出口，不得因未使能而拒绝
            手动.设置启用(false);

            await 手动.异步停止点动轴("X");
            await 手动.异步停止点动轴("GlueX");
        }
        finally
        {
            await 托管.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task 未配置的轴回零应被拒绝()
    {
        await using var 提供者 = 构建运行期(new 允许全部权限守卫());
        var 托管 = 提供者.GetRequiredService<IHostedService>();
        await 托管.StartAsync(CancellationToken.None);

        try
        {
            var 手动 = 提供者.GetRequiredService<手动控件服务接口>();
            手动.设置启用(true);
            await 手动.异步同步启用();

            var 异常 = await Assert.ThrowsAsync<InvalidOperationException>(() => 手动.异步回零("无此轴"));
            Assert.Contains("回零", 异常.Message);
        }
        finally
        {
            await 托管.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task 动作线圈读取应与写入互逆且不受使能限制()
    {
        await using var 提供者 = 构建运行期(new 允许全部权限守卫());
        var 托管 = 提供者.GetRequiredService<IHostedService>();
        await 托管.StartAsync(CancellationToken.None);

        try
        {
            var 手动 = 提供者.GetRequiredService<手动控件服务接口>();
            var 客户端 = 提供者.GetRequiredService<Modbus客户端接口>();

            var 截止 = Environment.TickCount64 + 5000;
            while (!客户端.是否已连接 && Environment.TickCount64 < 截止)
            {
                await Task.Delay(50);
            }

            手动.设置启用(true);

            // 切换型动作按钮的"激活 / 取消激活"就是"写一次 + 回读一次"：
            // 写进去什么，读出来必须是什么（否则按钮状态会长期说反话）
            await 手动.异步写入线圈("M1100", true);
            Assert.True(await 手动.异步读取线圈("M1100"));

            await 手动.异步写入线圈("M1100", false);
            Assert.False(await 手动.异步读取线圈("M1100"));

            // 只读不受手动使能限制：示教页进入时要回读按钮真实状态，此时使能常常还没开
            手动.设置启用(false);
            Assert.False(await 手动.异步读取线圈("M1100"));
        }
        finally
        {
            await 托管.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task 页面上下文应写入D10与两页各自的点码()
    {
        await using var 提供者 = 构建运行期(new 允许全部权限守卫());
        var 托管 = 提供者.GetRequiredService<IHostedService>();
        await 托管.StartAsync(CancellationToken.None);

        try
        {
            var 客户端 = 提供者.GetRequiredService<Modbus客户端接口>();
            var 上下文 = 提供者.GetRequiredService<页面上下文服务接口>();

            var 截止 = Environment.TickCount64 + 5000;
            while (!客户端.是否已连接 && Environment.TickCount64 < 截止)
            {
                await Task.Delay(50);
            }

            // 页码 = 当前页面（主监控 1、按侧栏顺序递增）；点码 = 各页选中项（0 基）
            await 上下文.异步写入页码(4);
            await 上下文.异步写入点号(页面上下文寄存器.锁附示教点码, 7);
            await 上下文.异步写入点号(页面上下文寄存器.点胶示教点码, 12);
            await 上下文.异步写入点号(页面上下文寄存器.标定其它点点码, 3);

            var 页 = await 客户端.异步读取保持寄存器(页面上下文寄存器.页码, 1);
            var 锁附 = await 客户端.异步读取保持寄存器(页面上下文寄存器.锁附示教点码, 1);
            var 点胶 = await 客户端.异步读取保持寄存器(页面上下文寄存器.点胶示教点码, 1);
            var 标定 = await 客户端.异步读取保持寄存器(页面上下文寄存器.标定其它点点码, 1);

            Assert.Equal((ushort)4, 页[0]);
            Assert.Equal((ushort)7, 锁附[0]);
            Assert.Equal((ushort)12, 点胶[0]);
            Assert.Equal((ushort)3, 标定[0]);

            // "最后值覆盖"语义：再选别的点应直接覆盖，不累加；且不得动其它页的字
            await 上下文.异步写入点号(页面上下文寄存器.锁附示教点码, 0);

            var 锁附0 = await 客户端.异步读取保持寄存器(页面上下文寄存器.锁附示教点码, 1);
            var 点胶不变 = await 客户端.异步读取保持寄存器(页面上下文寄存器.点胶示教点码, 1);
            var 标定不变 = await 客户端.异步读取保持寄存器(页面上下文寄存器.标定其它点点码, 1);

            Assert.Equal((ushort)0, 锁附0[0]);
            Assert.Equal((ushort)12, 点胶不变[0]);
            Assert.Equal((ushort)3, 标定不变[0]);
        }
        finally
        {
            await 托管.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task 缺少编辑示教权限时点表示教应被拒绝()
    {
        await using var 提供者 = 构建运行期(new 拒绝全部权限守卫());
        var 托管 = 提供者.GetRequiredService<IHostedService>();
        await 托管.StartAsync(CancellationToken.None);

        try
        {
            var 点表 = 提供者.GetRequiredService<点表服务接口>();

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => 点表.异步示教当前位置("锁附", 0));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => 点表.异步写入点表("锁附"));
        }
        finally
        {
            await 托管.StopAsync(CancellationToken.None);
        }
    }
}
