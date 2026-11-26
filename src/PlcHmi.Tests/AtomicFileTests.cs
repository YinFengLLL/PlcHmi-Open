using PlcHmi.Business.Configuration;

namespace PlcHmi.Tests;

/// <summary>
/// 配置文件原子落盘的测试。
///
/// <para><b>为什么要专门测这个：</b>原子写是为了防"写一半断电把配置文件写坏"。
/// 这类缺陷在开发机上永远复现不出来 —— 只有在现场停电那一刻才暴露，
/// 而代价是全部机种配方丢失。所以必须用测试把行为钉死。</para>
///
/// <para>测试全部在临时目录里进行，不触碰 <c>config/</c> 下的真实文件。</para>
/// </summary>
public class AtomicFileTest : IDisposable
{
    private readonly string _dir;

    public AtomicFileTest()
    {
        _dir = Path.Combine(Path.GetTempPath(), "plchmi-atomic-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public async Task FirstWriteShouldCreateFileAndWriteCompleteWholeContent()
    {
        var filePath = Path.Combine(_dir, "recipes.json");

        await AtomicFile.WriteAllTextAsync(filePath, """{"a":1}""");

        Assert.True(File.Exists(filePath));
        Assert.Equal("""{"a":1}""", await File.ReadAllTextAsync(filePath));
    }

    [Fact]
    public async Task OverwriteWriteAfterContentShouldAsNewValueAndOldValueAlreadyBackup()
    {
        var filePath = Path.Combine(_dir, "recipes.json");
        await AtomicFile.WriteAllTextAsync(filePath, "旧内容");

        await AtomicFile.WriteAllTextAsync(filePath, "新内容");

        Assert.Equal("新内容", await File.ReadAllTextAsync(filePath));
        Assert.True(File.Exists(filePath + ".bak"), "应留下 .bak 回滚副本");
        Assert.Equal("旧内容", await File.ReadAllTextAsync(filePath + ".bak"));
    }

    [Fact]
    public async Task WriteSuccessAfterShouldNotResidualKeepTemporaryFile()
    {
        var filePath = Path.Combine(_dir, "users.json");

        await AtomicFile.WriteAllTextAsync(filePath, "内容一");
        await AtomicFile.WriteAllTextAsync(filePath, "内容二");

        Assert.False(File.Exists(filePath + ".tmp"), "成功后 .tmp 应已被消费掉，不应残留");
    }

    [Fact]
    public async Task LargeContentShouldCompleteWholeWriteNotTruncateDisconnect()
    {
        var filePath = Path.Combine(_dir, "big.json");
        // 约 400 KB，用于验证写入路径不会截断
        var Content = new string('A', 400_000);

        await AtomicFile.WriteAllTextAsync(filePath, Content);

        var Read = await File.ReadAllTextAsync(filePath);
        Assert.Equal(Content.Length, Read.Length);
    }

    [Fact]
    public async Task TargetDirectoryWhenMissingShouldAutoCreate()
    {
        var filePath = Path.Combine(_dir, "nested", "deeper", "recipes.json");

        await AtomicFile.WriteAllTextAsync(filePath, "内容");

        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public async Task EmptyPathShouldThrowParameterException()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => AtomicFile.WriteAllTextAsync(string.Empty, "内容"));
    }

    [Theory]
    [InlineData("recipes.json.tmp", true)]
    [InlineData("recipes.json.bak", true)]
    [InlineData("recipes.json", false)]
    [InlineData("config/tags.json", false)]
    public void IntermediateOutputObjectDetectShouldOnlyMatchtmpAndbak(string filePath, bool expected)
    {
        Assert.Equal(expected, AtomicFile.IsIntermediateFile(filePath));
    }
}
