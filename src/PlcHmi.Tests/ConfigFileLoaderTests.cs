using System.Text.Json;
using PlcHmi.Business.Configuration;

namespace PlcHmi.Tests;

/// <summary>
/// 配置加载的回退与修复测试。
///
/// <para><b>为什么要专门测这个：</b>2026-09-13 审查发现 <c>AtomicFile</c> 每次都写 <c>.bak</c>，
/// 但<b>没有任何代码去读它</b> —— 备份是死的。于是"断电 → 主文件损坏 → 启动以空数据运行"
/// 这条链依然成立，配方照样全丢。原子写与备份恢复是同一件事的两半。</para>
///
/// <para>测试全部在临时目录里进行，不触碰 <c>config/</c> 下的真实文件。</para>
/// </summary>
public class ConfigFileLoaderTest : IDisposable
{
    private readonly string _dir;

    public ConfigFileLoaderTest()
    {
        _dir = Path.Combine(Path.GetTempPath(), "plchmi-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论
        }

        GC.SuppressFinalize(this);
    }

    private string GetPath(string nameValue) => Path.Combine(_dir, nameValue);

    private sealed class TestPayload
    {
        public int Value { get; set; }
    }

    private static ConfigLoadResult<TestPayload> Load(string filePath) =>
        ConfigFileLoader.LoadJson<TestPayload>(filePath, JsonConfig.Read);

    // ── 正常与缺失 ──────────────────────────────────────────────────────

    [Fact]
    public void MainFileNormalWhenShouldDirectConnectLoad()
    {
        var filePath = GetPath("ok.json");
        File.WriteAllText(filePath, """{"value":7}""");

        var resultValue = Load(filePath);

        Assert.Equal(LoadResultSplitCategory.Loaded, resultValue.loadResult);
        Assert.Equal(7, resultValue.Value!.Value);
        Assert.False(resultValue.NeedCloseNote);
    }

    [Fact]
    public void FileMissingShouldReportMissingInsteadOfFailed()
    {
        var resultValue = Load(GetPath("nope.json"));

        Assert.Equal(LoadResultSplitCategory.Missing, resultValue.loadResult);
        Assert.Null(resultValue.Value);

        // 首次运行不算降级：各加载器对缺失文件有自己的默认值策略
        Assert.False(resultValue.NeedCloseNote);
    }

    // ── 损坏与恢复 ──────────────────────────────────────────────────────

    [Fact]
    public void MainFileCorruptButBackupIntactWhenShouldFromBackupRestoreAndFixMainFile()
    {
        var filePath = GetPath("recipes.json");
        File.WriteAllText(filePath, """{"value":1}""" + "###被截断的垃圾###");
        File.WriteAllText(filePath + ".bak", """{"value":42}""");

        var resultValue = Load(filePath);

        Assert.Equal(LoadResultSplitCategory.RestoredFromBackup, resultValue.loadResult);
        Assert.Equal(42, resultValue.Value!.Value);
        Assert.True(resultValue.NeedCloseNote, "数据曾丢失，必须让上层能够示警");

        // 关键：主文件必须被修复回可用状态。
        // 否则下一次保存会把这份坏内容轮换成 .bak，把唯一的好副本也毁掉。
        Assert.Equal("""{"value":42}""", File.ReadAllText(filePath));
        Assert.Equal(LoadResultSplitCategory.Loaded, Load(filePath).loadResult);
    }

    [Fact]
    public void MainFileAndBackupBothCorruptShouldReportFailed()
    {
        var filePath = GetPath("tags.json");
        File.WriteAllText(filePath, "{ 坏");
        File.WriteAllText(filePath + ".bak", "也坏");

        var resultValue = Load(filePath);

        Assert.Equal(LoadResultSplitCategory.Failed, resultValue.loadResult);
        Assert.Null(resultValue.Value);
        Assert.True(resultValue.NeedCloseNote);
    }

    [Fact]
    public void MainFileCorruptAndNoneBackupShouldReportFailed()
    {
        var filePath = GetPath("alarms.json");
        File.WriteAllText(filePath, "不是 JSON");

        var resultValue = Load(filePath);

        Assert.Equal(LoadResultSplitCategory.Failed, resultValue.loadResult);
        Assert.True(resultValue.NeedCloseNote);
    }

    [Fact]
    public void ZeroByteMainFileShouldBeAbleToFromBackupRestore()
    {
        // 断电最典型的残留形态：替换已完成，但数据从未落盘
        var filePath = GetPath("users.json");
        File.WriteAllBytes(filePath, []);
        File.WriteAllText(filePath + ".bak", """{"value":5}""");

        var resultValue = Load(filePath);

        Assert.Equal(LoadResultSplitCategory.RestoredFromBackup, resultValue.loadResult);
        Assert.Equal(5, resultValue.Value!.Value);
    }

    [Fact]
    public async Task LoadSameSampleSupportBackupRestoreAsync()
    {
        var filePath = GetPath("param-tables.json");
        await File.WriteAllTextAsync(filePath, "坏内容");
        await File.WriteAllTextAsync(filePath + ".bak", """{"value":9}""");

        var resultValue = await ConfigFileLoader
            .LoadJsonAsync<TestPayload>(filePath, JsonConfig.Read);

        Assert.Equal(LoadResultSplitCategory.RestoredFromBackup, resultValue.loadResult);
        Assert.Equal(9, resultValue.Value!.Value);
    }

    // ── 备份本身不得被坏数据覆盖 ────────────────────────────────────────

    [Fact]
    public async Task MainFileIsEmptyWhenSaveShouldNotBackupOverwriteAsEmpty()
    {
        // 现场形态：recipes.json 被截断成 0 字节，而 .bak 里还留着最后一份好数据。
        // 此时若照常轮换，好副本会被这个 0 字节文件顶掉 —— 等于亲手毁掉退路。
        var filePath = GetPath("recipes.json");
        await File.WriteAllTextAsync(filePath + ".bak", "最后一次好数据");
        File.WriteAllBytes(filePath, []);

        await AtomicFile.WriteAllTextAsync(filePath, "新数据");

        Assert.Equal("新数据", await File.ReadAllTextAsync(filePath));
        Assert.Equal("最后一次好数据", await File.ReadAllTextAsync(filePath + ".bak"));
    }

    [Fact]
    public void RestoreBackupFailedWhenShouldNotThrowException()
    {
        Assert.False(AtomicFile.TryRestoreBackup(GetPath("根本不存在.json")));
    }

    // ── 健康登记 ────────────────────────────────────────────────────────

    [Fact]
    public void LoadFailedShouldRegisterToConfigHealthAndLoadedNotRegister()
    {
        var Health = new ConfigHealthService();
        var filePath = GetPath("alarms.json");

        File.WriteAllText(filePath, """{"value":1}""");
        Health.Report("报警配置", Load(filePath));
        Assert.False(Health.IsDegraded);

        File.WriteAllText(filePath, "坏");
        Health.Report("报警配置", Load(filePath));

        Assert.True(Health.IsDegraded);
        Assert.Single(Health.IssueCollection);
        Assert.True(Health.IssueCollection[0].IsBlocked);
        Assert.Contains("报警配置", Health.IssueCollection[0].Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void SameOneConfigDuplicateRegisterShouldOverwriteInsteadOfHeapAccumulate()
    {
        var Health = new ConfigHealthService();

        Health.Report(new ConfigIssue("报警配置", LoadResultSplitCategory.Failed, "第一次"));
        Health.Report(new ConfigIssue("报警配置", LoadResultSplitCategory.Failed, "第二次"));

        Assert.Single(Health.IssueCollection);
        Assert.Equal("第二次", Health.IssueCollection[0].detail);
    }

    [Fact]
    public void HealthStateChangeShouldTriggerEvent()
    {
        var Health = new ConfigHealthService();
        var Triggered = 0;

        Health.changed += (_, _) => Triggered++;
        Health.Report(new ConfigIssue("点位表", LoadResultSplitCategory.Failed, "损坏"));

        Assert.Equal(1, Triggered);
    }

    [Fact]
    public void FromBackupRestoreShouldMarkAsNeedWarnButNotBlock()
    {
        var issueItem = new ConfigIssue("机种数据", LoadResultSplitCategory.RestoredFromBackup, "已恢复");

        Assert.False(issueItem.IsBlocked);
        Assert.Contains("已从备份恢复", issueItem.Summary, StringComparison.Ordinal);
    }
}
