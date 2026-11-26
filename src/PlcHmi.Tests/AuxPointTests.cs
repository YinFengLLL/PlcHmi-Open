using Microsoft.Extensions.Logging.Abstractions;
using 上位机.业务.配置;
using 上位机.业务.服务;
using 上位机.核心.抽象;
using 上位机.上位机测试.替身;

namespace 上位机.上位机测试;

/// <summary>
/// 其它点（辅助点）定义管理测试。
///
/// <para><b>守的是什么：</b>现场反馈"编辑其它点时改不了已有名称"（2026-10-15）——
/// 原先编辑模式把名称框锁死，而名称其实是可改的：服务层按原名定位后整体替换。
/// 改名不能让两个点撞名（名称是"按名查找 / 删除 / CSV 交换"的依据），
/// 也不能让"只改坐标、名称原样提交"被误判成重复。</para>
///
/// <para>会写测试输出目录里的 <c>config/aux-points.json</c>，与其它碰配置文件的用例同集合，禁用并行。</para>
/// </summary>
[Collection("config-files")]
public class 其它点坐标测试
{
    private static 辅助点服务 新服务(替身Modbus客户端 客户端) =>
        new(
            客户端,
            配方配置加载器.加载(out _),
            new 允许全部权限守卫(),
            NullLogger<辅助点服务>.Instance);

    [Fact]
    public async Task 编辑时应能把已有名称改掉并落盘()
    {
        var 路径 = 配置路径.其它点;
        var 原始 = File.Exists(路径) ? await File.ReadAllTextAsync(路径) : null;

        try
        {
            await using var 客户端 = new 替身Modbus客户端();
            var 服务实例 = 新服务(客户端);
            await 服务实例.异步初始化();

            var 原名 = 服务实例.点位集合[0].名称;
            var 候选 = 服务实例.点位集合[0].克隆();
            候选.名称 = "改名后的点";

            await 服务实例.异步编辑(原名, 候选);

            Assert.DoesNotContain(服务实例.点位集合, 点项 => 点项.名称 == 原名);
            Assert.Equal("改名后的点", 服务实例.点位集合[0].名称);
            Assert.Contains("改名后的点", await File.ReadAllTextAsync(路径), StringComparison.Ordinal);
        }
        finally
        {
            await 还原(路径, 原始);
        }
    }

    [Fact]
    public async Task 改名撞上其它点应被拒绝且不留状态()
    {
        var 路径 = 配置路径.其它点;
        var 原始 = File.Exists(路径) ? await File.ReadAllTextAsync(路径) : null;

        try
        {
            await using var 客户端 = new 替身Modbus客户端();
            var 服务实例 = 新服务(客户端);
            await 服务实例.异步初始化();

            Assert.True(服务实例.点位集合.Count >= 2, "配置里的其它点默认定义不足两个，本用例失去意义");

            var 甲 = 服务实例.点位集合[0];
            var 乙 = 服务实例.点位集合[1];
            var 候选 = 甲.克隆();
            候选.名称 = 乙.名称;

            var 异常 = await Assert.ThrowsAsync<InvalidOperationException>(
                () => 服务实例.异步编辑(甲.名称, 候选));

            Assert.Contains("已存在", 异常.Message);
            Assert.Equal(甲.名称, 服务实例.点位集合[0].名称);
        }
        finally
        {
            await 还原(路径, 原始);
        }
    }

    [Fact]
    public async Task 只改坐标不改名应被接受()
    {
        var 路径 = 配置路径.其它点;
        var 原始 = File.Exists(路径) ? await File.ReadAllTextAsync(路径) : null;

        try
        {
            await using var 客户端 = new 替身Modbus客户端();
            var 服务实例 = 新服务(客户端);
            await 服务实例.异步初始化();

            var 原名 = 服务实例.点位集合[0].名称;
            var 候选 = 服务实例.点位集合[0].克隆();
            候选.X基址 += 2;

            await 服务实例.异步编辑(原名, 候选);

            Assert.Equal(候选.X基址, 服务实例.点位集合[0].X基址);
            Assert.Equal(原名, 服务实例.点位集合[0].名称);
        }
        finally
        {
            await 还原(路径, 原始);
        }
    }

    /// <summary>配置文件是测试输出目录里的共享副本，用完必须还原（原先不存在就删掉）。</summary>
    private static async Task 还原(string 路径, string? 原始)
    {
        if (原始 is null)
        {
            if (File.Exists(路径))
            {
                File.Delete(路径);
            }

            return;
        }

        await File.WriteAllTextAsync(路径, 原始);
    }
}
