using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using 上位机.业务.配置;
using 上位机.业务.服务;
using 上位机.通信.汇川;
using 上位机.核心.枚举;
using 上位机.核心.模型;
using Xunit.Abstractions;

namespace 上位机.上位机测试;

/// <summary>
/// 管理员编辑配置条目的回归测试。
///
/// <para><b>背景（2026-09-11 需求方反馈）：</b>旧版校验用 <c>ReferenceEquals</c> 排除自身，
/// 但编辑对话框操作的是 Clone 副本，引用比较永远不成立——管理员只要编辑已有条目
/// （改名字、改地址）就弹「已被占用」。本文件锁死这个行为，防止回归。</para>
/// </summary>
/// <remarks>
/// 与其它读写 <c>config/</c> 的用例同集合：本组会临时替换 <c>param-tables.json</c>
/// 并保存 <c>manual-widgets.json</c>，并行跑会和其它用例抢同一个文件。
/// </remarks>
[Collection("config-files")]
public class 管理员编辑测试
{
    private readonly ITestOutputHelper _输出;

    public 管理员编辑测试(ITestOutputHelper 输出) => _输出 = 输出;

    private static 报警定义服务 新报警服务() =>
        new(new 汇川地址解析器(PLC系列.小型PLC),
            NullLogger<报警定义服务>.Instance,
            new 允许全部权限守卫());

    private static 参数表格服务 新参数服务()
    {
        var 解析器 = new 汇川地址解析器(PLC系列.小型PLC);

        // TagWriter 只在真正写 PLC 时用到；Validate 不触碰它。
        标签写入器 写入器 = null!;
        return new 参数表格服务(
            解析器, 写入器, NullLogger<参数表格服务>.Instance, new 允许全部权限守卫());
    }

    private static 手动元件服务 新元件服务() =>
        new(new 汇川地址解析器(PLC系列.小型PLC),
            NullLogger<手动元件服务>.Instance,
            new 允许全部权限守卫());

    // ── 报警编辑 ────────────────────────────────────────────────────────

    [Fact]
    public async Task 编辑已有报警_改名字与地址_不应报已被占用()
    {
        var 服务实例 = 新报警服务();
        await 服务实例.异步初始化();

        var 原始 = 服务实例.定义集合.First(条目项 => 条目项.标识 == "A001");
        var 已编辑 = 原始.克隆();
        Assert.NotSame(原始, 已编辑);   // 编辑走的是克隆副本——旧 bug 的前提

        已编辑.消息 = "新报警文本";
        已编辑.地址 = "M510";

        var 校验项 = 服务实例.校验(已编辑, 是否更新: true);

        Assert.True(校验项.是否有效, $"编辑已有报警不应报错：{校验项.摘要}");
        Assert.DoesNotContain("已被占用", 校验项.摘要);
    }

    [Fact]
    public async Task 新增报警_编号与已有条目相同_仍应被拦截()
    {
        var 服务实例 = 新报警服务();
        await 服务实例.异步初始化();

        var 现有 = 服务实例.定义集合.First(条目项 => 条目项.标识 == "A001");

        // 新增一条编号也叫 A001 的报警——必须拦住，否则列表里出现两条 A001
        var 新用户 = 现有.克隆();
        var 校验项 = 服务实例.校验(新用户, 是否更新: false);

        Assert.False(校验项.是否有效);
        Assert.Contains("已被占用", 校验项.摘要);
    }

    [Fact]
    public async Task 编辑报警_校验通过后Update应能按编号定位()
    {
        var 服务实例 = 新报警服务();
        await 服务实例.异步初始化();

        var 原始 = 服务实例.定义集合.First(条目项 => 条目项.标识 == "A002");
        var 已编辑 = 原始.克隆();
        已编辑.消息 = "更新后的文本";

        Assert.True(服务实例.校验(已编辑, 是否更新: true).是否有效);

        服务实例.更新(已编辑);   // 不应抛「找不到编号」

        Assert.Equal("更新后的文本", 服务实例.定义集合.Single(条目项 => 条目项.标识 == "A002").消息);
    }

    // ── 参数表编辑 ──────────────────────────────────────────────────────

    /// <summary>用例自带两条延时（现场出厂配置只有按钮表，延时表为空）。</summary>
    private const string 测试延时表 = """
        {
          "延迟集合": [
            {
              "标识": "delay-1",
              "名称": "测试延时1",
              "地址": "D1000",
              "单元": "s",
              "最小": 0,
              "最大": 2147483.647,
              "启用": true,
              "顺序": 1
            },
            {
              "标识": "delay-2",
              "名称": "测试延时2",
              "地址": "D1002",
              "单元": "s",
              "最小": 0,
              "最大": 2147483.647,
              "启用": true,
              "顺序": 2
            }
          ],
          "按钮集合": [
            {
              "标识": "btn-1",
              "名称": "MES 开关",
              "地址": "M5000",
              "启用": true,
              "顺序": 1
            }
          ]
        }
        """;

    private static async Task<string> 安装测试延时表()
    {
        var 路径 = 配置路径.参数表格;
        var 原始 = await File.ReadAllTextAsync(路径);
        await File.WriteAllTextAsync(路径, 测试延时表);
        return 原始;
    }

    [Fact]
    public async Task 编辑已有延时条目_改名字与地址_不应报已被占用()
    {
        var 备份 = await 安装测试延时表();

        try
        {
            var 服务实例 = 新参数服务();
            await 服务实例.异步初始化();

            var 原始 = 服务实例.延迟集合.First(条目项 => 条目项.标识 == "delay-1");
            var 已编辑 = 原始.克隆();

            已编辑.名称 = "改过的延时名";
            已编辑.地址 = "D1050";   // 延时专属区 D1000–D1099 内

            var 校验项 = 服务实例.校验(参数表格类别.延迟, 已编辑, 是否更新: true);

            Assert.True(校验项.是否有效, $"编辑已有延时不应报错：{校验项.摘要}");
        }
        finally
        {
            await File.WriteAllTextAsync(配置路径.参数表格, 备份);
        }
    }

    [Fact]
    public async Task 编辑已有按钮条目_不应报已被占用()
    {
        var 服务实例 = 新参数服务();
        await 服务实例.异步初始化();

        var 原始 = 服务实例.按钮集合.First(条目项 => 条目项.标识 == "btn-1");
        var 已编辑 = 原始.克隆();
        已编辑.名称 = "MES 开关（改）";
        已编辑.地址 = "M5001";

        var 校验项 = 服务实例.校验(参数表格类别.按钮, 已编辑, 是否更新: true);

        Assert.True(校验项.是否有效, $"编辑已有按钮不应报错：{校验项.摘要}");
    }

    [Fact]
    public async Task 新增参数条目_编号与已有条目相同_仍应被拦截()
    {
        var 备份 = await 安装测试延时表();

        try
        {
            var 服务实例 = 新参数服务();
            await 服务实例.异步初始化();

            var 新用户 = 服务实例.延迟集合.First().克隆();
            var 校验项 = 服务实例.校验(参数表格类别.延迟, 新用户, 是否更新: false);

            Assert.False(校验项.是否有效);
            Assert.Contains("已被占用", 校验项.摘要);
        }
        finally
        {
            await File.WriteAllTextAsync(配置路径.参数表格, 备份);
        }
    }

    [Fact]
    public async Task 服务层按钮置位校验_同一条目重复校验_不应报已被占用()
    {
        // SetButtonAsync / WriteDelayAsync 内部会再校验一次（isUpdate: true 路径）。
        // 这里直接模拟同一条目反复 Validate：旧实现对列表内原始对象也会自撞。
        var 服务实例 = 新参数服务();
        await 服务实例.异步初始化();

        var 待校验条目 = 服务实例.按钮集合.First();

        for (var 轮次 = 0; 轮次 < 3; 轮次++)
        {
            var 校验项 = 服务实例.校验(参数表格类别.按钮, 待校验条目, 是否更新: true);
            Assert.True(校验项.是否有效, $"第 {轮次 + 1} 次校验不应报错：{校验项.摘要}");
        }
    }

    // ── 手动页气缸编辑（需求方反馈「管理员无法编辑气缸」的业务层实证） ──

    [Fact]
    public async Task 编辑气缸_改名字与地址_校验通过且能定位更新()
    {
        var 服务实例 = 新元件服务();
        await 服务实例.异步初始化();

        // 现场配置里 cyl-1 是"螺丝机开盖"按钮，气缸是 cyl-2（螺丝机定位气缸）
        var 原始 = 服务实例.元件集合.First(元件条目 => 元件条目.标识 == "cyl-2");
        Assert.Equal(手动元件类型.气缸, 原始.类型);

        // 与 ManualViewModel.EditWidgetAsync 相同的编辑路径：Clone → 改字段 → Validate → Update
        var 已编辑 = 原始.克隆();
        已编辑.名称 = "上料气缸（改名）";
        已编辑.地址 = "M226";

        var 校验项 = 服务实例.校验(已编辑);
        Assert.True(校验项.是否有效, $"气缸编辑校验不应报错：{校验项.摘要}");

        服务实例.更新(已编辑);   // 不应抛「找不到 Id」

        var 已更新 = 服务实例.元件集合.Single(元件条目 => 元件条目.标识 == "cyl-2");
        Assert.Equal("上料气缸（改名）", 已更新.名称);
        Assert.Equal("M226", 已更新.地址);
    }

    [Fact]
    public async Task 编辑气缸_保存重载后改动应持久化()
    {
        var 服务实例 = 新元件服务();
        await 服务实例.异步初始化();

        var 原始 = 服务实例.元件集合.First(元件条目 => 元件条目.标识 == "cyl-2");
        var 已编辑 = 原始.克隆();
        已编辑.名称 = "压装气缸（改名）";

        Assert.True(服务实例.校验(已编辑).是否有效);
        服务实例.更新(已编辑);
        await 服务实例.异步保存();

        // 新建一个服务实例重新加载，验证改动真的落了盘
        var 已重载 = 新元件服务();
        await 已重载.异步初始化();

        var 已落盘 = 已重载.元件集合.Single(元件条目 => 元件条目.标识 == "cyl-2");
        Assert.Equal("压装气缸（改名）", 已落盘.名称);

        _输出.WriteLine($"落盘验证通过：{已落盘.标识} → {已落盘.名称} @ {已落盘.地址}");
    }

    [Fact]
    public void 气缸绑定非M区地址_校验应拒绝()
    {
        var 服务实例 = 新元件服务();

        var 元件项 = new 手动元件定义
        {
            标识 = "cyl-x", 类型 = 手动元件类型.气缸, 名称 = "非法气缸", 顺序 = 9,
            地址 = "Y0",   // Y 区专供输出，上位机不允许直接操作
        };

        var 校验项 = 服务实例.校验(元件项);

        Assert.False(校验项.是否有效);
        Assert.Contains("M 区", 校验项.摘要);
    }
}
