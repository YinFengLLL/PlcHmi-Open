using Microsoft.Extensions.Logging.Abstractions;
using 上位机.业务.服务;

namespace 上位机.上位机测试;

/// <summary>
/// 产量统计服务的统计口径回归测试。
///
/// <para>覆盖：整点采样与小时产量、计数器回退（数据清除）、缺口小时补 0、
/// 近 7 天日汇总与日期标签、时钟回拨忽略。</para>
/// </summary>
public class 产量统计测试
{
    /// <summary>可控的产量计数器与时钟。</summary>
    private sealed class 测试环境
    {
        public long 产量;
        public DateTime 时刻;

        public readonly 产量统计服务 服务;

        public 测试环境(DateTime 初始时刻)
        {
            时刻 = 初始时刻;
            服务 = new 产量统计服务(
                NullLogger<产量统计服务>.Instance,
                null,
                () => 产量,
                () => 时刻);
        }
    }

    private static readonly DateTime 今日 = new(2026, 9, 24, 10, 0, 0);   // 周四

    [Fact]
    public void 整点采样后_小时产量应为相邻快照差值_当前小时取实时值()
    {
        var 环境 = new 测试环境(今日);
        var 服务 = 环境.服务;

        // 10:00 打点（累计 100）→ 11:00 打点（累计 160）→ 11:30 实时 200
        环境.产量 = 100;
        Assert.True(服务.检查采样(今日));

        环境.产量 = 160;
        环境.时刻 = 今日.AddHours(1);
        Assert.True(服务.检查采样(环境.时刻));

        环境.产量 = 200;
        环境.时刻 = 今日.AddMinutes(90);
        Assert.False(服务.检查采样(环境.时刻));   // 同一小时不打点

        var 序列 = 服务.今日小时产量;

        Assert.Equal(0, 序列[9]);
        Assert.Equal(60, 序列[10]);   // 160 − 100
        Assert.Equal(40, 序列[11]);   // 实时：200 − 160
    }

    [Fact]
    public void 计数器回退_数据清除后_小时产量应取清零后的计数而非负数()
    {
        var 环境 = new 测试环境(今日);
        var 服务 = 环境.服务;

        环境.产量 = 500;
        服务.检查采样(今日);

        // 数据清除：计数器从 500 清回 20
        环境.产量 = 20;
        环境.时刻 = 今日.AddHours(1);
        服务.检查采样(环境.时刻);

        var 序列 = 服务.今日小时产量;

        Assert.Equal(20, 序列[10]);   // 20 − 500 < 0 → 取下一快照值 20，绝不出负数
        Assert.Equal(0, 序列[11]);    // 当前小时实时：20 − 20
    }

    [Fact]
    public void 缺口小时_程序未运行期间产量记零_重启后第一段计入差值()
    {
        var 环境 = new 测试环境(今日);
        var 服务 = 环境.服务;

        环境.产量 = 100;
        服务.检查采样(今日);

        // 程序停了 3 小时（11:00–13:00 无读数），14:00 重启累计 400
        环境.产量 = 400;
        环境.时刻 = 今日.AddHours(4);
        服务.检查采样(环境.时刻);

        var 序列 = 服务.今日小时产量;

        Assert.Equal(0, 序列[10]);
        Assert.Equal(0, 序列[11]);    // 缺口小时记 0
        Assert.Equal(0, 序列[12]);
        Assert.Equal(300, 序列[13]);  // 400 − 100 归入重启后第一段
        Assert.Equal(0, 序列[14]);    // 当前小时实时
    }

    [Fact]
    public void 近七天日汇总_应逐日累计小时产量_今天含实时值()
    {
        var 环境 = new 测试环境(今日);
        var 服务 = 环境.服务;

        // 昨天（09-23）：8 点 100 → 9 点 180（此后停机，计数器不动）
        环境.产量 = 100;
        环境.时刻 = 今日.AddDays(-1).AddHours(8);
        服务.检查采样(环境.时刻);

        环境.产量 = 180;
        环境.时刻 = 今日.AddDays(-1).AddHours(9);
        服务.检查采样(环境.时刻);

        // 今天（09-24）：10 点仍是 180（停机中）→ 11 点 260（复工生产 80 件）
        环境.产量 = 180;
        环境.时刻 = 今日;
        服务.检查采样(环境.时刻);

        环境.产量 = 260;
        环境.时刻 = 今日.AddHours(1);
        服务.检查采样(环境.时刻);

        var 周产量 = 服务.周每日产量;
        var 标签 = 服务.周日期标签;

        Assert.Equal(7, 周产量.Count);
        Assert.Equal(7, 标签.Count);

        // 6 天前到 3 天前都没有样本 → 0
        Assert.Equal(0, 周产量[0]);
        Assert.Equal(0, 周产量[3]);

        // 昨天：8 点段 80（100→180）；9 点段 = 下一快照 180 − 180 = 0
        Assert.Equal(80, 周产量[5]);
        Assert.StartsWith("09-23", 标签[5]);
        Assert.Contains("周三", 标签[5]);

        // 今天：9 点段（停机缺口最后一段）= 180 − 180 = 0；
        // 10 点段 = 260 − 180 = 80；11 点实时 = 260 − 260 = 0
        Assert.Equal(80, 周产量[6]);
        Assert.StartsWith("09-24", 标签[6]);
        Assert.Contains("周四", 标签[6]);
    }

    [Fact]
    public void 时钟回拨_不应产生采样点()
    {
        var 环境 = new 测试环境(今日);
        var 服务 = 环境.服务;

        环境.产量 = 100;
        Assert.True(服务.检查采样(今日));

        环境.时刻 = 今日.AddHours(1);
        Assert.True(服务.检查采样(环境.时刻));

        // 回拨到 10 点：拒绝打点，样本不增不减
        Assert.False(服务.检查采样(今日));

        var 序列 = 服务.今日小时产量;

        // 10 点段 = 11 点快照 − 10 点快照 = 100 − 100 = 0（计数器没动）
        Assert.Equal(0, 序列[10]);
        Assert.Equal(0, 序列[11]);   // 当前小时实时：100 − 100
    }
}
