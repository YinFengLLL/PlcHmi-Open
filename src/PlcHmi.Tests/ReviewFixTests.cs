using Microsoft.Extensions.Logging.Abstractions;
using 上位机.业务.配置;
using 上位机.业务.服务;
using 上位机.通信.汇川;
using 上位机.核心.枚举;
using 上位机.核心.模型;
using 上位机.上位机测试.替身;

namespace 上位机.上位机测试;

/// <summary>
/// 2026-09-29 代码审查整改的回归测试。
///
/// <para>每个用例钉住一条审查发现，防止修复被后续改动"改回去"：
/// 脉冲取消复位（H1）、浮点变化检测容差（H6）、失效推送（M9）、
/// 克隆漏字段（H11）、延时默认上限（M24）。</para>
/// </summary>
public class 审查整改回归测试
{
    private static 手动控件服务 新手动服务(替身Modbus客户端 客户端)
    {
        var 解析器 = new 汇川地址解析器(PLC系列.小型PLC);

        // 权限分支由 权限守卫测试 覆盖，这里只钉行为
        return new 手动控件服务(
            new 标签写入器(
                客户端,
                new 标签仓储(NullLogger<标签仓储>.Instance),
                解析器,
                NullLogger<标签写入器>.Instance),
            客户端,
            解析器,
            配方配置加载器.加载(out _),
            new 允许全部权限守卫(),
            NullLogger<手动控件服务>.Instance);
    }

    // ── H1：脉冲取消后必须复位（安全出口）────────────────────────────────

    [Fact]
    public async Task 脉冲被取消时线圈仍须复位()
    {
        await using var 客户端 = new 替身Modbus客户端();
        await 客户端.异步连接();

        var 服务实例 = 新手动服务(客户端);
        服务实例.设置启用(true);

        using var 取消源 = new CancellationTokenSource();
        var 脉冲任务 = 服务实例.异步脉冲线圈("M220", TimeSpan.FromSeconds(5), 取消源.Token);

        await Task.Delay(50);
        await 取消源.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => 脉冲任务);

        // 取消发生在等待期间：置位已下发，复位必须无条件补上，否则线圈停在动作位
        Assert.Equal(2, 客户端.线圈写入.Count);
        Assert.True(客户端.线圈写入[0].值);
        Assert.False(客户端.线圈写入[^1].值);
    }

    [Fact]
    public async Task 脉冲正常完成时置位与复位各一次()
    {
        await using var 客户端 = new 替身Modbus客户端();
        await 客户端.异步连接();

        var 服务实例 = 新手动服务(客户端);
        服务实例.设置启用(true);

        await 服务实例.异步脉冲线圈("M220", TimeSpan.FromMilliseconds(10));

        Assert.Equal(2, 客户端.线圈写入.Count);
        Assert.True(客户端.线圈写入[0].值);
        Assert.False(客户端.线圈写入[^1].值);
    }

    // ── H6：浮点变化检测容差（1e-9）──────────────────────────────────────

    private static 标签新值 浮点值(string 名称值, float 原始值, double 缩放后) =>
        new(名称值, 原始值, 缩放后, DateTime.Now, true);

    [Fact]
    public void 浮点末位抖动在容差内不应重复推送()
    {
        var 服务实例 = new 标签值服务(NullLogger<标签值服务>.Instance);
        var 事件数 = 0;
        服务实例.新值已变更 += (_, _) => 事件数++;

        服务实例.发布(浮点值("AxisXPosition", 1.0000000001f, 1.0000000001));
        服务实例.发布(浮点值("AxisXPosition", 1.0000000003f, 1.0000000003));

        // 首次必发；第二次差值 2e-10 < 1e-9，视为未变化（修复前原始值位级不等 → 每次轮询都误报）
        Assert.Equal(1, 事件数);
    }

    [Fact]
    public void 浮点真实变化仍应推送且可读回()
    {
        var 服务实例 = new 标签值服务(NullLogger<标签值服务>.Instance);
        var 事件数 = 0;
        服务实例.新值已变更 += (_, _) => 事件数++;

        服务实例.发布(浮点值("AxisXPosition", 1.0f, 1.0));
        服务实例.发布(浮点值("AxisXPosition", 1.0001f, 1.0001));

        Assert.Equal(2, 事件数);
        Assert.True(服务实例.尝试获取双精度("AxisXPosition", out var 值));
        Assert.Equal(1.0001, 值, 6);
    }

    [Fact]
    public void 布尔值变化应推送且相同值不重复推送()
    {
        var 服务实例 = new 标签值服务(NullLogger<标签值服务>.Instance);
        var 事件数 = 0;
        服务实例.新值已变更 += (_, _) => 事件数++;

        服务实例.发布(new 标签新值("Cylinder1", false, 0, DateTime.Now, true));
        服务实例.发布(new 标签新值("Cylinder1", false, 0, DateTime.Now, true));
        服务实例.发布(new 标签新值("Cylinder1", true, 1, DateTime.Now, true));

        Assert.Equal(2, 事件数);
    }

    // ── M9：失效全部要推送置灰事件 ───────────────────────────────────────

    [Fact]
    public void 失效全部应推送无效事件供界面置灰()
    {
        var 服务实例 = new 标签值服务(NullLogger<标签值服务>.Instance);
        var 事件数 = 0;
        var 最后一次有效 = true;
        服务实例.新值已变更 += (_, 值) =>
        {
            事件数++;
            最后一次有效 = 值.是否有效;
        };

        服务实例.发布(浮点值("AxisXPosition", 1.0f, 1.0));
        服务实例.失效全部();

        // 有效 → 无效 是一次真实的状态变化，界面必须收到通知（修复前静默改字典）
        Assert.Equal(2, 事件数);
        Assert.False(最后一次有效);
        Assert.False(服务实例.获取新值("AxisXPosition").是否有效);
    }

    // ── H11：克隆不得漏字段 ──────────────────────────────────────────────

    [Fact]
    public void 历史记录配置克隆应保留读取后复位触发位()
    {
        var 配置 = new 历史记录配置
        {
            标题 = "锁螺丝记录",
            读取后复位触发位 = false,   // PLC 程序自管触发位
            保留天数 = 30,
        };

        var 副本 = 配置.克隆();

        Assert.False(副本.读取后复位触发位);
        Assert.Equal(配置.标题, 副本.标题);
        Assert.Equal(配置.保留天数, 副本.保留天数);
    }

    // ── M24：延时条目默认上限 ────────────────────────────────────────────

    [Fact]
    public void 延时条目默认上限应为DINT毫秒上限()
    {
        // 2147483.647 秒 × 1000 = 2147483647 = Int32 上限（2026-10-16 现场要求延时按 DINT 存）。
        // 上限比实际换算窄会让合法值被拒；比它宽则下发时才报错——默认值必须与换算口径一致。
        Assert.Equal(2147483.647, new 参数条目定义().最大);
        Assert.Equal(2, 参数条目定义.延时占字数);
    }
}
