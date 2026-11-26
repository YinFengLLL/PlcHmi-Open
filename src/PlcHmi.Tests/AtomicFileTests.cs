using 上位机.业务.配置;

namespace 上位机.上位机测试;

/// <summary>
/// 配置文件原子落盘的测试。
///
/// <para><b>为什么要专门测这个：</b>原子写是为了防"写一半断电把配置文件写坏"。
/// 这类缺陷在开发机上永远复现不出来 —— 只有在现场停电那一刻才暴露，
/// 而代价是全部机种配方丢失。所以必须用测试把行为钉死。</para>
///
/// <para>测试全部在临时目录里进行，不触碰 <c>config/</c> 下的真实文件。</para>
/// </summary>
public class 原子文件测试 : IDisposable
{
    private readonly string _目录;

    public 原子文件测试()
    {
        _目录 = Path.Combine(Path.GetTempPath(), "plchmi-atomic-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public async Task 首次写入应创建文件并写入完整内容()
    {
        var 文件路径 = Path.Combine(_目录, "recipes.json");

        await 原子文件.异步写入全部文本(文件路径, """{"a":1}""");

        Assert.True(File.Exists(文件路径));
        Assert.Equal("""{"a":1}""", await File.ReadAllTextAsync(文件路径));
    }

    [Fact]
    public async Task 覆盖写入后内容应为新值且旧值已备份()
    {
        var 文件路径 = Path.Combine(_目录, "recipes.json");
        await 原子文件.异步写入全部文本(文件路径, "旧内容");

        await 原子文件.异步写入全部文本(文件路径, "新内容");

        Assert.Equal("新内容", await File.ReadAllTextAsync(文件路径));
        Assert.True(File.Exists(文件路径 + ".bak"), "应留下 .bak 回滚副本");
        Assert.Equal("旧内容", await File.ReadAllTextAsync(文件路径 + ".bak"));
    }

    [Fact]
    public async Task 写入成功后不应残留临时文件()
    {
        var 文件路径 = Path.Combine(_目录, "users.json");

        await 原子文件.异步写入全部文本(文件路径, "内容一");
        await 原子文件.异步写入全部文本(文件路径, "内容二");

        Assert.False(File.Exists(文件路径 + ".tmp"), "成功后 .tmp 应已被消费掉，不应残留");
    }

    [Fact]
    public async Task 大内容应完整写入不截断()
    {
        var 文件路径 = Path.Combine(_目录, "big.json");
        // 约 400 KB，用于验证写入路径不会截断
        var 内容 = new string('A', 400_000);

        await 原子文件.异步写入全部文本(文件路径, 内容);

        var 读取 = await File.ReadAllTextAsync(文件路径);
        Assert.Equal(内容.Length, 读取.Length);
    }

    [Fact]
    public async Task 目标目录不存在时应自动创建()
    {
        var 文件路径 = Path.Combine(_目录, "nested", "deeper", "recipes.json");

        await 原子文件.异步写入全部文本(文件路径, "内容");

        Assert.True(File.Exists(文件路径));
    }

    [Fact]
    public async Task 空路径应抛出参数异常()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => 原子文件.异步写入全部文本(string.Empty, "内容"));
    }

    [Theory]
    [InlineData("recipes.json.tmp", true)]
    [InlineData("recipes.json.bak", true)]
    [InlineData("recipes.json", false)]
    [InlineData("config/tags.json", false)]
    public void 中间产物识别应只匹配tmp与bak(string 文件路径, bool 期望)
    {
        Assert.Equal(期望, 原子文件.是否中间文件(文件路径));
    }
}
