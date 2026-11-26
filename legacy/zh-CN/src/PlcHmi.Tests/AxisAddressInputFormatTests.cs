using Microsoft.Extensions.Logging.Abstractions;
using 上位机.业务.配置;
using 上位机.业务.服务;
using 上位机.通信.汇川;
using 上位机.核心.枚举;
using 上位机.上位机测试.替身;

namespace 上位机.上位机测试;

/// <summary>
/// 轴地址的"输入书写形式"与保存链路测试。
///
/// <para><b>守的是什么：</b>参数页「轴速度」表的地址列显示为 <c>D1202</c>，
/// 而现场习惯是把整格全选后直接填寄存器号 <c>1252</c>。服务层的写入路径本来就是宽松解析
/// （<c>TrimStart('D')</c>），若校验只认带前缀写法，就会出现
/// "页面报『不是有效的软元件地址』、服务其实完全能写"的分裂——现场表现就是
/// <b>「改了轴速度地址保存不了」</b>（2026-10-14 反馈）。</para>
///
/// <para>另一半是落盘形态：位置地址会被写进 <c>tags.json</c>，那里必须是<b>软元件符号</b>。
/// 裸数字写进去会让地址解析器解析失败、采集计划静默跳过该点位
/// （"改了地址却不生效"，最难查的一类），所以保存前必须规范成 <c>D{序号}</c>。</para>
///
/// <para>会写测试输出目录里的 <c>config/recipe-config.json</c> 与 <c>config/tags.json</c>，
/// 与其它碰配置文件的用例同集合，禁用并行。</para>
/// </summary>
[Collection("config-files")]
public class 轴地址输入格式测试
{
    private static 轴地址服务 新服务() =>
        new(
            配方配置加载器.加载(out _),
            new 标签仓储(NullLogger<标签仓储>.Instance),
            new 汇川地址解析器(PLC系列.小型PLC),
            new 替身轮询引擎(),
            new 允许全部权限守卫(),
            NullLogger<轴地址服务>.Instance);

    [Fact]
    public void 定位速度地址省前缀应被受理()
    {
        var 服务实例 = 新服务();
        var 候选 = 服务实例.表格.克隆();

        候选.查找("X")!.定位速度地址 = "1252";

        // 校验与写入必须同口径：服务写入用的就是宽松解析
        Assert.True(服务实例.校验(候选).是否有效, "裸寄存器号应按 D 区受理");
    }

    [Fact]
    public void 命令类地址省前缀仍应被拒绝()
    {
        var 服务实例 = 新服务();
        var 候选 = 服务实例.表格.克隆();

        // 点动 / 回零允许 M 区线圈或 D 区字内位：裸数字无法判断意图，必须写明前缀
        候选.查找("X")!.点动正转地址 = "470";

        var 校验项 = 服务实例.校验(候选);

        Assert.False(校验项.是否有效);
        Assert.Contains(校验项.错误集合, 事件参数 =>
            事件参数.Contains("不是有效的软元件地址", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 只改定位速度地址应能落盘并回读()
    {
        var (配方路径, 标签路径, 原始配方, 原始标签) = 备份配置();

        try
        {
            var 服务实例 = 新服务();
            var 候选 = 服务实例.表格.克隆();

            // 模拟参数页「轴速度」表里改定位速度地址（唯一编辑入口）
            候选.查找("X")!.定位速度地址 = "D1252";

            Assert.True(服务实例.校验(候选).是否有效);

            await 服务实例.异步保存(候选);

            Assert.Equal("D1252", 服务实例.表格.查找("X")!.定位速度地址);
            Assert.Contains("\"地址\": 1252", await File.ReadAllTextAsync(配方路径), StringComparison.Ordinal);
        }
        finally
        {
            还原配置(配方路径, 标签路径, 原始配方, 原始标签);
        }
    }

    [Fact]
    public async Task 裸寄存器号保存后应规范为带前缀并写进tags()
    {
        var (配方路径, 标签路径, 原始配方, 原始标签) = 备份配置();

        try
        {
            var 服务实例 = 新服务();
            var 候选 = 服务实例.表格.克隆();

            // 现场习惯：全选地址格后直接填寄存器号（省掉前缀）
            候选.查找("X")!.定位速度地址 = "1252";
            候选.查找("X")!.位置地址 = "1221";

            await 服务实例.异步保存(候选);

            // 回读一律是带前缀的规范写法（界面显示的就是它）
            Assert.Equal("D1252", 服务实例.表格.查找("X")!.定位速度地址);
            Assert.Equal("D1221", 服务实例.表格.查找("X")!.位置地址);

            // 位置地址落在 tags.json，必须是能解析的软元件符号，否则采集计划会静默跳过该点位
            Assert.Contains("\"AxisXPosition\"", await File.ReadAllTextAsync(标签路径), StringComparison.Ordinal);
            Assert.Contains("\"D1221\"", await File.ReadAllTextAsync(标签路径), StringComparison.Ordinal);
        }
        finally
        {
            还原配置(配方路径, 标签路径, 原始配方, 原始标签);
        }
    }

    // ── 配置备份 / 还原 ─────────────────────────────────────────────────

    private static (string 配方路径, string 标签路径, string 原始配方, string 原始标签) 备份配置()
    {
        var 配方路径 = 配置路径.配方配置;
        var 标签路径 = 配置路径.标签集合;

        return (配方路径, 标签路径, File.ReadAllText(配方路径), File.ReadAllText(标签路径));
    }

    private static void 还原配置(string 配方路径, string 标签路径, string 原始配方, string 原始标签)
    {
        File.WriteAllText(配方路径, 原始配方);
        File.WriteAllText(标签路径, 原始标签);
    }
}
