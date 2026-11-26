using Microsoft.Extensions.Logging.Abstractions;
using 上位机.业务.服务;
using 上位机.通信.汇川;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;
using 上位机.上位机测试.替身;

namespace 上位机.上位机测试;

/// <summary>
/// 主页面左下工位状态条（锁附 / 点胶 / 加工完成 + 已锁附颗数）的数据汇聚回归测试。
///
/// <para><b>2026-10-16 现场要求：</b>三盏灯 <c>0</c> = 未完成、<c>1</c> = 完成；
/// 颗数读一个 <b>INT</b> 型地址，地址均由管理员在运行页「编辑地址」里改
/// （元件表与校验见 <see cref="运行监控地址测试"/>）。</para>
///
/// <para>这里钉住 <see cref="设备服务"/> 的汇聚行为：事件只在值变化时推、
/// 无效值保持上次显示（不能闪成"未完成"）、脏值按"非 0 即完成"归一。</para>
/// </summary>
public class 工位状态条测试
{
    private static (设备服务 设备, 标签值服务 值列表) 新服务(替身Modbus客户端 客户端)
    {
        var 解析器 = new 汇川地址解析器(PLC系列.小型PLC);
        var 值列表 = new 标签值服务(NullLogger<标签值服务>.Instance);

        var 写入器 = new 标签写入器(
            客户端, 替身标签仓储.运行页默认点位(), 解析器, NullLogger<标签写入器>.Instance);

        return (new 设备服务(值列表, 写入器, new 允许全部权限守卫(),
            NullLogger<设备服务>.Instance), 值列表);
    }

    /// <summary>三个完成标志是 M 区<b>位</b>：点位值是 bool（true = 完成）。</summary>
    private static 标签新值 标志(string 名称值, bool 值) =>
        new(名称值, 值, 值 ? 1 : 0, DateTime.Now, true);

    // ── 显示文案 ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, "完成")]
    [InlineData(false, "未完成")]
    public void 完成状态显示_文案应与现场口径一致(bool 完成, string 期望文本)
    {
        var (文本, 颜色键) = 设备服务.完成状态显示(完成);

        Assert.Equal(期望文本, 文本);
        Assert.False(string.IsNullOrWhiteSpace(颜色键));
    }

    [Fact]
    public void 完成状态显示_未完成不得使用报警色()
    {
        // 未完成是正常的中间态：用红色/橙色会把机器待机时的状态条染成一片黄，
        // 反而让真正的报警失色。抖动颜色必须先红在"未完成变红"这条上。
        Assert.Equal("SuccessBrush", 设备服务.完成状态显示(true).颜色键);
        Assert.Equal("MutedBrush", 设备服务.完成状态显示(false).颜色键);
    }

    // ── 汇聚行为 ────────────────────────────────────────────────────────

    [Fact]
    public async Task 加工状态_初始应为未完成且颗数为零()
    {
        await using var 客户端 = new 替身Modbus客户端();
        var (设备, _) = 新服务(客户端);

        Assert.False(设备.锁附完成);
        Assert.False(设备.点胶完成);
        Assert.False(设备.加工完成);
        Assert.Equal(0, 设备.已锁附颗数);
    }

    [Fact]
    public async Task 加工状态_点位置一应上报完成且只在变化时推事件()
    {
        await using var 客户端 = new 替身Modbus客户端();
        var (设备, 值列表) = 新服务(客户端);

        var 锁附次数 = 0;
        var 点胶次数 = 0;
        var 加工次数 = 0;
        设备.锁附状态已变更 += (_, _) => 锁附次数++;
        设备.点胶状态已变更 += (_, _) => 点胶次数++;
        设备.加工状态已变更 += (_, _) => 加工次数++;

        值列表.发布(标志("ScrewDone", true));
        值列表.发布(标志("GlueDone", true));
        值列表.发布(标志("ProcessDone", true));

        Assert.True(设备.锁附完成);
        Assert.True(设备.点胶完成);
        Assert.True(设备.加工完成);
        Assert.Equal((1, 1, 1), (锁附次数, 点胶次数, 加工次数));

        // 值没变：不得再推事件（界面每周期刷新会闪；与 标签值服务 的变化检测同口径）
        值列表.发布(标志("ScrewDone", true));
        Assert.Equal(1, 锁附次数);

        // 回到未完成：推事件，界面跟着变回灰色"未完成"
        值列表.发布(标志("ScrewDone", false));
        Assert.False(设备.锁附完成);
        Assert.Equal(2, 锁附次数);
    }

    [Fact]
    public async Task 加工状态_点位若被定义成数值量也应正确归一()
    {
        await using var 客户端 = new 替身Modbus客户端();
        var (设备, 值列表) = 新服务(客户端);

        // 出厂定义是 M 区位（bool），但现场也可能把该点位改定义成 D 区数值量：
        // 一律按"非 0 即完成"归一，不能把"完成度 2"透传给界面
        值列表.发布(new 标签新值("ScrewDone", (ushort)2, 2, DateTime.Now, true));
        Assert.True(设备.锁附完成);

        值列表.发布(new 标签新值("GlueDone", (ushort)0, 0, DateTime.Now, true));
        Assert.False(设备.点胶完成);
    }

    [Fact]
    public async Task 加工状态_无效值应保持上次显示而不闪成未完成()
    {
        await using var 客户端 = new 替身Modbus客户端();
        var (设备, 值列表) = 新服务(客户端);

        值列表.发布(标志("ProcessDone", true));
        Assert.True(设备.加工完成);

        // 断线 / 解析失败：保持"完成"，等下一轮有效值；不能因为读不到就显示"未完成"误导操作员
        值列表.发布(标签新值.无效("ProcessDone"));
        Assert.True(设备.加工完成);
    }

    [Fact]
    public async Task 已锁附颗数_应按点位值更新并支持负数照实显示()
    {
        await using var 客户端 = new 替身Modbus客户端();
        var (设备, 值列表) = 新服务(客户端);

        var 事件次数 = 0;
        long 最近值 = -1;
        设备.已锁附颗数已变更 += (_, 颗数) =>
        {
            事件次数++;
            最近值 = 颗数;
        };

        值列表.发布(new 标签新值("ScrewCount", (short)1234, 1234, DateTime.Now, true));
        Assert.Equal(1234, 设备.已锁附颗数);
        Assert.Equal(1234, 最近值);

        // Int16 有符号：PLC 侧回绕成负数时照实显示（显示成 0 会掩盖现场问题）
        值列表.发布(new 标签新值("ScrewCount", (short)-3, -3, DateTime.Now, true));
        Assert.Equal(-3, 设备.已锁附颗数);

        Assert.Equal(2, 事件次数);
    }
}
