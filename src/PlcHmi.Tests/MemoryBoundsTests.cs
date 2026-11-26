using Microsoft.Extensions.Logging.Abstractions;
using 上位机.业务.服务;
using 上位机.通信.汇川;
using 上位机.核心.枚举;
using 上位机.核心.模型;
using 上位机.上位机测试.替身;

namespace 上位机.上位机测试;

/// <summary>
/// 长时运行的内存有界性断言。
///
/// <para><b>为什么补这一组：</b>上位机在现场是"开机不关"的，任何只增不减的集合
/// 最终都会把内存吃干。报警历史与活动列表是最容易失控的两处，
/// 用断言把它们的有界性固定下来，防止后续改动无意中拆掉裁剪逻辑。</para>
/// </summary>
public class 内存边界测试
{
    private static 报警服务 新报警服务(替身Modbus客户端 客户端) =>
        new(
            new 标签写入器(
                客户端,
                new 标签仓储(NullLogger<标签仓储>.Instance),
                new 汇川地址解析器(PLC系列.小型PLC),
                NullLogger<标签写入器>.Instance),
            NullLogger<报警服务>.Instance);

    [Fact]
    public async Task 报警历史_超过上限应裁剪到一千条()
    {
        await using var 客户端 = new 替身Modbus客户端();
        var 报警列表 = 新报警服务(客户端);

        for (var 条目 = 0; 条目 < 1200; 条目++)
        {
            var 代码值 = $"T{条目:0000}";
            报警列表.触发(代码值, "测试报警", 报警等级.警告);
            报警列表.清空(代码值);   // 立即消除，避免活动列表无界增长掩盖"历史裁剪"这一被测点
        }

        Assert.Empty(报警列表.活动报警);
        Assert.Equal(1000, 报警列表.历史.Count);
    }

    [Fact]
    public async Task 活动报警_同编号不应重复入列()
    {
        await using var 客户端 = new 替身Modbus客户端();
        var 报警列表 = 新报警服务(客户端);

        报警列表.触发("A001", "第一次", 报警等级.警告);
        报警列表.触发("A001", "第二次", 报警等级.紧急);

        // 重复触发不等于新增：活动列表与历史各只应有一条
        Assert.Single(报警列表.活动报警);
        Assert.Single(报警列表.历史);
    }

    [Fact]
    public void 点位值未变化时不应推送事件()
    {
        var 值列表 = new 标签值服务(NullLogger<标签值服务>.Instance);
        var 已触发 = 0;
        值列表.新值已变更 += (_, _) => 已触发++;

        var 当前时刻 = DateTime.Now;

        值列表.发布(new 标签新值("Speed", 1.0, 1.0, 当前时刻, true));
        Assert.Equal(1, 已触发);

        // 同为 1.0：不应再推送（否则 UI 会被无变化的刷新淹没）
        值列表.发布(new 标签新值("Speed", 1.0, 1.0, 当前时刻.AddSeconds(1), true));
        Assert.Equal(1, 已触发);

        值列表.发布(new 标签新值("Speed", 2.0, 2.0, 当前时刻.AddSeconds(2), true));
        Assert.Equal(2, 已触发);
    }

    [Fact]
    public void 点位值_标记失效后应取不到值()
    {
        var 值列表 = new 标签值服务(NullLogger<标签值服务>.Instance);
        值列表.发布(new 标签新值("Speed", 1.0, 1.0, DateTime.Now, true));

        Assert.True(值列表.尝试获取双精度("Speed", out var 之前));
        Assert.Equal(1.0, 之前);
        Assert.True(值列表.获取新值("Speed").是否有效);

        值列表.失效全部();

        Assert.False(值列表.获取新值("Speed").是否有效);
        Assert.False(值列表.尝试获取双精度("Speed", out _));
        Assert.False(值列表.尝试获取布尔("Speed", out _));
    }
}
