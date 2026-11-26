using Microsoft.Extensions.Logging.Abstractions;
using 上位机.业务.服务;
using 上位机.通信.汇川;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.上位机测试;

/// <summary>
/// 延时表地址区间约束（D1000–D1099）。
///
/// <para><b>背景</b>：D1000–D1500 原先被记为「PLC 公共区、禁止占用」，2026-09-12 需求方确认
/// PLC 侧已释放整段，现划分为 <b>D1000–D1099 延时专属区</b> 与 <b>D1100–D1500 上位机预留段</b>。</para>
///
/// <para>此前延时表只校验"必须是 D 区"，上下限都不管——管理员能填 <c>D0</c> 或 <c>D7000</c>，
/// 撞掉轴速度参数区或示教点表。本文件把这条边界钉在服务层。</para>
/// </summary>
public class 延迟地址范围测试
{
    private static 参数表格服务 新服务()
    {
        var 解析器 = new 汇川地址解析器(PLC系列.小型PLC);

        // Validate 不触碰 TagWriter；只有真正写 PLC 时才用到它
        标签写入器 写入器 = null!;
        return new 参数表格服务(
            解析器, 写入器, NullLogger<参数表格服务>.Instance, new 允许全部权限守卫());
    }

    private static 参数条目定义 延迟条目(string 地址值) => 延迟条目(地址值, "test-delay");

    private static 参数条目定义 延迟条目(string 地址值, string 标识值) => new()
    {
        标识 = 标识值,
        名称 = "测试延时",
        地址 = 地址值,
        单元 = "s",
        最小 = 0,
        最大 = 65.535,
        启用 = true,
    };

    [Theory]
    [InlineData("D1000")]
    [InlineData("D1050")]
    [InlineData("D1098")]     // 2026-10-16 起延时占 2 字（DINT），上限收到 D1098
    public void 专属区内的延时地址不应报区间错误(string 地址值)
    {
        var 结果值 = 新服务().校验(参数表格类别.延迟, 延迟条目(地址值), 是否更新: false);

        Assert.DoesNotContain(结果值.错误集合, 事件参数 => 事件参数.Contains("必须在 D", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("D999")]
    [InlineData("D1099")]     // 会跨到 D1100（上位机预留段）：DINT 占 2 字后不再放行
    [InlineData("D1100")]
    [InlineData("D700")]      // 迁移前的旧地址，现已越界
    [InlineData("D1600")]     // 轴参数区（D1200–D1799）
    [InlineData("D2500")]     // 示教点表区
    public void 专属区外的延时地址应被拒绝(string 地址值)
    {
        var 结果值 = 新服务().校验(参数表格类别.延迟, 延迟条目(地址值), 是否更新: false);

        Assert.False(结果值.是否有效);
        Assert.Contains(结果值.错误集合, 事件参数 => 事件参数.Contains("必须在 D1000", StringComparison.Ordinal));
    }

    [Fact]
    public void 非D区地址应被拒绝()
    {
        var 结果值 = 新服务().校验(参数表格类别.延迟, 延迟条目("M1000"), 是否更新: false);

        Assert.False(结果值.是否有效);
        Assert.Contains(结果值.错误集合, 事件参数 => 事件参数.Contains("D 区", StringComparison.Ordinal));
    }

    [Fact]
    public void 与其它延时重叠的地址应被拒绝()
    {
        // 延时按 DINT 存毫秒、每条占 2 字：D1050 与 D1051 会互相吃掉高位字
        //（表现为"某条延时偶尔变成几万秒"，现场毫无线索），必须当场拒绝。
        var 服务实例 = 新服务();
        服务实例.添加(参数表格类别.延迟, 延迟条目("D1050", "delay-甲"));

        var 重叠 = 服务实例.校验(参数表格类别.延迟, 延迟条目("D1051", "delay-乙"), 是否更新: false);

        Assert.False(重叠.是否有效);
        Assert.Contains(重叠.错误集合, 事件参数 => 事件参数.Contains("重叠", StringComparison.Ordinal));

        // 相隔 2 字则放行（D1052 占用 D1052–D1053，与 D1050–D1051 不冲突）
        var 合法 = 服务实例.校验(参数表格类别.延迟, 延迟条目("D1052", "delay-丙"), 是否更新: false);
        Assert.True(合法.是否有效, 合法.摘要);
    }

    /// <summary>
    /// 出厂配置里已经配好的延时地址必须落在专属区（D1000–D1098）。
    /// <para><b>2026-10-21：</b>现场版出厂配置只用按钮表、延时表为空——
    /// 没有条目时这条自检自然成立，不强行要求"必须有延时"。</para>
    /// </summary>
    [Fact]
    public async Task 配置文件里的延时地址应落在专属区内()
    {
        var 服务实例 = 新服务();
        await 服务实例.异步初始化();

        foreach (var 延迟值 in 服务实例.延迟集合)
        {
            Assert.StartsWith("D", 延迟值.地址, StringComparison.OrdinalIgnoreCase);
            Assert.True(int.TryParse(延迟值.地址.AsSpan(1), out var 编号),
                $"地址格式异常：{延迟值.地址}");

            Assert.InRange(编号, 参数表格服务.延迟地址最小, 参数表格服务.延迟地址最大);
        }
    }
}
