using PlcHmi.Core.Models;

namespace PlcHmi.Tests;

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
public class RecipeCsvCodecTest
{
    private static RecipeData IndicateCaseMachineModel()
    {
        var recipeItem = new RecipeData { Number = 3, Name = "锁附 A" };

        // 锁附示教第 1/2 点 X（Float32，低字在前）300.0f = 0x4396_0000
        recipeItem.put(2500, 0x0000);
        recipeItem.put(2501, 0x4396);
        recipeItem.put(2502, 0x0000);
        recipeItem.put(2503, 0x4396);

        // 点胶指令码 D3400 = 5（UInt16 单字）
        recipeItem.put(3400, 5);

        // 锁附辅助地址（同属机种区段）
        recipeItem.put(2680, 0x0000);
        recipeItem.put(2681, 0x42C8);

        return recipeItem;
    }

    [Fact]
    public void EncodeShouldContainMachineModelInfoSectionAndRegisterSection()
    {
        var Text = RecipeCsvCodec.Encode(IndicateCaseMachineModel());

        Assert.Contains("[机种]", Text, StringComparison.Ordinal);
        Assert.Contains("[寄存器]", Text, StringComparison.Ordinal);
        Assert.Contains("号,名称", Text, StringComparison.Ordinal);
        Assert.Contains("地址,值", Text, StringComparison.Ordinal);
        Assert.Contains("3,锁附 A", Text, StringComparison.Ordinal);
        Assert.Contains("D2501,17302", Text, StringComparison.Ordinal);   // 0x4396
        Assert.Contains("D3400,5", Text, StringComparison.Ordinal);
        Assert.Contains($"D{MachineModelSection.StartAt} – D{MachineModelSection.end}", Text, StringComparison.Ordinal);
    }

    [Fact]
    public void EncodeShouldOnlyWriteNonZeroRegister()
    {
        var Text = RecipeCsvCodec.Encode(IndicateCaseMachineModel());

        // 稀疏：值为 0 的字（D2500/D2502）整行都不写
        Assert.DoesNotContain("D2500,", Text, StringComparison.Ordinal);
        Assert.DoesNotContain("D2502,", Text, StringComparison.Ordinal);

        var dataRow = Text
            .Split('\n')
            .Select(Row => Row.Trim())
            .Where(Row => Row.StartsWith("D", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(4, dataRow.Count);   // 2501 / 2503 / 2681 / 3400
    }

    [Fact]
    public void EncodeDecodeRoundTripShouldKeepConsistent()
    {
        var originalItem = IndicateCaseMachineModel();
        var Text = RecipeCsvCodec.Encode(originalItem);
        var resultValue = RecipeCsvCodec.Decode(Text, 1);

        Assert.True(resultValue.Success, resultValue.Summary);
        Assert.NotNull(resultValue.Recipe);
        Assert.Equal(3, resultValue.Recipe!.Number);
        Assert.Equal("锁附 A", resultValue.Recipe.Name);
        Assert.Equal(4, resultValue.Recipe.SectionSnapshot.Count);

        foreach (var (Offset, Value) in originalItem.SectionSnapshot)
        {
            Assert.Equal(Value, resultValue.Recipe.SectionSnapshot[Offset]);
        }
    }

    [Fact]
    public void FileNotWriteMachineModelNumberWhenShouldUseFallbackNumber()
    {
        const string Text = """
            # 只有数据，没有机种段
            [寄存器]
            地址,值
            D2501,17302
            """;

        var resultValue = RecipeCsvCodec.Decode(Text, 7);

        Assert.True(resultValue.Success, resultValue.Summary);
        Assert.Equal(7, resultValue.Recipe!.Number);
        Assert.Equal(0x4396, resultValue.Recipe.Take(2501));
    }

    [Fact]
    public void SectionOuterAddressShouldBeRejected()
    {
        const string Text = """
            [寄存器]
            地址,值
            D1202,300
            """;

        var resultValue = RecipeCsvCodec.Decode(Text, 1);

        Assert.False(resultValue.Success);
        Assert.Contains(resultValue.ErrorCollection, eventArgs => eventArgs.Contains("不在机种区段", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("D2500,abc")]      // 值不是整数
    [InlineData("D2500,70000")]    // 超出 16 位
    [InlineData("X2500,1")]        // 不是 D 区
    public void IllegalDataRowShouldBeRejectedAndGiveExplicitConfirmHint(string Row)
    {
        var Text = $"[寄存器]\n地址,值\n{Row}\n";

        var resultValue = RecipeCsvCodec.Decode(Text, 1);

        Assert.False(resultValue.Success);
        Assert.NotEmpty(resultValue.ErrorCollection);
        Assert.Contains("第 3 行", resultValue.Summary + string.Join("|", resultValue.ErrorCollection), StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyFileShouldReportWrongInsteadOfStaticSilentSuccess()
    {
        var resultValue = RecipeCsvCodec.Decode("   ", 1);

        Assert.False(resultValue.Success);
        Assert.Contains(resultValue.ErrorCollection, eventArgs => eventArgs.Contains("文件为空", StringComparison.Ordinal));
    }

    [Fact]
    public void SectionOuterDiscreteRowOnlyHintShouldNotFailed()
    {
        const string Text = """
            随手写的一行
            [寄存器]
            D2501,17302
            """;

        var resultValue = RecipeCsvCodec.Decode(Text, 1);

        Assert.True(resultValue.Success, resultValue.Summary);
        Assert.Single(resultValue.Warning);
    }

    [Fact]
    public void MachineModelNameInCommaNumberShouldBeReplaceWithAvoidBreakBadColumnStructure()
    {
        var recipeItem = new RecipeData { Number = 1, Name = "锁附,A" };
        recipeItem.put(2501, 1);

        var Text = RecipeCsvCodec.Encode(recipeItem);
        var resultValue = RecipeCsvCodec.Decode(Text, 1);

        Assert.True(resultValue.Success, resultValue.Summary);
        Assert.Equal("锁附，A", resultValue.Recipe!.Name);
    }
}
