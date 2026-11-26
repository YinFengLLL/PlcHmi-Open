using Microsoft.Extensions.Logging.Abstractions;
using 上位机.业务.配置;
using 上位机.业务.服务;
using 上位机.通信.汇川;
using 上位机.核心.枚举;
using 上位机.核心.模型;
using 上位机.上位机测试.替身;

namespace 上位机.上位机测试;

/// <summary>
/// 点动速度档位写入测试。
///
/// <para><b>约定（2026-09-12 需求方确认）</b>：每轴一个 D 区寄存器，写 1 低速 / 2 中速 / 3 高速，
/// 由 PLC 侧据此选择点动速度；地址可改、随选中轴切换。</para>
///
/// <para><b>为什么不需要手动使能</b>：档位是"参数设定"而不是动作机构——
/// 与参数页的延时 / 按钮表一致；真正的动作仍受使能联锁保护。</para>
/// </summary>
public class 点动速度等级测试
{
    private static 手动控件服务 新服务(配方配置 配置对象, 替身Modbus客户端 客户端)
    {
        var 解析器 = new 汇川地址解析器(PLC系列.小型PLC);

        return new 手动控件服务(
            new 标签写入器(
                客户端,
                new 标签仓储(NullLogger<标签仓储>.Instance),
                解析器,
                NullLogger<标签写入器>.Instance),
            客户端,
            解析器,
            配置对象,
            new 允许全部权限守卫(),   // 本组用例不覆盖权限分支，显式声明
            NullLogger<手动控件服务>.Instance);
    }

    [Theory]
    // 2026-10-13 现场要求：六轴共用同一个速度档位地址（切档一次，六轴同时生效）；
    // 2026-10-21 出厂配置换为现场版本，档位地址是 D15 —— 本用例逐一钉死"每轴都写同一个寄存器"。
    [InlineData("X", 15)]
    [InlineData("Y", 15)]
    [InlineData("Z", 15)]
    [InlineData("ZZ", 15)]
    [InlineData("GlueX", 15)]
    [InlineData("GlueZ", 15)]
    public async Task 档位应写入该轴对应的寄存器(string 轴号, int 期望地址)
    {
        await using var 客户端 = new 替身Modbus客户端();
        var 服务实例 = 新服务(配方配置加载器.加载(out _), 客户端);

        await 服务实例.异步设置点动速度等级(轴号, 2);

        var 写入 = Assert.Single(客户端.寄存器写入);

        // D 区基址为 0，因此 Modbus 地址 == D 序号
        Assert.Equal(期望地址, 写入.地址);
        Assert.Equal((ushort)2, 写入.值);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(-1)]
    public async Task 越界档位值应被拒绝且不写PLC(int 等级)
    {
        await using var 客户端 = new 替身Modbus客户端();
        var 服务实例 = 新服务(配方配置加载器.加载(out _), 客户端);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => 服务实例.异步设置点动速度等级("X", 等级));

        Assert.Empty(客户端.寄存器写入);
    }

    [Fact]
    public async Task 未配置档位地址的轴应被拒绝()
    {
        await using var 客户端 = new 替身Modbus客户端();
        var 配置对象 = 配方配置加载器.加载(out _);
        配置对象.点动速度等级.Clear();

        var 服务实例 = 新服务(配置对象, 客户端);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => 服务实例.异步设置点动速度等级("X", 点动速度等级地址.最小等级));
    }

    [Fact]
    public async Task 不支持的轴应被拒绝()
    {
        await using var 客户端 = new 替身Modbus客户端();
        var 服务实例 = 新服务(配方配置加载器.加载(out _), 客户端);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => 服务实例.异步设置点动速度等级("Q", 1));
    }
}
