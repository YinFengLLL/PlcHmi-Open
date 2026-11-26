using Microsoft.Extensions.Logging.Abstractions;
using PlcHmi.Business.Configuration;
using PlcHmi.Business.Services;

namespace PlcHmi.Tests;

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
public class RecipeStoreRestoreTest
{
    [Fact]
    public async Task MainFileCorruptWhenShouldFromBackupRestoreMachineModelData()
    {
        var master = ConfigPath.RecipeCollection;
        var backup = master + ".bak";

        var originalMaster = File.Exists(master) ? await File.ReadAllTextAsync(master) : null;
        var originalBackup = File.Exists(backup) ? await File.ReadAllTextAsync(backup) : null;

        try
        {
            // 1. 造一份"最后一次已知良好"的数据（机种 7，便于识别）
            using (var Writer = new RecipeStore(NullLogger<RecipeStore>.Instance))
            {
                Writer.GetOrCreate(7);
                await Writer.SaveAsync();
            }

            // 2. 把它固定成备份，再让主文件损坏 —— 模拟"写完备份后断电"
            File.Copy(master, backup, overwrite: true);
            await File.WriteAllTextAsync(master, """{"currentRecipeNumber":1,"recipeCollection":[{"number":7""" + "###截断");

            // 3. 重新构造：这一步在现场就是"重新开机"
            var Health = new ConfigHealthService();
            using var Store = new RecipeStore(NullLogger<RecipeStore>.Instance, Health);

            // 备份里的机种数据必须真的回来了
            Assert.NotNull(Store.Find(7));

            // 且必须被登记为降级 —— 数据虽救回，最近一次改动可能已丢失，操作员要知道
            Assert.True(Health.IsDegraded, "从备份恢复必须登记，不能静默继续");
            Assert.Equal(LoadResultSplitCategory.RestoredFromBackup, Health.IssueCollection[0].loadResult);

            // 主文件已修复：内容与备份一致，下一次保存不会再拿坏内容去覆盖备份
            Assert.Equal(await File.ReadAllTextAsync(backup), await File.ReadAllTextAsync(master));
        }
        finally
        {
            Recover(master, originalMaster);
            Recover(backup, originalBackup);
        }
    }

    [Fact]
    public void MainFileAndBackupBothUnavailableWhenShouldIsEmptyRecipeAndRegisterDegradeLevel()
    {
        var master = ConfigPath.RecipeCollection;
        var backup = master + ".bak";

        var originalMaster = File.Exists(master) ? File.ReadAllText(master) : null;
        var originalBackup = File.Exists(backup) ? File.ReadAllText(backup) : null;

        try
        {
            File.WriteAllText(master, "彻底坏了");
            File.WriteAllText(backup, "备份也坏了");

            var Health = new ConfigHealthService();
            using var Store = new RecipeStore(NullLogger<RecipeStore>.Instance, Health);

            Assert.Null(Store.Find(7));
            Assert.True(Health.IsDegraded);
            Assert.True(Health.IssueCollection[0].IsBlocked, "配方全丢属于阻断级，必须区别于'已恢复'");
        }
        finally
        {
            Recover(master, originalMaster);
            Recover(backup, originalBackup);
        }
    }

    private static void Recover(string filePath, string? Content)
    {
        if (Content is null)
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            return;
        }

        File.WriteAllText(filePath, Content);
    }
}
