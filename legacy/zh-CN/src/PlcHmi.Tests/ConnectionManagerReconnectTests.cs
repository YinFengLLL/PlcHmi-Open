using Microsoft.Extensions.Logging.Abstractions;
using 上位机.业务.服务;
using 上位机.核心.枚举;
using 上位机.核心.模型;
using 上位机.上位机测试.替身;

namespace 上位机.上位机测试;

/// <summary>
/// 连接管理器断线重连的自动化验证。
///
/// <para><b>为什么补这一组：</b>指数退避逻辑此前只有人工推理，没有可执行断言
/// （见 <c>docs/01</c> 风险表的"断线后无法自愈 —— 待实测"）。
/// 断线自愈是无人值守工控现场的底线能力，必须由测试锁定，而不是靠"看着像对"。</para>
///
/// <para>退避间隔统一调小（20ms / 80ms），使测试在数百毫秒内完成，不真等 1s～30s。</para>
/// </summary>
public class 连接管理器重连测试
{
    private static PLC连接选项 快选项(bool 自动重连, int 初始毫秒 = 20, int 最大毫秒 = 80) => new()
    {
        自动重连 = 自动重连,
        重连初始延迟毫秒 = 初始毫秒,
        重连最大延迟毫秒 = 最大毫秒,
        心跳间隔毫秒 = 1000,   // 本组测试不走到心跳：连接始终失败或刚成功即结束
    };

    /// <summary>线程安全的状态记录器（StateChanged 在后台线程触发）。</summary>
    private sealed class 状态记录器
    {
        private readonly object _同步 = new();
        private readonly List<连接状态> _状态 = new();

        public void 添加(连接状态 状态值)
        {
            lock (_同步)
            {
                _状态.Add(状态值);
            }
        }

        public IReadOnlyList<连接状态> 快照()
        {
            lock (_同步)
            {
                return _状态.ToList();
            }
        }
    }

    private static async Task<bool> 异步等待状态值(
        连接管理器 管理器, 连接状态 期望, int 超时毫秒 = 5000)
    {
        var 截止时间 = Environment.TickCount64 + 超时毫秒;

        while (Environment.TickCount64 < 截止时间)
        {
            if (管理器.状态 == 期望)
            {
                return true;
            }

            await Task.Delay(10);
        }

        return 管理器.状态 == 期望;
    }

    [Fact]
    public async Task 断线重连_首次为连接中_失败后转重连中且退避逐步拉长()
    {
        await using var 客户端 = new 替身Modbus客户端();
        客户端.失败下一个连接(int.MaxValue);   // 全程失败，只看退避节奏

        var 记录器 = new 状态记录器();
        var 选项 = 快选项(自动重连: true);

        await using var 管理器 = new 连接管理器(
            客户端, 选项, NullLogger<连接管理器>.Instance);
        管理器.状态已变更 += (_, 状态值) => 记录器.添加(状态值);

        await 管理器.异步启动();
        await Task.Delay(400);
        await 管理器.异步停止();

        var 尝试次数 = 客户端.连接尝试次数;
        Assert.True(尝试次数.Count >= 3, $"应至少重试 3 次，实际 {尝试次数.Count} 次");

        var 状态 = 记录器.快照();
        Assert.Equal(连接状态.连接中, 状态[0]);
        Assert.Contains(连接状态.重连中, 状态);
        Assert.DoesNotContain(连接状态.已连接, 状态);

        var 缺口 = new List<double>();
        for (var 条目 = 1; 条目 < 尝试次数.Count; 条目++)
        {
            缺口.Add((尝试次数[条目] - 尝试次数[条目 - 1]).TotalMilliseconds);
        }

        var 最大间隔 = 缺口.Max();

        // 退避确实在拉长（固定间隔重试会让最大间隔停在初始值附近）
        Assert.True(最大间隔 >= 选项.重连初始延迟毫秒 * 1.5,
            $"退避未生效：最大间隔仅 {最大间隔:F0}ms，初始 {选项.重连初始延迟毫秒}ms");

        // 且没有突破上限（上限 80ms 时不应出现几百毫秒的间隔）
        Assert.True(最大间隔 <= 选项.重连最大延迟毫秒 * 2.0,
            $"退避超出上限：最大间隔 {最大间隔:F0}ms，上限 {选项.重连最大延迟毫秒}ms");
    }

    [Fact]
    public async Task 断线重连_连续失败后恢复应回到已连接()
    {
        await using var 客户端 = new 替身Modbus客户端();
        客户端.失败下一个连接(2);   // 前两次失败，第三次成功

        var 记录器 = new 状态记录器();
        var 选项 = 快选项(自动重连: true, 初始毫秒: 20, 最大毫秒: 200);

        await using var 管理器 = new 连接管理器(
            客户端, 选项, NullLogger<连接管理器>.Instance);
        管理器.状态已变更 += (_, 状态值) => 记录器.添加(状态值);

        await 管理器.异步启动();

        var 已连接 = await 异步等待状态值(管理器, 连接状态.已连接, 5000);
        Assert.True(已连接, "两次失败后第三次应连上，但始终未进入 Connected");

        Assert.Equal(3, 客户端.连接尝试数量);

        var 状态 = 记录器.快照();
        Assert.Equal(连接状态.连接中, 状态[0]);
        Assert.Contains(连接状态.重连中, 状态);
        Assert.Equal(连接状态.已连接, 状态[^1]);

        await 管理器.异步停止();
        Assert.Equal(连接状态.已断开, 管理器.状态);
    }

    [Fact]
    public async Task 断线重连_关闭自动重连时应进入故障态且只尝试一次()
    {
        await using var 客户端 = new 替身Modbus客户端();
        客户端.失败下一个连接(5);

        var 选项 = 快选项(自动重连: false);

        await using var 管理器 = new 连接管理器(
            客户端, 选项, NullLogger<连接管理器>.Instance);

        await 管理器.异步启动();

        var 故障 = await 异步等待状态值(管理器, 连接状态.故障, 3000);
        Assert.True(故障, "关闭自动重连后应进入 Faulted");

        Assert.Equal(1, 客户端.连接尝试数量);

        // 再等一会，确认没有偷偷重试
        await Task.Delay(200);
        Assert.Equal(1, 客户端.连接尝试数量);

        await 管理器.异步停止();
        Assert.Equal(连接状态.已断开, 管理器.状态);
    }

    [Fact]
    public async Task 断线重连_停止时应能干净退出()
    {
        await using var 客户端 = new 替身Modbus客户端();
        客户端.失败下一个连接(int.MaxValue);

        var 选项 = 快选项(自动重连: true);

        await using var 管理器 = new 连接管理器(
            客户端, 选项, NullLogger<连接管理器>.Instance);

        await 管理器.异步启动();
        await Task.Delay(60);

        using var 取消令牌源 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var 停止 = 管理器.异步停止(取消令牌源.Token);

        var 已完成 = await Task.WhenAny(停止, Task.Delay(TimeSpan.FromSeconds(4))) == 停止;
        Assert.True(已完成, "StopAsync 未在预期时间内返回，重连循环可能未响应取消");

        await 停止;
        Assert.Equal(连接状态.已断开, 管理器.状态);
    }
}
