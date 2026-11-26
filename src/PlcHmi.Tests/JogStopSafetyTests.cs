using Microsoft.Extensions.Logging.Abstractions;
using 上位机.业务.配置;
using 上位机.业务.服务;
using 上位机.通信.汇川;
using 上位机.核心.枚举;
using 上位机.核心.模型;
using 上位机.上位机测试.替身;

namespace 上位机.上位机测试;

/// <summary>
/// 点动停止的安全性测试。
///
/// <para><b>为什么单独一组</b>：2026-09-12 起点动改为"按住才动、松开即停"，界面上不再有
/// 独立的「停止」按钮。这意味着<b>停止路径成了唯一的安全出口</b>——
/// 一旦它因为"手动使能没开"之类的理由被拒绝，轴就会一直动下去。</para>
///
/// <para><b>2026-09-17 起</b>：点动方向按 PLC 地址分配表改为 D 区字内位
/// （X 轴 D1210.2 / D1210.3），写命令是"读字 → 改位 → 写回"，
/// 因此停止路径体现在<b>寄存器写</b>上（写完目标位为 0），不再是线圈写。</para>
/// </summary>
public class 点动停止安全测试
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

    [Fact]
    public async Task 未使能时点动应被拒绝()
    {
        await using var 客户端 = new 替身Modbus客户端();
        var 服务实例 = 新服务(配方配置加载器.加载(out _), 客户端);

        Assert.False(服务实例.是否启用);

        await Assert.ThrowsAsync<InvalidOperationException>(() => 服务实例.异步点动("X", 1));

        Assert.Empty(客户端.线圈写入);
        Assert.Empty(客户端.寄存器写入);
    }

    [Fact]
    public async Task 未使能时停止仍须生效()
    {
        await using var 客户端 = new 替身Modbus客户端();
        var 服务实例 = 新服务(配方配置加载器.加载(out _), 客户端);

        Assert.False(服务实例.是否启用);

        // 停止动作必须无条件生效：使能断开时拒绝停止，就等于让轴一直动下去
        await 服务实例.异步停止点动("X");

        // 正转 D1210.2 / 反转 D1210.3 同在一字上：两次读-改-写，写回的字都清掉了目标位
        Assert.Equal(2, 客户端.寄存器写入.Count);
        Assert.All(客户端.寄存器写入, 写入 => Assert.Equal(0, 写入.值 & 0x0C));

        Assert.Equal(new[] { 1210, 1210 }, 客户端.寄存器写入.Select(元件项 => 元件项.地址).ToArray());
    }

    [Theory]
    [InlineData("X", 1210)]
    [InlineData("Z", 1410)]
    [InlineData("R", 1510)]
    public async Task 停止应复位该轴命令字的正反转两个位(string 轴号, int 命令字地址)
    {
        await using var 客户端 = new 替身Modbus客户端();
        var 服务实例 = 新服务(配方配置加载器.加载(out _), 客户端);

        await 服务实例.异步停止点动(轴号);

        // 命令字偏移 10：位 2 正转、位 3 反转；停止后两位都应为 0
        Assert.Equal(2, 客户端.寄存器写入.Count);
        Assert.All(客户端.寄存器写入, 写入 =>
        {
            Assert.Equal(命令字地址, 写入.地址);
            Assert.Equal(0, 写入.值 & 0x0C);
        });
    }
}
