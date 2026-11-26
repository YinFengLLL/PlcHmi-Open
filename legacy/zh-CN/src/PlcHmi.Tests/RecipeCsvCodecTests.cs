using 上位机.核心.模型;

namespace 上位机.上位机测试;

/// <summary>
/// 机种数据 CSV 编解码测试。
///
/// <para><b>格式契约：</b>一个机种 = 一个 CSV 文件，两段——
/// <c>[机种]</c>（号 / 名称）与 <c>[寄存器]</c>（D2500–D3999 的稀疏 地址,值 对）。
/// 机种的定义就是这段寄存器，所以 CSV 与「机种 = D2500–D3999 快照」严格一一对应；
/// 以后新增元件（还是落在这个区间）不会有任何"老 CSV 对不上号"的问题。</para>
///
/// <para>导出给 Excel 编辑、机种之间拷贝用；<b>主存始终是 recipes.json</b>，
/// CSV 必须经「导入」才生效。</para>
/// </summary>
public class 配方CSV编解码器测试
{
    private static 配方数据 示例机种()
    {
        var 配方项 = new 配方数据 { 号 = 3, 名称 = "锁附 A" };

        // 锁附示教第 1/2 点 X（Float32，低字在前）300.0f = 0x4396_0000
        配方项.置(2500, 0x0000);
        配方项.置(2501, 0x4396);
        配方项.置(2502, 0x0000);
        配方项.置(2503, 0x4396);

        // 点胶指令码 D3400 = 5（UInt16 单字）
        配方项.置(3400, 5);

        // 锁附辅助地址（同属机种区段）
        配方项.置(2680, 0x0000);
        配方项.置(2681, 0x42C8);

        return 配方项;
    }

    [Fact]
    public void 编码应含机种信息段与寄存器段()
    {
        var 文本 = 配方CSV编解码器.编码(示例机种());

        Assert.Contains("[机种]", 文本, StringComparison.Ordinal);
        Assert.Contains("[寄存器]", 文本, StringComparison.Ordinal);
        Assert.Contains("号,名称", 文本, StringComparison.Ordinal);
        Assert.Contains("地址,值", 文本, StringComparison.Ordinal);
        Assert.Contains("3,锁附 A", 文本, StringComparison.Ordinal);
        Assert.Contains("D2501,17302", 文本, StringComparison.Ordinal);   // 0x4396
        Assert.Contains("D3400,5", 文本, StringComparison.Ordinal);
        Assert.Contains($"D{机种区段.起始} – D{机种区段.结束}", 文本, StringComparison.Ordinal);
    }

    [Fact]
    public void 编码应只写非零寄存器()
    {
        var 文本 = 配方CSV编解码器.编码(示例机种());

        // 稀疏：值为 0 的字（D2500/D2502）整行都不写
        Assert.DoesNotContain("D2500,", 文本, StringComparison.Ordinal);
        Assert.DoesNotContain("D2502,", 文本, StringComparison.Ordinal);

        var 数据行 = 文本
            .Split('\n')
            .Select(行 => 行.Trim())
            .Where(行 => 行.StartsWith("D", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(4, 数据行.Count);   // 2501 / 2503 / 2681 / 3400
    }

    [Fact]
    public void 编码解码往返应保持一致()
    {
        var 原项 = 示例机种();
        var 文本 = 配方CSV编解码器.编码(原项);
        var 结果值 = 配方CSV编解码器.解码(文本, 1);

        Assert.True(结果值.成功, 结果值.摘要);
        Assert.NotNull(结果值.配方);
        Assert.Equal(3, 结果值.配方!.号);
        Assert.Equal("锁附 A", 结果值.配方.名称);
        Assert.Equal(4, 结果值.配方.区段快照.Count);

        foreach (var (偏移, 值) in 原项.区段快照)
        {
            Assert.Equal(值, 结果值.配方.区段快照[偏移]);
        }
    }

    [Fact]
    public void 文件未写机种号时应使用回退号()
    {
        const string 文本 = """
            # 只有数据，没有机种段
            [寄存器]
            地址,值
            D2501,17302
            """;

        var 结果值 = 配方CSV编解码器.解码(文本, 7);

        Assert.True(结果值.成功, 结果值.摘要);
        Assert.Equal(7, 结果值.配方!.号);
        Assert.Equal(0x4396, 结果值.配方.取(2501));
    }

    [Fact]
    public void 区段外地址应被拒绝()
    {
        const string 文本 = """
            [寄存器]
            地址,值
            D1202,300
            """;

        var 结果值 = 配方CSV编解码器.解码(文本, 1);

        Assert.False(结果值.成功);
        Assert.Contains(结果值.错误集合, 事件参数 => 事件参数.Contains("不在机种区段", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("D2500,abc")]      // 值不是整数
    [InlineData("D2500,70000")]    // 超出 16 位
    [InlineData("X2500,1")]        // 不是 D 区
    public void 非法数据行应被拒绝并给出明确提示(string 行)
    {
        var 文本 = $"[寄存器]\n地址,值\n{行}\n";

        var 结果值 = 配方CSV编解码器.解码(文本, 1);

        Assert.False(结果值.成功);
        Assert.NotEmpty(结果值.错误集合);
        Assert.Contains("第 3 行", 结果值.摘要 + string.Join("|", 结果值.错误集合), StringComparison.Ordinal);
    }

    [Fact]
    public void 空文件应报错而不是静默成功()
    {
        var 结果值 = 配方CSV编解码器.解码("   ", 1);

        Assert.False(结果值.成功);
        Assert.Contains(结果值.错误集合, 事件参数 => 事件参数.Contains("文件为空", StringComparison.Ordinal));
    }

    [Fact]
    public void 段外散行只提示不应失败()
    {
        const string 文本 = """
            随手写的一行
            [寄存器]
            D2501,17302
            """;

        var 结果值 = 配方CSV编解码器.解码(文本, 1);

        Assert.True(结果值.成功, 结果值.摘要);
        Assert.Single(结果值.警告);
    }

    [Fact]
    public void 机种名里的逗号应被替换以免破坏列结构()
    {
        var 配方项 = new 配方数据 { 号 = 1, 名称 = "锁附,A" };
        配方项.置(2501, 1);

        var 文本 = 配方CSV编解码器.编码(配方项);
        var 结果值 = 配方CSV编解码器.解码(文本, 1);

        Assert.True(结果值.成功, 结果值.摘要);
        Assert.Equal("锁附，A", 结果值.配方!.名称);
    }
}
