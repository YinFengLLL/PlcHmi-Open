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
/// 历史记录模块的回归测试：数值缩放（INT/DINT + 小数位）、CSV 导出、
/// 配置校验、权限边界与按天落盘。
///
/// <para>端到端采集链路（触发位上升沿 → 落一条记录）见
/// <see cref="历史记录集成测试"/> —— 那里要起完整的宿主与虚拟 PLC，
/// 与这些纯逻辑用例分开更易定位。</para>
///
/// <para>与 <c>RecipeStoreRecoveryTests</c> 同属 <c>config-files</c> 集合：
/// 用例会写 <c>config/history-record.json</c>，与其他直接读写真实配置文件的用例必须串行。</para>
/// </summary>
[Collection("config-files")]
public class 历史记录测试
{
    // ── 数值缩放与 CSV ──────────────────────────────────────────────────

    [Theory]
    [InlineData(2, 600, "6.00")]
    [InlineData(0, 600, "600")]
    [InlineData(1, 600, "60.0")]
    [InlineData(3, 600, "0.600")]
    [InlineData(2, -1, "-0.01")]
    public void 小数位应把PLC整数值缩放成工程量文本(int 小数位, double 原始值, string 期望)
    {
        var 项 = new 历史数据项 { 名称 = "实际扭力", 地址 = "D2109", 数据类型 = 标签数据类型.Int32, 小数位 = 小数位 };

        Assert.Equal(期望, 项.显示文本(原始值));
    }

    [Fact]
    public void CSV应写表头与缩放后的值()
    {
        var 配置 = new 历史记录配置
        {
            数据项集合 =
            [
                new() { 名称 = "实际扭力", 地址 = "D2109", 数据类型 = 标签数据类型.Int32, 小数位 = 2, 单元 = "N·m" },
                new() { 名称 = "结果", 地址 = "D2121", 数据类型 = 标签数据类型.Int16 },
            ],
        };

        var 条目 = new 历史记录条目
        {
            时刻 = new DateTime(2026, 9, 24, 20, 13, 46),
            数值集合 = [600, 1],
        };

        var 文本 = 历史记录CSV编解码器.编码(配置, [条目]);
        var 行 = 文本.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("时间,实际扭力(N·m),结果", 行[0]);
        Assert.Equal("2026-09-24 20:13:46,6.00,1", 行[1]);
    }

    [Fact]
    public void CSV应对含逗号的列名加引号()
    {
        var 配置 = new 历史记录配置
        {
            数据项集合 = [new() { 名称 = "扭力,上限", 地址 = "D2105", 数据类型 = 标签数据类型.Int32 }],
        };

        var 文本 = 历史记录CSV编解码器.编码(配置, []);

        Assert.Contains("\"扭力,上限\"", 文本);
    }

    // ── 配置校验 ────────────────────────────────────────────────────────

    [Fact]
    public async Task 默认配置应校验通过()
    {
        await using var 客户端 = new 替身Modbus客户端();
        var 服务实例 = 新建服务(客户端);

        var 校验项 = 服务实例.校验(服务实例.配置);

        Assert.True(校验项.是否有效, 校验项.摘要);
        Assert.NotEmpty(服务实例.配置.数据项集合);
    }

    [Fact]
    public async Task 触发位填成字元件应被拒绝()
    {
        await using var 客户端 = new 替身Modbus客户端();
        var 服务实例 = 新建服务(客户端);
        var 配置 = 服务实例.配置.克隆();
        配置.触发地址 = "D2100";

        var 校验项 = 服务实例.校验(配置);

        Assert.False(校验项.是否有效);
        Assert.Contains("M 区", 校验项.摘要);
    }

    [Fact]
    public async Task 数据项填成位地址应被拒绝()
    {
        await using var 客户端 = new 替身Modbus客户端();
        var 服务实例 = 新建服务(客户端);
        var 配置 = 服务实例.配置.克隆();
        配置.数据项集合[0].地址 = "D2100.1";

        var 校验项 = 服务实例.校验(配置);

        Assert.False(校验项.是否有效);
        Assert.Contains("位地址", 校验项.摘要);
    }

    [Fact]
    public async Task 数据项类型不是INT_DINT_REAL应被拒绝()
    {
        await using var 客户端 = new 替身Modbus客户端();
        var 服务实例 = 新建服务(客户端);
        var 配置 = 服务实例.配置.克隆();

        // 2026-10-24：REAL（32 位浮点）已成为合法类型，这里改用一个真正不支持的类型
        配置.数据项集合[0].数据类型 = 标签数据类型.布尔;

        var 校验项 = 服务实例.校验(配置);

        Assert.False(校验项.是否有效);
        Assert.Contains("只支持 INT", 校验项.摘要);
    }

    /// <summary>
    /// 2026-10-24 现场要求：数据项支持 REAL（32 位浮点，占 2 字）。
    /// 校验要放行，且按 2 字做区间重叠检查（与下一项不能咬在一起）。
    /// </summary>
    [Fact]
    public async Task 数据项支持REAL且按两字校验重叠()
    {
        await using var 客户端 = new 替身Modbus客户端();
        var 服务实例 = 新建服务(客户端);

        var 单列 = new 历史记录配置
        {
            标题 = "浮点测试",
            触发地址 = "M8",
            数据项集合 =
            [
                new() { 名称 = "胶量", 地址 = "D1420", 数据类型 = 标签数据类型.Float32, 小数位 = 3, 单元 = "mg" },
            ],
        };

        Assert.True(服务实例.校验(单列).是否有效, 服务实例.校验(单列).摘要);

        // 下一项紧挨着写在高位字上（D1421）→ 必须判为重叠
        var 冲突 = 单列.克隆();
        冲突.数据项集合.Add(new() { 名称 = "下一项", 地址 = "D1421", 数据类型 = 标签数据类型.Int16 });

        var 校验项 = 服务实例.校验(冲突);

        Assert.False(校验项.是否有效);
        Assert.Contains("重叠", 校验项.摘要);
    }

    /// <summary>
    /// DINT 占两个字，紧挨着写下一项就会与它重叠——这是现场最容易犯的地址抄写错误，
    /// 必须拦下来，否则两列的取值会互相"咬"在一起且谁也说不清哪个对。
    /// </summary>
    [Fact]
    public async Task 地址区间重叠应被拒绝()
    {
        await using var 客户端 = new 替身Modbus客户端();
        var 服务实例 = 新建服务(客户端);

        var 配置 = new 历史记录配置
        {
            标题 = "重叠测试",
            触发地址 = "M700",
            数据项集合 =
            [
                new() { 名称 = "实际扭力", 地址 = "D2109", 数据类型 = 标签数据类型.Int32, 小数位 = 2 },
                new() { 名称 = "高位字", 地址 = "D2110", 数据类型 = 标签数据类型.Int16 },
            ],
        };

        var 校验项 = 服务实例.校验(配置);

        Assert.False(校验项.是否有效);
        Assert.Contains("重叠", 校验项.摘要);
    }

    [Fact]
    public async Task 名称重复与小数位越界应被拒绝()
    {
        await using var 客户端 = new 替身Modbus客户端();
        var 服务实例 = 新建服务(客户端);

        var 配置 = new 历史记录配置
        {
            标题 = "非法配置",
            触发地址 = "M700",
            数据项集合 =
            [
                new() { 名称 = "扭力", 地址 = "D2109", 数据类型 = 标签数据类型.Int32, 小数位 = 2 },
                new() { 名称 = "扭力", 地址 = "D2111", 数据类型 = 标签数据类型.Int32, 小数位 = 9 },
            ],
        };

        var 校验项 = 服务实例.校验(配置);

        Assert.False(校验项.是否有效);
        Assert.Contains("重复", 校验项.摘要);
        Assert.Contains("小数位", 校验项.摘要);
    }

    // ── 权限边界（服务层，R5） ──────────────────────────────────────────

    [Fact]
    public async Task 操作员保存配置或清除历史应被拒绝()
    {
        await using var 客户端 = new 替身Modbus客户端();

        var 用户 = new 用户服务(NullLogger<用户服务>.Instance);
        var 服务实例 = 新建服务(客户端, new 用户权限守卫(用户, NullLogger<用户权限守卫>.Instance));

        var 配置 = 服务实例.配置.克隆();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => 服务实例.异步保存配置(配置));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => 服务实例.异步清除全部());
    }

    [Fact]
    public async Task 保存配置后应能落盘并被新实例读回()
    {
        await using var 客户端 = new 替身Modbus客户端();

        var 原文件 = File.Exists(配置路径.历史记录配置)
            ? await File.ReadAllTextAsync(配置路径.历史记录配置)
            : null;

        try
        {
            var 服务实例 = 新建服务(客户端);

            var 配置 = 服务实例.配置.克隆();
            配置.标题 = "单列数据体";
            配置.保留天数 = 30;
            配置.数据项集合 = [new() { 名称 = "实际扭力", 地址 = "D2109", 数据类型 = 标签数据类型.Int32, 小数位 = 2 }];

            await 服务实例.异步保存配置(配置);

            var 新实例 = 新建服务(客户端);
            await 新实例.异步初始化();

            Assert.Equal("单列数据体", 新实例.配置.标题);
            Assert.Equal(30, 新实例.配置.保留天数);
            Assert.Single(新实例.配置.数据项集合);
            Assert.Equal("D2109", 新实例.配置.数据项集合[0].地址);
        }
        finally
        {
            if (原文件 is not null)
            {
                await File.WriteAllTextAsync(配置路径.历史记录配置, 原文件);
            }
        }
    }

    // ── 按天落盘 ────────────────────────────────────────────────────────

    [Fact]
    public async Task 按天落盘应能扛住重启并可整体清除()
    {
        // 用远期日期，避免与真实运行数据 / 其他用例互相干扰
        var 日期值 = new DateTime(2035, 6, 1);

        try
        {
            var 存储 = new 历史记录存储(NullLogger<历史记录存储>.Instance);
            await 存储.异步初始化(日期值, 90);

            await 存储.异步追加(new 历史记录条目 { 时刻 = 日期值.AddHours(8), 数值集合 = [600, 1] });
            await 存储.异步追加(new 历史记录条目 { 时刻 = 日期值.AddHours(9), 数值集合 = [620, 1] });

            Assert.Equal(2, (await 存储.异步读取日(日期值)).Count);
            Assert.Contains(日期值, 存储.有记录的日期集合());

            // 新实例 = 模拟重启：当天已有记录必须先被装载，再追加不能覆盖它们
            var 重启后 = new 历史记录存储(NullLogger<历史记录存储>.Instance);
            await 重启后.异步初始化(日期值, 90);
            await 重启后.异步追加(new 历史记录条目 { 时刻 = 日期值.AddHours(10), 数值集合 = [640, 1] });

            var 全部 = await 重启后.异步读取日(日期值);
            Assert.Equal(3, 全部.Count);
            Assert.Equal(600, 全部[0].数值集合[0]);
            Assert.Equal(640, 全部[^1].数值集合[0]);

            重启后.清除全部();
            Assert.Empty(await 重启后.异步读取日(日期值));
            Assert.DoesNotContain(日期值, 重启后.有记录的日期集合());
        }
        finally
        {
            foreach (var 路径 in new[]
                     {
                         配置路径.历史记录文件(日期值),
                         配置路径.历史记录文件(日期值) + ".bak",
                     })
            {
                if (File.Exists(路径))
                {
                    File.Delete(路径);
                }
            }
        }
    }

    [Fact]
    public async Task 超期文件应在初始化时被清理()
    {
        var 旧日期 = new DateTime(2035, 1, 1);
        var 今天 = new DateTime(2035, 6, 1);

        try
        {
            var 存储 = new 历史记录存储(NullLogger<历史记录存储>.Instance);
            await 存储.异步初始化(旧日期, 90);
            await 存储.异步追加(new 历史记录条目 { 时刻 = 旧日期.AddHours(8), 数值集合 = [1] });

            var 清理数 = 存储.清理过期(今天, 90);

            Assert.True(清理数 >= 1);
            Assert.DoesNotContain(旧日期, 存储.有记录的日期集合());
        }
        finally
        {
            foreach (var 路径 in new[]
                     {
                         配置路径.历史记录文件(旧日期),
                         配置路径.历史记录文件(旧日期) + ".bak",
                     })
            {
                if (File.Exists(路径))
                {
                    File.Delete(路径);
                }
            }
        }
    }

    // ── 辅助 ────────────────────────────────────────────────────────────

    /// <summary>装载一份被测服务。客户端由用例持有并释放（<c>await using</c>），与服务同生命周期。</summary>
    private static 历史记录服务 新建服务(替身Modbus客户端 客户端, 权限守卫接口? 权限 = null) =>
        new(new 汇川地址解析器(PLC系列.小型PLC),
            客户端,
            权限 ?? new 允许全部权限守卫(),
            new 历史记录存储(NullLogger<历史记录存储>.Instance),
            NullLogger<历史记录服务>.Instance);
}
