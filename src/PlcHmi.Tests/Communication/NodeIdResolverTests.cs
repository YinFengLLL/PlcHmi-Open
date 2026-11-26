using PlcHmi.Core.Enums;
using PlcHmi.Core.Exceptions;
using PlcHmi.Core.Models;
using PlcHmi.Communication.Inovance;
using PlcHmi.Communication.OpcUa;

namespace PlcHmi.Tests;

/// <summary>
/// NodeId 推导规则单测。覆盖软元件反解（含 X/Y 八进制还原）、模板占位符与非法地址。
/// </summary>
public class NodeIdResolverTest
{
    private static NodeIdResolver CreateResolver(Action<PlcConnectionOptions>? adjustWhole = null)
    {
        var Options = new PlcConnectionOptions();
        adjustWhole?.Invoke(Options);
        return new NodeIdResolver(Options, new InovanceAddressParser(Options.Series));
    }

    // ── 软元件符号推导 ──────────────────────────────────────────────────

    [Theory]
    [InlineData(ModbusArea.HoldingRegister, 100, "ns=2;s=D100")]     // D 区
    [InlineData(ModbusArea.Coil, 100, "ns=2;s=M100")]           // M 区
    [InlineData(ModbusArea.DiscreteInput, 63496, "ns=2;s=X10")]      // X 八进制：0xF800+8 → X10（不是 X8）；X 是输入走 FC02
    [InlineData(ModbusArea.Coil, 64512, "ns=2;s=Y0")]           // Y0 = 0xFC00
    [InlineData(ModbusArea.HoldingRegister, 12293, "ns=2;s=R5")]     // R 基址 0x3000
    [InlineData(ModbusArea.Coil, 12295, "ns=2;s=B7")]           // B 基址 0x3000（线圈区）
    [InlineData(ModbusArea.Coil, 9226, "ns=2;s=SM10")]          // SM 基址 0x2400
    [InlineData(ModbusArea.HoldingRegister, 9219, "ns=2;s=SD3")]     // SD 基址 0x2400
    [InlineData(ModbusArea.Coil, 61440, "ns=2;s=T0")]           // T 基址 0xF000
    [InlineData(ModbusArea.Coil, 62464, "ns=2;s=C0")]           // C 基址 0xF400
    [InlineData(ModbusArea.Coil, 57344, "ns=2;s=S0")]           // S 基址 0xE000
    public void Resolve_NormalSeeSoftElement_ShouldByRuleThenGenerateSectionPointText(ModbusArea areaValue, int addressValue, string expected)
    {
        var Resolver = CreateResolver();

        Assert.Equal(expected, Resolver.ResolveText(areaValue, addressValue));
    }

    [Fact]
    public void Resolve_NamespaceIndexConfigurable_ShouldReflectAtSectionPointText()
    {
        var Resolver = CreateResolver(Options => Options.OpcUaNamespaceIndex = 5);

        Assert.Equal("ns=5;s=D100", Resolver.ResolveText(ModbusArea.HoldingRegister, 100));
    }

    [Fact]
    public void Resolve_SelfDefinitionFormatTemplate_ShouldReplaceAllOccupyBitSymbol()
    {
        var Resolver = CreateResolver(Options => Options.OpcUaSectionPointFormat = "{命名空间}|{软元件}|{序号}");

        Assert.Equal("2|SM|10", Resolver.ResolveText(ModbusArea.Coil, 9226));
    }

    [Fact]
    public void Resolve_ReturnNodeId_IdSymbolShouldAsSoftElementDocumentWriteShapeStyle()
    {
        var Resolver = CreateResolver();
        var sectionPoint = Resolver.Resolve(ModbusArea.HoldingRegister, 100);

        Assert.Equal("D100", sectionPoint.Identifier.ToString());
    }

    [Fact]
    public void Resolve_SameOneAddressMultiTimesInvoke_ShouldHitBufferStoreReturnSameOneRealCase()
    {
        var Resolver = CreateResolver();

        var firstTime = Resolver.Resolve(ModbusArea.HoldingRegister, 100);
        var secondTime = Resolver.Resolve(ModbusArea.HoldingRegister, 100);

        Assert.Same(firstTime, secondTime);
    }

    // ── 非法地址 ────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_AddressAtSoftElementAreaBetweenEmptyGap_ShouldThrowAddressParseException()
    {
        // 0x2000（8192）位于 M 区上限（7999 → 0x1F3F）与 SM 区基址（0x2400）之间的空隙
        var Resolver = CreateResolver();

        Assert.Throws<AddressParseException>(() => Resolver.ResolveText(ModbusArea.Coil, 8192));
    }

    [Fact]
    public void Resolve_SmallPlcNoneDiscreteInputAreaSoftElement_ShouldThrowAddressParseException()
    {
        var Resolver = CreateResolver();

        Assert.Throws<AddressParseException>(() => Resolver.ResolveText(ModbusArea.DiscreteInput, 0));
    }

    [Fact]
    public void Resolve_NegativeAddress_ShouldThrowAddressParseException()
    {
        var Resolver = CreateResolver();

        Assert.Throws<AddressParseException>(() => Resolver.ResolveText(ModbusArea.HoldingRegister, -1));
    }
}
