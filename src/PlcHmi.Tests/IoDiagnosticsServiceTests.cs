using Microsoft.Extensions.Logging.Abstractions;
using 上位机.业务.服务;
using 上位机.核心.抽象;
using 上位机.上位机测试.替身;

namespace 上位机.上位机测试;

/// <summary>
/// IO 通信诊断测试：X / Y 段 × FC01 / FC02 的原始读数与结论。
///
/// <para><b>为什么值得单测：</b>2026-10-17 现场第二次报障"X 对不上"的真因是 X 被放在线圈表
/// （用 FC01 读输入区）——连接正常、无异常、无告警，只是读到了另一块内存。诊断服务把两个
/// 功能码的读数并排摆出来，这类错位才看得见；结论文案必须能直接指导现场改配置。</para>
/// </summary>
public class IO诊断服务测试
{
    private static async Task<IO诊断报告> 诊断(替身位区客户端 客户端) =>
        await new IO诊断服务(客户端, NullLogger<IO诊断服务>.Instance)
            .异步诊断(替身位区客户端.X基址, 替身位区客户端.Y基址, 16);

    /// <summary>按给定位区行为跑一次诊断（替身由 using 释放，满足 CA2000）。</summary>
    private static async Task<IO诊断报告> 诊断(
        位区行为 线圈行为, 位区行为 离散行为, Action<替身位区客户端>? 置位 = null)
    {
        await using var 客户端 = new 替身位区客户端();
        客户端.XY线圈行为 = 线圈行为;
        客户端.XY离散行为 = 离散行为;
        置位?.Invoke(客户端);

        return await 诊断(客户端);
    }

    private static IO诊断项 取行(IO诊断报告 报告, string 标题) =>
        报告.项集合.Single(项 => 项.标题 == 标题);

    [Fact]
    public async Task 诊断_应读取四个功能码与地址段的组合()
    {
        var 报告 = await 诊断(位区行为.按物理状态, 位区行为.按物理状态);

        Assert.Equal(4, 报告.项集合.Count);
        Assert.Contains(报告.项集合, 项 => 项.标题 == "X 区 · 线圈" && 项.功能码 == "FC01");
        Assert.Contains(报告.项集合, 项 => 项.标题 == "X 区 · 离散输入" && 项.功能码 == "FC02");
        Assert.Contains(报告.项集合, 项 => 项.标题 == "Y 区 · 线圈" && 项.功能码 == "FC01");
        Assert.Contains(报告.项集合, 项 => 项.标题 == "Y 区 · 离散输入" && 项.功能码 == "FC02");

        // 成功的行要给 16 位位串（高位在左，长度与请求位数一致）
        foreach (var 项 in 报告.项集合.Where(项 => 项.成功))
        {
            Assert.Equal(16, 项.位串.Length);
            Assert.Matches("^[01]{16}$", 项.位串);
        }
    }

    [Fact]
    public async Task 诊断_X区只有FC02有信号_结论应指向离散输入()
    {
        // 复刻现场：FC01 在线圈表读"成功"但恒 0；物理输入在 FC02
        var 报告 = await 诊断(位区行为.恒读零, 位区行为.按物理状态,
            客户端 => 客户端.设置物理状态(替身位区客户端.X基址 + 0, true));   // X0 上料到位

        Assert.Equal(0, 取行(报告, "X 区 · 线圈").置一位数);
        Assert.Equal(1, 取行(报告, "X 区 · 离散输入").置一位数);

        // 结论必须直接告诉现场下一步怎么配
        Assert.Contains("离散输入", 报告.结论);
        Assert.Contains("X区域", 报告.结论);
    }

    [Fact]
    public async Task 诊断_两个功能码都读到信号_结论应要求显式配置()
    {
        var 报告 = await 诊断(位区行为.按物理状态, 位区行为.按物理状态,
            客户端 => 客户端.设置物理状态(替身位区客户端.X基址 + 0, true));

        Assert.True(取行(报告, "X 区 · 线圈").置一位数 > 0);
        Assert.True(取行(报告, "X 区 · 离散输入").置一位数 > 0);
        Assert.Contains("无法自动判定", 报告.结论);
    }

    [Fact]
    public async Task 诊断_两个功能码都读不到_结论应指向连接与地址()
    {
        var 报告 = await 诊断(位区行为.抛异常, 位区行为.抛异常);

        Assert.All(报告.项集合, 项 =>
        {
            Assert.False(项.成功);
            Assert.Equal("读不到", 项.结果文本);
            Assert.False(string.IsNullOrWhiteSpace(项.错误消息));
        });

        Assert.Contains("都读不到", 报告.结论);
    }

    [Fact]
    public async Task 诊断_两个功能码都读到全零_结论应提示核对地址()
    {
        var 报告 = await 诊断(位区行为.恒读零, 位区行为.恒读零);

        Assert.All(报告.项集合, 项 => Assert.Equal(0, 项.置一位数));
        Assert.Contains("全 0", 报告.结论);
        Assert.Contains("八进制", 报告.结论);
    }
}
