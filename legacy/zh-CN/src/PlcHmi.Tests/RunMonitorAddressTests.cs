using Microsoft.Extensions.Logging.Abstractions;
using 上位机.业务.服务;
using 上位机.通信.汇川;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;
using 上位机.上位机测试.替身;
using Xunit.Abstractions;

namespace 上位机.上位机测试;

/// <summary>
/// 运行监控页元件地址管理的回归测试。
///
/// <para>覆盖：默认元件表与需求地址、D/M 区校验、重叠与冲突拦截、
/// 保存链路（更新点位 → 落盘 → 重建采集计划）与权限兜底。</para>
/// </summary>
public class 运行监控地址测试
{
    private readonly ITestOutputHelper _输出;

    public 运行监控地址测试(ITestOutputHelper 输出) => _输出 = 输出;

    private static (运行监控地址服务 服务, 替身标签仓储 点位, 替身轮询引擎 轮询) 新服务(
        权限守卫接口? 守卫 = null, 替身标签仓储? 点位表 = null)
    {
        var 表 = 点位表 ?? 替身标签仓储.运行页默认点位();
        var 轮询 = new 替身轮询引擎();
        var 服务实例 = new 运行监控地址服务(
            表,
            new 汇川地址解析器(PLC系列.小型PLC),
            轮询,
            守卫 ?? new 允许全部权限守卫(),
            NullLogger<运行监控地址服务>.Instance);
        return (服务实例, 表, 轮询);
    }

    // ── 默认元件表 ──────────────────────────────────────────────────────

    [Fact]
    public void 默认元件表_应包含全部15个元件且默认地址符合现场规划()
    {
        var (服务实例, _, _) = 新服务();

        var 期望地址 = new Dictionary<string, string>
        {
            ["RunStatus"] = "D6",
            ["CycleTime"] = "D1100",
            ["OutputCount"] = "D1102",
            ["TargetCount"] = "D1104",
            ["GoodCount"] = "D1106",
            ["BadCount"] = "D1108",
            // 左下状态条三盏灯（M 区位）+ 已锁附颗数（D 区 INT）（2026-10-16 现场要求）
            ["ScrewDone"] = "M490",
            ["GlueDone"] = "M491",
            ["ProcessDone"] = "M492",
            ["ScrewCount"] = "D1116",
            ["CmdDataClear"] = "M1000",
            ["CmdStart"] = "M200",
            ["CmdStop"] = "M201",
            ["CmdReset"] = "M202",
            ["CmdAlarmClear"] = "M203",
        };

        Assert.Equal(期望地址.Count, 服务实例.元件集合.Count);

        foreach (var (标签名, 地址) in 期望地址)
        {
            var 元件项 = 服务实例.元件集合.Single(条目 => 条目.标签名 == 标签名);
            Assert.Equal(地址, 元件项.默认地址);
            Assert.Equal(地址, 元件项.地址);   // 默认点位表下当前地址 = 默认地址
        }

        // 命令类与监控量的划分要与校验规则一致
        Assert.Equal(10, 服务实例.元件集合.Count(条目 => !条目.是命令));
        Assert.Equal(5, 服务实例.元件集合.Count(条目 => 条目.是命令));

        // 三盏工位灯与颗数都是"只读监控量"：写错成 M 区会被校验拦下
        foreach (var 标签名 in new[] { "ScrewDone", "GlueDone", "ProcessDone", "ScrewCount" })
        {
            Assert.False(服务实例.元件集合.Single(条目 => 条目.标签名 == 标签名).是命令);
        }
    }

    [Fact]
    public void 工位状态灯填D区地址_校验应拒绝()
    {
        // 三盏灯是 M 区位（FC01 读线圈）：填 D 区会去读一个字的位，值对不上
        var (服务实例, _, _) = 新服务();

        var 候选 = 服务实例.元件集合.Select(条目 => 条目.克隆()).ToList();
        候选.First(条目 => 条目.标签名 == "ScrewDone").地址 = "D1110";

        var 校验项 = 服务实例.校验(候选);

        Assert.False(校验项.是否有效);
        Assert.Contains(校验项.错误集合, 错误 => 错误.Contains("锁附完成") && 错误.Contains("M 区"));
    }

    [Fact]
    public void 已锁附颗数填M区地址_校验应拒绝()
    {
        // 颗数是 D 区 INT：M 区位装不下一个计数
        var (服务实例, _, _) = 新服务();

        var 候选 = 服务实例.元件集合.Select(条目 => 条目.克隆()).ToList();
        候选.First(条目 => 条目.标签名 == "ScrewCount").地址 = "M495";

        var 校验项 = 服务实例.校验(候选);

        Assert.False(校验项.是否有效);
        Assert.Contains(校验项.错误集合, 错误 => 错误.Contains("已锁附颗数") && 错误.Contains("D 区"));
    }

    [Fact]
    public void 工位状态元件与其它监控量重叠_校验应拒绝()
    {
        var (服务实例, _, _) = 新服务();

        // 已锁附颗数与良品数量落在同一地址：两条采集会互相覆盖读到的值
        var 候选 = 服务实例.元件集合.Select(条目 => 条目.克隆()).ToList();
        候选.First(条目 => 条目.标签名 == "ScrewCount").地址 = "D1106";

        var 校验项 = 服务实例.校验(候选);

        Assert.False(校验项.是否有效);
        Assert.Contains(校验项.错误集合, 错误 => 错误.Contains("重叠"));
    }

    [Fact]
    public void 默认地址表_校验应全部通过()
    {
        var (服务实例, _, _) = 新服务();

        var 校验项 = 服务实例.校验(服务实例.元件集合.Select(条目 => 条目.克隆()).ToList());

        Assert.True(校验项.是否有效, $"出厂默认地址必须能通过自己的校验：{校验项.摘要}");
    }

    // ── 校验规则 ────────────────────────────────────────────────────────

    [Fact]
    public void 监控量填M区地址_校验应拒绝()
    {
        var (服务实例, _, _) = 新服务();

        var 候选 = 服务实例.元件集合.Select(条目 => 条目.克隆()).ToList();
        候选.First(条目 => 条目.标签名 == "RunStatus").地址 = "M300";

        var 校验项 = 服务实例.校验(候选);

        Assert.False(校验项.是否有效);
        Assert.Contains(校验项.错误集合, 错误 => 错误.Contains("M 区"));
    }

    [Fact]
    public void 命令填D区地址_校验应拒绝()
    {
        var (服务实例, _, _) = 新服务();

        var 候选 = 服务实例.元件集合.Select(条目 => 条目.克隆()).ToList();
        候选.First(条目 => 条目.标签名 == "CmdStart").地址 = "D1150";

        var 校验项 = 服务实例.校验(候选);

        Assert.False(校验项.是否有效);
        Assert.Contains(校验项.错误集合, 错误 => 错误.Contains("M 区"));
    }

    [Fact]
    public void 候选地址相互重叠_校验应拒绝()
    {
        var (服务实例, _, _) = 新服务();

        // 节拍 D1100 是 UInt32 占 2 字（D1100–D1101），当前产量挪到 D1101 即重叠
        var 候选 = 服务实例.元件集合.Select(条目 => 条目.克隆()).ToList();
        候选.First(条目 => 条目.标签名 == "OutputCount").地址 = "D1101";

        var 校验项 = 服务实例.校验(候选);

        Assert.False(校验项.是否有效);
        Assert.Contains(校验项.错误集合, 错误 => 错误.Contains("重叠"));
    }

    [Fact]
    public void 候选地址与其它点位冲突_校验应拒绝()
    {
        var (服务实例, _, _) = 新服务();

        // D1221 是 AxisXPosition（Float32 占 2 字），不能被监控量占用
        var 候选 = 服务实例.元件集合.Select(条目 => 条目.克隆()).ToList();
        候选.First(条目 => 条目.标签名 == "RunStatus").地址 = "D1221";

        var 校验项 = 服务实例.校验(候选);

        Assert.False(校验项.是否有效);
        Assert.Contains(校验项.错误集合, 错误 => 错误.Contains("AxisXPosition"));
    }

    [Fact]
    public void 命令落入报警位保留区_校验应拒绝()
    {
        var (服务实例, _, _) = 新服务();

        var 候选 = 服务实例.元件集合.Select(条目 => 条目.克隆()).ToList();
        候选.First(条目 => 条目.标签名 == "CmdDataClear").地址 = "M510";

        var 校验项 = 服务实例.校验(候选);

        Assert.False(校验项.是否有效);
        Assert.Contains(校验项.错误集合, 错误 => 错误.Contains("报警位区"));
    }

    [Fact]
    public void 地址为空或无法解析_校验应拒绝()
    {
        var (服务实例, _, _) = 新服务();

        var 候选 = 服务实例.元件集合.Select(条目 => 条目.克隆()).ToList();
        候选.First(条目 => 条目.标签名 == "GoodCount").地址 = "";
        候选.First(条目 => 条目.标签名 == "BadCount").地址 = "X不是地址";

        var 校验项 = 服务实例.校验(候选);

        Assert.False(校验项.是否有效);
        Assert.Equal(2, 校验项.错误集合.Count);
    }

    // ── 保存链路 ────────────────────────────────────────────────────────

    [Fact]
    public async Task 保存通过后_点位地址应更新_落盘并重建采集计划()
    {
        var (服务实例, 点位表, 轮询) = 新服务();

        var 候选 = 服务实例.元件集合.Select(条目 => 条目.克隆()).ToList();
        候选.First(条目 => 条目.标签名 == "RunStatus").地址 = "D100";
        候选.First(条目 => 条目.标签名 == "CmdDataClear").地址 = "M1050";

        var 事件触发 = false;
        服务实例.地址已变更 += (_, _) => 事件触发 = true;

        await 服务实例.异步保存(候选);

        Assert.Equal("D100", 点位表.查找("RunStatus")!.地址);
        Assert.Equal("M1050", 点位表.查找("CmdDataClear")!.地址);
        Assert.Equal(1, 点位表.保存次数);          // 原子落盘（测试替身记录调用）
        Assert.Equal(1, 轮询.重建数量);             // 采集计划重建，新地址即刻生效
        Assert.True(事件触发);
        Assert.Equal("D100", 服务实例.查找地址("RunStatus"));

        _输出.WriteLine("保存链路完整：更新点位 → 落盘 → 重建采集计划均被触发");
    }

    [Fact]
    public async Task 保存校验不通过_应抛异常且不写入任何点位()
    {
        var (服务实例, 点位表, 轮询) = 新服务();

        var 候选 = 服务实例.元件集合.Select(条目 => 条目.克隆()).ToList();
        候选.First(条目 => 条目.标签名 == "RunStatus").地址 = "M300";   // 监控量不许用 M 区

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => 服务实例.异步保存(候选));

        Assert.Equal("D6", 点位表.查找("RunStatus")!.地址);   // 原地址未被污染
        Assert.Equal(0, 点位表.保存次数);
        Assert.Equal(0, 轮询.重建数量);
    }

    [Fact]
    public async Task 现场缺点位_保存时应按出厂定义补建并落盘()
    {
        // 复刻"老现场升级"：现场 tags.json 属于现场文件、不会被新版覆盖，
        // 里面没有新版自带的工位状态三灯与已锁附颗数。若保存直接失败，
        // 管理员就只能手工编辑 JSON——这条用例钉住"运行时补建"这条退路。
        var 旧点位表 = new 替身标签仓储(
            new 标签定义 { 名称 = "RunStatus", 地址 = "D6", 数据类型 = 标签数据类型.UInt16, 访问 = 标签访问.读取, 分组 = 标签分组.快 },
            new 标签定义 { 名称 = "CycleTime", 地址 = "D1100", 数据类型 = 标签数据类型.UInt32, 访问 = 标签访问.读取, 分组 = 标签分组.快 },
            new 标签定义 { 名称 = "CmdStart", 地址 = "M200", 数据类型 = 标签数据类型.布尔, 访问 = 标签访问.写入, 分组 = 标签分组.命令 });

        var (服务实例, 点位表, 轮询) = 新服务(点位表: 旧点位表);

        var 候选 = 服务实例.元件集合.Select(条目 => 条目.克隆()).ToList();
        await 服务实例.异步保存(候选);

        // 缺的四个工位状态点位都补建了；现场已有的点位不得被重建（不覆盖现场配置）
        foreach (var 名称 in new[] { "ScrewDone", "GlueDone", "ProcessDone", "ScrewCount" })
        {
            Assert.Contains(名称, 点位表.补建记录);
        }

        Assert.DoesNotContain("RunStatus", 点位表.补建记录);
        Assert.DoesNotContain("CmdStart", 点位表.补建记录);

        var 颗数点 = 点位表.查找("ScrewCount")!;
        Assert.Equal("D1116", 颗数点.地址);
        Assert.Equal(标签数据类型.Int16, 颗数点.数据类型);
        Assert.Equal(标签分组.快, 颗数点.分组);
        Assert.Equal(标签访问.读取, 颗数点.访问);   // 只读监控量：不得写成可写点位

        // 三盏灯按出厂定义补成 M 区位（布尔 / 只读）：补成 D 区就永远读不到
        var 锁附点 = 点位表.查找("ScrewDone")!;
        Assert.Equal("M490", 锁附点.地址);
        Assert.Equal(标签数据类型.布尔, 锁附点.数据类型);
        Assert.Equal(标签访问.读取, 锁附点.访问);

        // 补建的点位随整表保存落盘，采集计划也重建了
        Assert.Equal(1, 点位表.保存次数);
        Assert.Equal(1, 轮询.重建数量);
    }

    [Fact]
    public async Task 无编辑绑定地址权限_保存应被拒绝()
    {
        // 模拟"操作员越权调用服务层"：守卫直接拒绝一切
        var 拒绝守卫 = new 拒绝全部权限守卫();
        var (服务实例, 点位表, 轮询) = 新服务(拒绝守卫);

        var 候选 = 服务实例.元件集合.Select(条目 => 条目.克隆()).ToList();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => 服务实例.异步保存(候选));

        Assert.Equal(0, 点位表.保存次数);
        Assert.Equal(0, 轮询.重建数量);
    }

    /// <summary>全部拒绝的权限守卫（模拟无权限身份）。</summary>
    private sealed class 拒绝全部权限守卫 : 权限守卫接口
    {
        public void 要求(权限 权限项) =>
            throw new UnauthorizedAccessException($"测试守卫：缺少 {权限项} 权限。");
    }
}
