using System.Text.Json;
using 上位机.业务.配置;

namespace 上位机.上位机测试;

/// <summary>
/// 配置加载的回退与修复测试。
///
/// <para><b>为什么要专门测这个：</b>2026-09-13 审查发现 <c>AtomicFile</c> 每次都写 <c>.bak</c>，
/// 但<b>没有任何代码去读它</b> —— 备份是死的。于是"断电 → 主文件损坏 → 启动以空数据运行"
/// 这条链依然成立，配方照样全丢。原子写与备份恢复是同一件事的两半。</para>
///
/// <para>测试全部在临时目录里进行，不触碰 <c>config/</c> 下的真实文件。</para>
/// </summary>
public class 配置文件加载器测试 : IDisposable
{
    private readonly string _目录;

    public 配置文件加载器测试()
    {
        _目录 = Path.Combine(Path.GetTempPath(), "plchmi-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_目录);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_目录))
            {
                Directory.Delete(_目录, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论
        }

        GC.SuppressFinalize(this);
    }

    private string 取路径(string 名称值) => Path.Combine(_目录, 名称值);

    private sealed class 测试载荷
    {
        public int 值 { get; set; }
    }

    private static 配置加载结果<测试载荷> 加载(string 文件路径) =>
        配置文件加载器.加载JSON<测试载荷>(文件路径, JSON配置.读取);

    // ── 正常与缺失 ──────────────────────────────────────────────────────

    [Fact]
    public void 主文件正常时应直接加载()
    {
        var 文件路径 = 取路径("ok.json");
        File.WriteAllText(文件路径, """{"值":7}""");

        var 结果值 = 加载(文件路径);

        Assert.Equal(加载结果分类.已加载, 结果值.加载结果);
        Assert.Equal(7, 结果值.值!.值);
        Assert.False(结果值.需要关注);
    }

    [Fact]
    public void 文件不存在应报告Missing而不是Failed()
    {
        var 结果值 = 加载(取路径("nope.json"));

        Assert.Equal(加载结果分类.缺失, 结果值.加载结果);
        Assert.Null(结果值.值);

        // 首次运行不算降级：各加载器对缺失文件有自己的默认值策略
        Assert.False(结果值.需要关注);
    }

    // ── 损坏与恢复 ──────────────────────────────────────────────────────

    [Fact]
    public void 主文件损坏但备份完好时应从备份恢复并修复主文件()
    {
        var 文件路径 = 取路径("recipes.json");
        File.WriteAllText(文件路径, """{"值":1}""" + "###被截断的垃圾###");
        File.WriteAllText(文件路径 + ".bak", """{"值":42}""");

        var 结果值 = 加载(文件路径);

        Assert.Equal(加载结果分类.已从备份恢复, 结果值.加载结果);
        Assert.Equal(42, 结果值.值!.值);
        Assert.True(结果值.需要关注, "数据曾丢失，必须让上层能够示警");

        // 关键：主文件必须被修复回可用状态。
        // 否则下一次保存会把这份坏内容轮换成 .bak，把唯一的好副本也毁掉。
        Assert.Equal("""{"值":42}""", File.ReadAllText(文件路径));
        Assert.Equal(加载结果分类.已加载, 加载(文件路径).加载结果);
    }

    [Fact]
    public void 主文件与备份都损坏应报告Failed()
    {
        var 文件路径 = 取路径("tags.json");
        File.WriteAllText(文件路径, "{ 坏");
        File.WriteAllText(文件路径 + ".bak", "也坏");

        var 结果值 = 加载(文件路径);

        Assert.Equal(加载结果分类.失败, 结果值.加载结果);
        Assert.Null(结果值.值);
        Assert.True(结果值.需要关注);
    }

    [Fact]
    public void 主文件损坏且无备份应报告Failed()
    {
        var 文件路径 = 取路径("alarms.json");
        File.WriteAllText(文件路径, "不是 JSON");

        var 结果值 = 加载(文件路径);

        Assert.Equal(加载结果分类.失败, 结果值.加载结果);
        Assert.True(结果值.需要关注);
    }

    [Fact]
    public void 零字节主文件应能从备份恢复()
    {
        // 断电最典型的残留形态：替换已完成，但数据从未落盘
        var 文件路径 = 取路径("users.json");
        File.WriteAllBytes(文件路径, []);
        File.WriteAllText(文件路径 + ".bak", """{"值":5}""");

        var 结果值 = 加载(文件路径);

        Assert.Equal(加载结果分类.已从备份恢复, 结果值.加载结果);
        Assert.Equal(5, 结果值.值!.值);
    }

    [Fact]
    public async Task 异步加载同样支持备份恢复()
    {
        var 文件路径 = 取路径("param-tables.json");
        await File.WriteAllTextAsync(文件路径, "坏内容");
        await File.WriteAllTextAsync(文件路径 + ".bak", """{"值":9}""");

        var 结果值 = await 配置文件加载器
            .异步加载JSON<测试载荷>(文件路径, JSON配置.读取);

        Assert.Equal(加载结果分类.已从备份恢复, 结果值.加载结果);
        Assert.Equal(9, 结果值.值!.值);
    }

    // ── 备份本身不得被坏数据覆盖 ────────────────────────────────────────

    [Fact]
    public async Task 主文件为空时保存不应把备份覆盖成空()
    {
        // 现场形态：recipes.json 被截断成 0 字节，而 .bak 里还留着最后一份好数据。
        // 此时若照常轮换，好副本会被这个 0 字节文件顶掉 —— 等于亲手毁掉退路。
        var 文件路径 = 取路径("recipes.json");
        await File.WriteAllTextAsync(文件路径 + ".bak", "最后一次好数据");
        File.WriteAllBytes(文件路径, []);

        await 原子文件.异步写入全部文本(文件路径, "新数据");

        Assert.Equal("新数据", await File.ReadAllTextAsync(文件路径));
        Assert.Equal("最后一次好数据", await File.ReadAllTextAsync(文件路径 + ".bak"));
    }

    [Fact]
    public void 恢复备份失败时不应抛异常()
    {
        Assert.False(原子文件.尝试恢复备份(取路径("根本不存在.json")));
    }

    // ── 健康登记 ────────────────────────────────────────────────────────

    [Fact]
    public void 加载失败应登记到配置健康且Loaded不登记()
    {
        var 健康 = new 配置健康服务();
        var 文件路径 = 取路径("alarms.json");

        File.WriteAllText(文件路径, """{"值":1}""");
        健康.上报("报警配置", 加载(文件路径));
        Assert.False(健康.是否已降级);

        File.WriteAllText(文件路径, "坏");
        健康.上报("报警配置", 加载(文件路径));

        Assert.True(健康.是否已降级);
        Assert.Single(健康.问题集合);
        Assert.True(健康.问题集合[0].是否阻塞);
        Assert.Contains("报警配置", 健康.问题集合[0].摘要, StringComparison.Ordinal);
    }

    [Fact]
    public void 同一配置重复登记应覆盖而不是堆积()
    {
        var 健康 = new 配置健康服务();

        健康.上报(new 配置问题("报警配置", 加载结果分类.失败, "第一次"));
        健康.上报(new 配置问题("报警配置", 加载结果分类.失败, "第二次"));

        Assert.Single(健康.问题集合);
        Assert.Equal("第二次", 健康.问题集合[0].详情);
    }

    [Fact]
    public void 健康状态变化应触发事件()
    {
        var 健康 = new 配置健康服务();
        var 已触发 = 0;

        健康.已变更 += (_, _) => 已触发++;
        健康.上报(new 配置问题("点位表", 加载结果分类.失败, "损坏"));

        Assert.Equal(1, 已触发);
    }

    [Fact]
    public void 从备份恢复应标记为需要示警但不阻断()
    {
        var 问题项 = new 配置问题("机种数据", 加载结果分类.已从备份恢复, "已恢复");

        Assert.False(问题项.是否阻塞);
        Assert.Contains("已从备份恢复", 问题项.摘要, StringComparison.Ordinal);
    }
}
