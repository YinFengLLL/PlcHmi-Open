using PlcHmi.Core.Models;

namespace PlcHmi.Tests;

/// <summary>
/// 点胶示教指令码表（2026-10-21 现场口径：PLC 指令表 0 结束 / 1 起点 / 2 直线 … 9 对针）。
///
/// <para>钉死两件事：<b>码值顺序</b>（写进 D3400 段的就是这个数，错一位现场动作就完全不同）
/// 与<b>文字映射</b>（点表「指令码」列显示的文字）。</para>
/// </summary>
public class InstructionCodeDefinitionTest
{
    [Fact]
    public void InstructionCodeTableShouldAndPlcCommandTableConsistent()
    {
        var expected = new[]
        {
            (code: 0, Name: "结束"),
            (code: 1, Name: "起点"),
            (code: 2, Name: "直线"),
            (code: 3, Name: "直线终点"),
            (code: 4, Name: "圆弧中点"),
            (code: 5, Name: "圆弧终点"),
            (code: 6, Name: "单点"),
            (code: 7, Name: "空移"),
            (code: 8, Name: "刮胶"),
            (code: 9, Name: "对针"),
        };

        Assert.Equal(expected.Length, InstructionCodeDefinition.All.Count);

        for (var index = 0; index < expected.Length; index++)
        {
            var Item = InstructionCodeDefinition.All[index];
            Assert.Equal(expected[index].code, Item.code);
            Assert.Equal(expected[index].Name, Item.Name);
            Assert.Equal($"{expected[index].code:00} - {expected[index].Name}", Item.Show);
        }
    }

    [Theory]
    [InlineData(0, "结束")]
    [InlineData(2, "直线")]
    [InlineData(9, "对针")]
    public void CodeValueShouldShowAsPairShouldLiteral(int code, string literal) =>
        Assert.Equal(literal, InstructionCodeDefinition.ShowText(code));

    [Fact]
    public void UnknownCodeValueShouldOriginalSampleShowNumber()
    {
        // 老机种里可能存着按旧口径手填的值（例如 100），显示原值才能与 PLC 对照
        Assert.Equal("100", InstructionCodeDefinition.ShowText(100));
        Assert.Null(InstructionCodeDefinition.Find(10));
        Assert.Null(InstructionCodeDefinition.Find(-1));
    }
}
