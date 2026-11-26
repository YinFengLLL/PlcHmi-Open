using Microsoft.Extensions.Logging.Abstractions;
using 上位机.业务.配置;
using 上位机.业务.服务;
using 上位机.通信.汇川;
using 上位机.核心.枚举;
using 上位机.核心.模型;
using 上位机.上位机测试.替身;

namespace 上位机.上位机测试;

/// <summary>
/// 无人值守相关的行为测试：报警历史落盘、全部确认的一致性、目录可写性预检。
///
/// <para><b>为什么这一组重要：</b>上位机替代触控屏后是无人值守长跑的。
/// 报警历史只存内存 → 换班查不到上一班故障；"全部确认"只改一半 →
/// 同一个界面有两种说法。这两条都属于"开发机上永远看不出来"的问题。</para>
/// </summary>
// 直接增删真实的报警历史文件与数据目录，与其它碰配置文件的测试互斥，禁止并行
[Collection("config-files")]
public class 无人值守操作测试
{
    private static 标签写入器 新写入器(替身Modbus客户端 客户端) =>
        new(客户端,
            new 标签仓储(NullLogger<标签仓储>.Instance),
            new 汇川地址解析器(PLC系列.小型PLC),
            NullLogger<标签写入器>.Instance);

    /// <summary>备份真实的历史文件，返回还原动作。</summary>
    private static Action 备份历史()
    {
        var 文件路径 = 配置路径.报警历史;
        var 原始 = File.Exists(文件路径) ? File.ReadAllText(文件路径) : null;

        return () =>
        {
            if (原始 is null)
            {
                if (File.Exists(文件路径))
                {
                    File.Delete(文件路径);
                }
            }
            else
            {
                File.WriteAllText(文件路径, 原始);
            }
        };
    }

    /// <summary>等待后台落盘完成。</summary>
    private static void 等待文件(string 文件路径, int 超时毫秒 = 5000)
    {
        var 截止时间 = DateTime.Now.AddMilliseconds(超时毫秒);

        while (DateTime.Now < 截止时间 && !File.Exists(文件路径))
        {
            Thread.Sleep(20);
        }
    }

    // ── 报警历史落盘 ────────────────────────────────────────────────────

    [Fact]
    public void 报警历史应能落盘并重新读回()
    {
        var 恢复 = 备份历史();

        try
        {
            using (var 存储 = new 报警历史存储(NullLogger<报警历史存储>.Instance))
            {
                存储.请求保存(
                [
                    new 报警条目
                    {
                        代码 = "A001",
                        消息 = "急停被按下",
                        等级 = 报警等级.紧急,
                        已触发 = new DateTime(2026, 9, 13, 8, 30, 0),
                    },
                ]);

                // 落盘在后台线程，等它写完再释放（Dispose 会再刷一次，双保险）
                等待文件(配置路径.报警历史);
            }

            using var 重载 = new 报警历史存储(NullLogger<报警历史存储>.Instance);
            var 已重载 = 重载.加载();

            Assert.Single(已重载);
            Assert.Equal("A001", 已重载[0].代码);
            Assert.Equal(报警等级.紧急, 已重载[0].等级);
        }
        finally
        {
            恢复();
        }
    }

    [Fact]
    public void 历史文件不存在时应返回空列表而不是抛异常()
    {
        var 恢复 = 备份历史();

        try
        {
            if (File.Exists(配置路径.报警历史))
            {
                File.Delete(配置路径.报警历史);
            }

            using var 存储 = new 报警历史存储(NullLogger<报警历史存储>.Instance);

            Assert.Empty(存储.加载());
        }
        finally
        {
            恢复();
        }
    }

    // ── 报警服务：跨重启恢复 ────────────────────────────────────────────

    [Fact]
    public async Task 报警产生后历史应可被新实例读回()
    {
        var 恢复 = 备份历史();

        try
        {
            await using (var 客户端 = new 替身Modbus客户端())
            using (var 历史 = new 报警历史存储(NullLogger<报警历史存储>.Instance))
            {
                var 首个 = new 报警服务(新写入器(客户端), NullLogger<报警服务>.Instance, 历史);

                首个.触发("A010", "压装压力偏低", 报警等级.警告);
            }

            等待文件(配置路径.报警历史);

            // 模拟"重启"：新实例应当把上一班的报警读回来
            await using var 客户端2 = new 替身Modbus客户端();
            using var 历史2 = new 报警历史存储(NullLogger<报警历史存储>.Instance);

            var 第二 = new 报警服务(新写入器(客户端2), NullLogger<报警服务>.Instance, 历史2);

            Assert.Contains(第二.历史, 历史条目 => 历史条目.代码 == "A010");
        }
        finally
        {
            恢复();
        }
    }

    // ── 报警一致性 ──────────────────────────────────────────────────────

    [Fact]
    public async Task 全部确认应同时更新历史避免同一界面两种说法()
    {
        await using var 客户端 = new 替身Modbus客户端();

        // 不传 historyStore：本用例只验证内存一致性
        var 服务实例 = new 报警服务(新写入器(客户端), NullLogger<报警服务>.Instance);

        服务实例.触发("A020", "气缸超时", 报警等级.警告);
        服务实例.确认全部();

        Assert.True(服务实例.活动报警[0].是否已确认);

        // 历史里的同一条也必须已确认 —— 此前 AcknowledgeAll 只改 _active，
        // 导致"全部确认"之后历史列表仍显示未确认
        Assert.True(
            服务实例.历史.First(历史条目 => 历史条目.代码 == "A020").是否已确认,
            "全部确认必须同步历史，否则同一件事在同一个界面上有两种说法");
    }

    [Fact]
    public async Task 单条确认应同时更新历史()
    {
        await using var 客户端 = new 替身Modbus客户端();
        var 服务实例 = new 报警服务(新写入器(客户端), NullLogger<报警服务>.Instance);

        服务实例.触发("A021", "测试", 报警等级.警告);
        服务实例.确认("A021");

        Assert.True(服务实例.历史.First(历史条目 => 历史条目.代码 == "A021").是否已确认);
    }

    [Fact]
    public async Task 不传历史存储时不应写盘并应正常工作()
    {
        var 恢复 = 备份历史();

        try
        {
            if (File.Exists(配置路径.报警历史))
            {
                File.Delete(配置路径.报警历史);
            }

            await using var 客户端 = new 替身Modbus客户端();
            var 服务实例 = new 报警服务(新写入器(客户端), NullLogger<报警服务>.Instance);

            服务实例.触发("A030", "内存模式", 报警等级.信息);
            服务实例.清空历史();

            Assert.Empty(服务实例.历史);
            Assert.False(File.Exists(配置路径.报警历史), "未注入存储时不应产生磁盘文件");
        }
        finally
        {
            恢复();
        }
    }

    // ── 目录可写性预检 ──────────────────────────────────────────────────

    [Fact]
    public void 可写环境下预检应无问题并建好数据目录()
    {
        var 问题 = 配置路径.验证可写();

        Assert.Empty(问题);
        Assert.True(Directory.Exists(配置路径.数据目录), "预检应顺便把数据目录建出来");
    }
}
