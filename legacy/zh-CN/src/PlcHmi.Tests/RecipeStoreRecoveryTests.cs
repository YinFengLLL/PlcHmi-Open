using Microsoft.Extensions.Logging.Abstractions;
using 上位机.业务.配置;
using 上位机.业务.服务;

namespace 上位机.上位机测试;

/// <summary>
/// 机种数据（recipes.json）损坏恢复的端到端测试。
///
/// <para><b>复现的现场链路：</b>断电 → <c>recipes.json</c> 损坏 → 启动解析失败 →
/// <b>以空配方运行</b> → 操作员不知情 → 下一次保存把坏内容轮换成 <c>.bak</c>，
/// 唯一的好副本也没了 → 全部机种参数与点表不可恢复。</para>
///
/// <para>本组测试钉死前两步：加载必须真的回退备份，且必须登记为需要示警。</para>
/// </summary>
// 直接读写真实的 config/recipes.json，与其它碰配置文件的测试互斥，禁止并行
[Collection("config-files")]
public class 配方存储恢复测试
{
    [Fact]
    public async Task 主文件损坏时应从备份恢复机种数据()
    {
        var 主 = 配置路径.配方集合;
        var 备份 = 主 + ".bak";

        var 原始主 = File.Exists(主) ? await File.ReadAllTextAsync(主) : null;
        var 原始备份 = File.Exists(备份) ? await File.ReadAllTextAsync(备份) : null;

        try
        {
            // 1. 造一份"最后一次已知良好"的数据（机种 7，便于识别）
            using (var 写入器 = new 配方存储(NullLogger<配方存储>.Instance))
            {
                写入器.获取或创建(7);
                await 写入器.异步保存();
            }

            // 2. 把它固定成备份，再让主文件损坏 —— 模拟"写完备份后断电"
            File.Copy(主, 备份, overwrite: true);
            await File.WriteAllTextAsync(主, """{"当前配方号":1,"配方集合":[{"号":7""" + "###截断");

            // 3. 重新构造：这一步在现场就是"重新开机"
            var 健康 = new 配置健康服务();
            using var 存储 = new 配方存储(NullLogger<配方存储>.Instance, 健康);

            // 备份里的机种数据必须真的回来了
            Assert.NotNull(存储.查找(7));

            // 且必须被登记为降级 —— 数据虽救回，最近一次改动可能已丢失，操作员要知道
            Assert.True(健康.是否已降级, "从备份恢复必须登记，不能静默继续");
            Assert.Equal(加载结果分类.已从备份恢复, 健康.问题集合[0].加载结果);

            // 主文件已修复：内容与备份一致，下一次保存不会再拿坏内容去覆盖备份
            Assert.Equal(await File.ReadAllTextAsync(备份), await File.ReadAllTextAsync(主));
        }
        finally
        {
            恢复(主, 原始主);
            恢复(备份, 原始备份);
        }
    }

    [Fact]
    public void 主文件与备份都不可用时应为空配方且登记降级()
    {
        var 主 = 配置路径.配方集合;
        var 备份 = 主 + ".bak";

        var 原始主 = File.Exists(主) ? File.ReadAllText(主) : null;
        var 原始备份 = File.Exists(备份) ? File.ReadAllText(备份) : null;

        try
        {
            File.WriteAllText(主, "彻底坏了");
            File.WriteAllText(备份, "备份也坏了");

            var 健康 = new 配置健康服务();
            using var 存储 = new 配方存储(NullLogger<配方存储>.Instance, 健康);

            Assert.Null(存储.查找(7));
            Assert.True(健康.是否已降级);
            Assert.True(健康.问题集合[0].是否阻塞, "配方全丢属于阻断级，必须区别于'已恢复'");
        }
        finally
        {
            恢复(主, 原始主);
            恢复(备份, 原始备份);
        }
    }

    private static void 恢复(string 文件路径, string? 内容)
    {
        if (内容 is null)
        {
            if (File.Exists(文件路径))
            {
                File.Delete(文件路径);
            }

            return;
        }

        File.WriteAllText(文件路径, 内容);
    }
}
