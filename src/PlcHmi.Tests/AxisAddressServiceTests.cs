using Microsoft.Extensions.Logging.Abstractions;
using 上位机.业务.配置;
using 上位机.业务.服务;
using 上位机.通信.汇川;
using 上位机.核心.枚举;
using 上位机.上位机测试.替身;

namespace 上位机.上位机测试;

/// <summary>
/// 轴地址保存会写测试输出目录里的 <c>config/recipe-config.json</c> 与 <c>config/tags.json</c>，
/// 与其它读取配置的测试并行跑会互相看到半途的配置。本集合禁用并行。
/// </summary>
[CollectionDefinition("config-files", DisableParallelization = true)]
public sealed class 配置文件集合
{
    // 仅作为集合标记，不需要成员
}

/// <summary>
/// 轴地址管理的服务层校验测试。
///
/// <para><b>2026-10-12 变更：</b>按现场要求<b>取消地址范围限制</b>——
/// 不再校验轴参数区（D1200–D1799）、PLC 保留区与 tags.json 其它点位。
/// 因此原先"越界 / 保留区 / PLC 自有字段 / 与命令位冲突"那批用例已随规则一并移除；
/// 保留的是与"范围"无关的判定：地址能否解析、元素类型、本次提交内是否重叠。</para>
/// </summary>
[Collection("config-files")]
public class 轴地址服务测试
{
    private static 汇川地址解析器 解析器() => new(PLC系列.小型PLC);

    private static 轴地址服务 新服务(替身轮询引擎 轮询) =>
        new(
            配方配置加载器.加载(out _),
            new 标签仓储(NullLogger<标签仓储>.Instance),
            解析器(),
            轮询,
            new 允许全部权限守卫(),
            NullLogger<轴地址服务>.Instance);

    [Fact]
    public void 默认表应读齐六轴与回零地址()
    {
        var 服务实例 = 新服务(new 替身轮询引擎());

        // 点胶锁付机六轴：锁附 X/Y/Z/ZZ + 点胶 X/Z
        Assert.Equal(6, 服务实例.表格.轴列表.Count);
        Assert.Equal(
            new[] { "X", "Y", "Z", "ZZ", "GlueX", "GlueZ" },
            服务实例.表格.轴列表.Select(条目项 => 条目项.轴).ToArray());

        var 坐标X = 服务实例.表格.查找("X");
        Assert.NotNull(坐标X);
        // 2026-10-21 现场配方：定位速度 D1400 起、位置 D100 起、点动档位共用 D15
        Assert.Equal("D1400", 坐标X!.定位速度地址);
        Assert.Equal("D15", 坐标X.点动速度等级地址);
        Assert.Equal("D100", 坐标X.位置地址);

        // 点动：配置（jogButtons）优先于 tags.json，六轴一律走 M 区线圈
        Assert.Equal("M1002", 坐标X.点动正转地址);
        Assert.Equal("M1003", 坐标X.点动反转地址);
        Assert.Equal("M440", 坐标X.回零地址);

        Assert.Equal("M1152", 服务实例.表格.查找("ZZ")!.点动正转地址);
        Assert.Equal("M1252", 服务实例.表格.查找("GlueX")!.点动正转地址);
        Assert.Equal("M1202", 服务实例.表格.查找("GlueZ")!.点动正转地址);
        Assert.Equal("M445", 服务实例.表格.查找("GlueZ")!.回零地址);
    }

    [Fact]
    public void 默认表校验应通过()
    {
        var 服务实例 = 新服务(new 替身轮询引擎());

        // 同字不同位（D1210.2 / D1210.3）不应被判为重叠；无位置回读的轴允许留空
        var 校验项 = 服务实例.校验(服务实例.表格.克隆());

        Assert.True(校验项.是否有效, 校验项.摘要);
    }

    [Fact]
    public void 取消范围限制后轴参数区外地址应被接受()
    {
        var 服务实例 = 新服务(new 替身轮询引擎());
        var 候选 = 服务实例.表格.克隆();

        // 旧规则会拒绝这些地址（D1000 段 / 越界段），现在只要求"是 D 区且不重叠"
        候选.查找("X")!.定位速度地址 = "D1050";
        候选.查找("Y")!.定位速度地址 = "D2000";
        // 档位地址六轴共用（2026-10-13 现场要求）：改就一起改，保持一致
        foreach (var 轴项 in 候选.轴列表)
        {
            轴项.点动速度等级地址 = "D3000";
        }

        var 校验项 = 服务实例.校验(候选);

        Assert.True(校验项.是否有效, 校验项.摘要);
    }

    [Fact]
    public void 点动地址允许D字内位()
    {
        var 服务实例 = 新服务(new 替身轮询引擎());
        var 候选 = 服务实例.表格.克隆();

        候选.查找("X")!.点动正转地址 = "D1210.5";

        var 校验项 = 服务实例.校验(候选);

        Assert.True(校验项.是否有效, 校验项.摘要);
    }

    [Fact]
    public void 点动地址整字D区应被拒绝()
    {
        var 服务实例 = 新服务(new 替身轮询引擎());
        var 候选 = 服务实例.表格.克隆();

        // 整字写法（无 .位）：点动是位命令，写整字会覆盖同字的其它命令位
        候选.查找("X")!.点动正转地址 = "D230";

        var 校验项 = 服务实例.校验(候选);

        Assert.False(校验项.是否有效);
        Assert.Contains(校验项.错误集合, 事件参数 =>
            事件参数.Contains("必须是 M 区地址或 D/R 区字内位", StringComparison.Ordinal));
    }

    [Fact]
    public void 位置地址留空应被接受()
    {
        var 服务实例 = 新服务(new 替身轮询引擎());
        var 候选 = 服务实例.表格.克隆();

        候选.查找("ZZ")!.位置地址 = string.Empty;

        var 校验项 = 服务实例.校验(候选);

        Assert.True(校验项.是否有效, 校验项.摘要);
    }

    [Fact]
    public void 定位速度留空应被拒绝()
    {
        var 服务实例 = 新服务(new 替身轮询引擎());
        var 候选 = 服务实例.表格.克隆();

        候选.查找("X")!.定位速度地址 = string.Empty;

        var 校验项 = 服务实例.校验(候选);

        Assert.False(校验项.是否有效);
        Assert.Contains(校验项.错误集合, 事件参数 => 事件参数.Contains("不能为空", StringComparison.Ordinal));
    }

    [Fact]
    public void 非法地址应被拒绝()
    {
        var 服务实例 = 新服务(new 替身轮询引擎());
        var 候选 = 服务实例.表格.克隆();

        候选.查找("X")!.位置地址 = "X123";

        var 校验项 = 服务实例.校验(候选);

        Assert.False(校验项.是否有效);
    }

    [Fact]
    public void 同字不同位不算重叠_同位才算()
    {
        var 服务实例 = 新服务(new 替身轮询引擎());
        var 候选 = 服务实例.表格.克隆();

        // 同一字的两个不同位：D1210.2 与 D1210.3 互不冲突
        候选.查找("X")!.点动正转地址 = "D1210.2";
        候选.查找("Y")!.点动正转地址 = "D1210.3";

        Assert.True(服务实例.校验(候选).是否有效);

        // 同一位才冲突
        候选.查找("Y")!.点动正转地址 = "D1210.2";

        var 校验项 = 服务实例.校验(候选);

        Assert.False(校验项.是否有效);
        Assert.Contains(校验项.错误集合, 事件参数 => 事件参数.Contains("重叠", StringComparison.Ordinal));
    }

    [Fact]
    public void 回零与点动共用同一线圈应被判重叠()
    {
        var 服务实例 = 新服务(new 替身轮询引擎());
        var 候选 = 服务实例.表格.克隆();

        候选.查找("X")!.回零地址 = 候选.查找("Y")!.回零地址;

        var 校验项 = 服务实例.校验(候选);

        Assert.False(校验项.是否有效);
        Assert.Contains(校验项.错误集合, 事件参数 => 事件参数.Contains("重叠", StringComparison.Ordinal));
    }

    [Fact]
    public void 两轴地址重叠应被拒绝()
    {
        var 服务实例 = 新服务(new 替身轮询引擎());
        var 候选 = 服务实例.表格.克隆();

        候选.查找("Y")!.定位速度地址 = 候选.查找("X")!.定位速度地址;

        var 校验项 = 服务实例.校验(候选);

        Assert.False(校验项.是否有效);
        Assert.Contains(校验项.错误集合, 事件参数 => 事件参数.Contains("重叠", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 保存合法地址应落盘并触发采集计划重建()
    {
        var 配方路径 = 配置路径.配方配置;
        var 标签路径 = 配置路径.标签集合;

        var 原始配方 = await File.ReadAllTextAsync(配方路径);
        var 原始标签 = await File.ReadAllTextAsync(标签路径);

        try
        {
            var 轮询 = new 替身轮询引擎();
            var 服务实例 = 新服务(轮询);

            var 候选 = 服务实例.表格.克隆();

            // 档位地址六轴共用：改就一起改
        foreach (var 轴项 in 候选.轴列表)
        {
            轴项.点动速度等级地址 = "D1213";
        }

            var 校验项 = 服务实例.校验(候选);
            Assert.True(校验项.是否有效, 校验项.摘要);

            await 服务实例.异步保存(候选);

            Assert.Equal(1, 轮询.重建数量);
            Assert.Equal("D1213", 服务实例.表格.查找("X")!.点动速度等级地址);
            Assert.Contains("\"地址\": 1213", await File.ReadAllTextAsync(配方路径), StringComparison.Ordinal);
        }
        finally
        {
            // 配置文件是测试输出目录里的共享副本，必须还原，否则会污染其它测试
            await File.WriteAllTextAsync(配方路径, 原始配方);
            await File.WriteAllTextAsync(标签路径, 原始标签);
        }
    }

    [Fact]
    public async Task 保存重叠地址应抛异常且不落盘()
    {
        var 配方路径 = 配置路径.配方配置;
        var 原始 = await File.ReadAllTextAsync(配方路径);

        try
        {
            var 轮询 = new 替身轮询引擎();
            var 服务实例 = 新服务(轮询);

            var 候选 = 服务实例.表格.克隆();
            候选.查找("Y")!.定位速度地址 = 候选.查找("X")!.定位速度地址;

            await Assert.ThrowsAsync<InvalidOperationException>(() => 服务实例.异步保存(候选));

            Assert.Equal(0, 轮询.重建数量);
            Assert.Equal(原始, await File.ReadAllTextAsync(配方路径));
        }
        finally
        {
            await File.WriteAllTextAsync(配方路径, 原始);
        }
    }

    [Fact]
    public async Task 保存回零地址应写回配置()
    {
        var 配方路径 = 配置路径.配方配置;
        var 标签路径 = 配置路径.标签集合;

        var 原始配方 = await File.ReadAllTextAsync(配方路径);
        var 原始标签 = await File.ReadAllTextAsync(标签路径);

        try
        {
            var 服务实例 = 新服务(new 替身轮询引擎());
            var 候选 = 服务实例.表格.克隆();

            候选.查找("X")!.回零地址 = "M490";
            await 服务实例.异步保存(候选);

            Assert.Equal("M490", 服务实例.表格.查找("X")!.回零地址);
            Assert.Contains("\"M490\"", await File.ReadAllTextAsync(配方路径), StringComparison.Ordinal);
        }
        finally
        {
            await File.WriteAllTextAsync(配方路径, 原始配方);
            await File.WriteAllTextAsync(标签路径, 原始标签);
        }
    }
}
