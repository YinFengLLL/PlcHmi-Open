using Microsoft.Extensions.Logging.Abstractions;
using 上位机.业务.配置;
using 上位机.业务.服务;
using 上位机.通信.汇川;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;
using 上位机.上位机测试.替身;

namespace 上位机.上位机测试;

/// <summary>
/// 批次 F 回归：辅助地址「分组 + 键」定位与每组 <see cref="辅助地址项.每组上限"/> 个上限。
///
/// <para><b>守的缺陷</b>：<c>应用辅助地址</c> 旧逻辑全局按键 <c>FirstOrDefault</c> 就地更新，
/// 撞键即覆盖旧项；<c>移除</c> 旧逻辑全局按键 <c>RemoveAll</c>，会误删跨组同名项。
/// 契约是「键（同一分组内唯一）」，所以定位与删除都必须同时匹配分组。</para>
///
/// <para><b>为什么只测服务层</b>：测试项目只引用 Core / Communication / Business，
/// 没有引用 App（WPF），对话框里的「下一个未占用键」私有方法无法在此直接调用，
/// 其键唯一化靠代码审查保证；这里测的是服务层「分组 + 键」与 30 上限两条硬约束。</para>
///
/// <para><b>配置隔离</b>：用自建的 <see cref="配方配置"/> 直接构造真实服务，
/// 落盘仍会写测试输出目录的 <c>config/recipe-config.json</c>，故每个用例都备份 / 还原；
/// 加入 <c>config-files</c> 集合以禁用并行，避免与其它配置用例互相看到半途状态。</para>
/// </summary>
[Collection("config-files")]
public class 辅助地址上限测试
{
    private const string 锁附分组 = "锁附";
    private const string 点胶分组 = "点胶";
    private const string 标定分组 = "标定";

    // ── 构造 ────────────────────────────────────────────────────────────

    /// <summary>构造一个最小但可保存的配置：点表要有「锁附 / 点胶」两页（点表页保存要查表）。</summary>
    private static 配方配置 新配置(IEnumerable<辅助地址项> 辅助地址表, IEnumerable<动作按钮项>? 动作按钮表 = null)
    {
        return new 配方配置
        {
            点表 = new 点表配置
            {
                点表 =
                [
                    new 点表定义 { 键 = 锁附分组, 名称 = 锁附分组, 点动分组 = 锁附分组 },
                    new 点表定义 { 键 = 点胶分组, 名称 = 点胶分组, 点动分组 = 点胶分组 },
                ],
            },
            辅助地址 = 辅助地址表.ToList(),
            动作按钮 = 动作按钮表 is null ? new List<动作按钮项>() : 动作按钮表.ToList(),
        };
    }

    /// <summary>用自建配置构造真实服务（不实现 页面地址服务接口 替身，避免接口新增方法时编译失败）。</summary>
    private static 页面地址服务 新服务(配方配置 配置对象, Modbus客户端接口 连接)
    {
        var 解析器 = new 汇川地址解析器(PLC系列.小型PLC);

        return new 页面地址服务(
            连接,
            new 标签写入器(连接, new 标签仓储(NullLogger<标签仓储>.Instance), 解析器,
                NullLogger<标签写入器>.Instance),
            配置对象,
            new 允许全部权限守卫(),
            NullLogger<页面地址服务>.Instance);
    }

    private static 辅助地址项 辅助项(string 键, string 分组, string 地址 = "D3900", string 名称 = "") =>
        new()
        {
            键 = 键,
            名称 = string.IsNullOrEmpty(名称) ? 键 : 名称,
            分组 = 分组,
            地址 = 地址,
        };

    private static 地址编辑项 编辑项(
        string 键, string 分组, string 地址 = "D3900", string 名称 = "", bool 新增 = false) =>
        new()
        {
            类别 = "辅助地址",
            键 = 键,
            名称 = string.IsNullOrEmpty(名称) ? 键 : 名称,
            地址文本 = 地址,
            数据类型文本 = "Float32",
            分组 = 分组,
            可删除 = true,
            新增 = 新增,
        };

    private static 地址编辑项 按钮编辑项(string 键, string 分组, string 地址 = "M490", string 名称 = "") =>
        new()
        {
            类别 = "动作按钮",
            键 = 键,
            名称 = string.IsNullOrEmpty(名称) ? 键 : 名称,
            地址文本 = 地址,
            数据类型文本 = "布尔",
            分组 = 分组,
            可删除 = true,
        };

    private static 动作按钮项 按钮项(string 键, string 分组, bool 固定 = false, string 地址 = "M490") =>
        new()
        {
            键 = 键,
            名称 = 键,
            分组 = 分组,
            地址 = 地址,
            固定 = 固定,
        };

    /// <summary>两个保存入口都走同一套应用 / 校验逻辑，用 Theory 一次覆盖。</summary>
    private static Task 保存(页面地址服务 服务实例, bool 标定页, 地址编辑请求 请求) =>
        标定页 ? 服务实例.异步保存标定页(请求) : 服务实例.异步保存点表页(锁附分组, 请求);

    private static async Task<string> 备份配方() =>
        await File.ReadAllTextAsync(配置路径.配方配置);

    private static async Task 还原配方(string 原始) =>
        await File.WriteAllTextAsync(配置路径.配方配置, 原始);

    // ── 用例 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 已有 <c>辅助地址9</c>（锁附组）时，新增一个同键但不同分组（标定组）的项：
    /// 旧逻辑全局按键命中旧项就地改掉 → 旧项被覆盖、总数不变；新逻辑按「分组 + 键」应新建。
    /// </summary>
    [Fact]
    public async Task 同键不同分组的辅助地址新增不应覆盖旧项()
    {
        var 原始 = await 备份配方();

        try
        {
            var 配置 = 新配置([辅助项("辅助地址9", 锁附分组, "D3366", "ZZ轴锁附终点")]);
            await using var 客户端 = new 替身Modbus客户端();
            var 服务实例 = 新服务(配置, 客户端);

            var 请求 = new 地址编辑请求
            {
                标题 = "标定 · 其它点",
                默认分组 = 标定分组,
                项集合 = [编辑项("辅助地址9", 标定分组, "D3900", "标定新增项", 新增: true)],
            };

            await 服务实例.异步保存标定页(请求);

            Assert.Equal(2, 配置.辅助地址.Count);

            var 旧项 = 配置.辅助地址.Single(项 => 项.分组 == 锁附分组 && 项.键 == "辅助地址9");
            Assert.Equal("ZZ轴锁附终点", 旧项.名称);
            Assert.Equal("D3366", 旧项.地址);

            var 新项 = 配置.辅助地址.Single(项 => 项.分组 == 标定分组 && 项.键 == "辅助地址9");
            Assert.Equal("标定新增项", 新项.名称);
            Assert.Equal("D3900", 新项.地址);
        }
        finally
        {
            await 还原配方(原始);
        }
    }

    /// <summary>
    /// 两个分组各有一个同名键，改「后出现的那个分组」时不得串改前一个：
    /// 旧 <c>FirstOrDefault</c> 全局按键会先命中列表靠前的项 → 改点胶却把锁附改掉。
    /// </summary>
    [Fact]
    public async Task 两个分组同名键改其一不应串改另一个()
    {
        var 原始 = await 备份配方();

        try
        {
            // 顺序刻意：被误命中的「锁附」放前面
            var 配置 = 新配置(
            [
                辅助项("辅助地址7", 锁附分组, "D3346", "锁附原值"),
                辅助项("辅助地址7", 点胶分组, "D3500", "点胶原值"),
            ]);
            await using var 客户端 = new 替身Modbus客户端();
            var 服务实例 = 新服务(配置, 客户端);

            var 请求 = new 地址编辑请求
            {
                标题 = "点胶示教",
                默认分组 = 点胶分组,
                项集合 = [编辑项("辅助地址7", 点胶分组, "D3800", "点胶改后")],
            };

            await 服务实例.异步保存点表页(点胶分组, 请求);

            Assert.Equal(2, 配置.辅助地址.Count);
            Assert.Equal("锁附原值", 配置.辅助地址.Single(项 => 项.分组 == 锁附分组).名称);
            Assert.Equal("D3346", 配置.辅助地址.Single(项 => 项.分组 == 锁附分组).地址);

            var 点胶项 = 配置.辅助地址.Single(项 => 项.分组 == 点胶分组);
            Assert.Equal("点胶改后", 点胶项.名称);
            Assert.Equal("D3800", 点胶项.地址);
        }
        finally
        {
            await 还原配方(原始);
        }
    }

    /// <summary>动作按钮同名的分组 + 键定位（不调用历史 查找动作按钮 全局按键方法）。</summary>
    [Fact]
    public async Task 两个分组同名动作按钮改其一不应串改另一个()
    {
        var 原始 = await 备份配方();

        try
        {
            var 配置 = 新配置([],
            [
                按钮项("动作1", 锁附分组, 地址: "M490"),
                按钮项("动作1", 点胶分组, 地址: "M491"),
            ]);
            await using var 客户端 = new 替身Modbus客户端();
            var 服务实例 = 新服务(配置, 客户端);

            var 请求 = new 地址编辑请求
            {
                标题 = "点胶示教",
                默认分组 = 点胶分组,
                项集合 = [按钮编辑项("动作1", 点胶分组, "M493", "点胶按钮改后")],
            };

            await 服务实例.异步保存点表页(点胶分组, 请求);

            Assert.Equal(2, 配置.动作按钮.Count);
            Assert.Equal("M490", 配置.动作按钮.Single(项 => 项.分组 == 锁附分组).地址);

            var 点胶按钮 = 配置.动作按钮.Single(项 => 项.分组 == 点胶分组);
            Assert.Equal("M493", 点胶按钮.地址);
            Assert.Equal("点胶按钮改后", 点胶按钮.名称);
        }
        finally
        {
            await 还原配方(原始);
        }
    }

    /// <summary>
    /// 连续新增到 30 个不报重复键，第 31 个被两个保存入口的兜底校验拒绝（落盘前抛异常）。
    /// </summary>
    [Theory]
    [InlineData(false, 锁附分组)]
    [InlineData(true, 标定分组)]
    public async Task 连续新增到30个应成功且键不重复且第31个被拒(bool 标定页, string 分组)
    {
        var 原始 = await 备份配方();

        try
        {
            var 配置 = 新配置([]);
            await using var 客户端 = new 替身Modbus客户端();
            var 服务实例 = 新服务(配置, 客户端);

            var 满额请求 = new 地址编辑请求
            {
                标题 = "满额",
                默认分组 = 分组,
                项集合 = Enumerable.Range(1, 辅助地址项.每组上限)
                    .Select(序号 => 编辑项($"辅助地址{序号}", 分组, 新增: true))
                    .ToList(),
            };

            await 保存(服务实例, 标定页, 满额请求);

            Assert.Equal(辅助地址项.每组上限, 配置.辅助地址.Count);
            Assert.Equal(
                辅助地址项.每组上限,
                配置.辅助地址.Select(项 => 项.键).Distinct(StringComparer.OrdinalIgnoreCase).Count());

            // 第 31 个：新增项已被加入 _配置.辅助地址，校验必须覆盖新增后的状态
            var 超额请求 = new 地址编辑请求
            {
                标题 = "超额",
                默认分组 = 分组,
                项集合 = [编辑项($"辅助地址{辅助地址项.每组上限 + 1}", 分组, 新增: true)],
            };

            var 异常 = await Assert.ThrowsAsync<InvalidOperationException>(
                () => 保存(服务实例, 标定页, 超额请求));

            Assert.Contains($"超过 {辅助地址项.每组上限}", 异常.Message, StringComparison.Ordinal);
            Assert.Equal(辅助地址项.每组上限 + 1, 配置.辅助地址.Count); // 校验在落盘前抛，内存里已 31
        }
        finally
        {
            await 还原配方(原始);
        }
    }

    /// <summary>
    /// 跨组同名辅助地址删除：旧逻辑全局按键 <c>RemoveAll</c> 会把两组的同名项一起删掉
    /// （别的分组的项不在本页请求里，删了就真的没了，不会被重新加回来）。
    /// </summary>
    [Fact]
    public async Task 跨组同名辅助地址删除不应误删()
    {
        var 原始 = await 备份配方();

        try
        {
            var 配置 = 新配置(
            [
                辅助项("辅助地址8", 锁附分组, "D3364", "锁附ZZ起点"),
                辅助项("辅助地址8", 点胶分组, "D3500", "点胶ZZ起点"),
                辅助项("辅助地址20", 点胶分组, "D3810", "点胶保留项"),
            ]);
            await using var 客户端 = new 替身Modbus客户端();
            var 服务实例 = 新服务(配置, 客户端);

            // 点胶页编辑器只列出本组条目：删的是点胶组的 辅助地址8，
            // 锁附组的同名项根本不在这个请求里（这正是误删会丢数据的场景）。
            // 项集合里留一个本组正常项，模拟"删一行、其余行照常回写"。
            var 请求 = new 地址编辑请求
            {
                标题 = "点胶示教",
                默认分组 = 点胶分组,
                项集合 = [编辑项("辅助地址20", 点胶分组, "D3810", "点胶保留项")],
                已删除项 = [编辑项("辅助地址8", 点胶分组, "D3500", "点胶ZZ起点")],
            };

            await 服务实例.异步保存点表页(点胶分组, 请求);

            // 正确结果：锁附同名项 + 点胶保留项（旧逻辑全局 RemoveAll 会把锁附同名项也删掉 → 只剩 1 条）
            Assert.Equal(2, 配置.辅助地址.Count);
            var 锁附项 = 配置.辅助地址.Single(项 => 项.分组 == 锁附分组 && 项.键 == "辅助地址8");
            Assert.Equal("锁附ZZ起点", 锁附项.名称);
            Assert.Equal("D3364", 锁附项.地址);
            Assert.Contains(配置.辅助地址, 项 => 项.分组 == 点胶分组 && 项.键 == "辅助地址20");
            Assert.DoesNotContain(配置.辅助地址, 项 => 项.分组 == 点胶分组 && 项.键 == "辅助地址8");
        }
        finally
        {
            await 还原配方(原始);
        }
    }

    /// <summary>
    /// 跨组同名动作按钮删除 + 固定按钮保护：只删目标组的非固定项，
    /// 同组固定按钮（<c>!甲.固定</c>）与其它组同名项都要留下。
    /// </summary>
    [Fact]
    public async Task 跨组同名动作按钮删除不应误删且固定按钮受保护()
    {
        var 原始 = await 备份配方();

        try
        {
            var 配置 = 新配置([],
            [
                按钮项("动作1", 锁附分组, 地址: "M490"),
                按钮项("动作1", 点胶分组, 地址: "M491"),
                按钮项("动作2", 锁附分组, 固定: true, 地址: "M492"),
                按钮项("动作9", 点胶分组, 地址: "M494"),
            ]);
            await using var 客户端 = new 替身Modbus客户端();
            var 服务实例 = 新服务(配置, 客户端);

            // 点胶页只删本组的 动作1；锁附组的同名项与固定按钮都不在请求里。
            // 旧逻辑全局按键 RemoveAll 会把锁附的 动作1 也删掉（固定按钮靠 !固定 保护留下）。
            var 请求 = new 地址编辑请求
            {
                标题 = "点胶示教",
                默认分组 = 点胶分组,
                项集合 =
                [
                    按钮编辑项("动作9", 点胶分组, "M494", "点胶保留按钮"),
                ],
                已删除项 = [按钮编辑项("动作1", 点胶分组, "M491", "动作1")],
            };

            await 服务实例.异步保存点表页(点胶分组, 请求);

            // 锁附同名项 + 锁附固定按钮 + 点胶保留按钮 = 3（旧逻辑误删后只剩 2）
            Assert.Equal(3, 配置.动作按钮.Count);
            Assert.Contains(配置.动作按钮, 项 => 项.分组 == 锁附分组 && 项.键 == "动作1" && 项.地址 == "M490");
            Assert.Contains(配置.动作按钮, 项 => 项.键 == "动作2" && 项.固定 && 项.地址 == "M492");
            Assert.Contains(配置.动作按钮, 项 => 项.分组 == 点胶分组 && 项.键 == "动作9");
            Assert.DoesNotContain(配置.动作按钮, 项 => 项.分组 == 点胶分组 && 项.键 == "动作1");
        }
        finally
        {
            await 还原配方(原始);
        }
    }
}
