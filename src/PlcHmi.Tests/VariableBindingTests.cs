using Microsoft.Extensions.Logging.Abstractions;
using PlcHmi.Business.Configuration;
using PlcHmi.Business.Services;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;
using PlcHmi.Communication.Simulation;
using PlcHmi.Communication.Inovance;
using PlcHmi.Communication.Modbus;
using PlcHmi.Communication.OpcUa;
using PlcHmi.Core.Abstractions;

namespace PlcHmi.Tests;

/// <summary>
/// 变量绑定回填与 OPC UA 变量符号寻址单测：
/// 地址回填、失效降级、NodeId 命中/回退、仿真客户端的类型化读写往返。
/// </summary>
public class VariableBindingAndAddressingTest
{
    /// <summary>与样例变量表对应的文档（D1200 REAL / D1212 INT / M200 BOOL）。</summary>
    private static VariableTableDocument CreateDocument() => new()
    {
        EntryCollection =
        [
            new() { VariableName = "X轴参数.r_定位速度", SoftElementAddress = "D1200", DataType = TagDataType.Float32 },
            new() { VariableName = "X轴命令.i_手动速度档位", SoftElementAddress = "D1212", DataType = TagDataType.Int16 },
            new() { VariableName = "系统状态.b_运行", SoftElementAddress = "M200", DataType = TagDataType.Boolean },
            new() { VariableName = "X轴命令.b_轴使能", SoftElementAddress = "D1210.0", DataType = TagDataType.Boolean },
        ],
    };

    private static IAddressParser CreateParser() => new InovanceAddressParser(PlcSeries.SmallPlc);

    private static (VariableTableService Table, NodeIdResolver Resolver) CreateResolver(VariableTableDocument? Document = null)
    {
        var Table = new VariableTableService(Document ?? CreateDocument(), CreateParser(), NullLogger<VariableTableService>.Instance);
        var Resolver = new NodeIdResolver(new PlcConnectionOptions(), new InovanceAddressParser(PlcSeries.SmallPlc), Table);
        return (Table, Resolver);
    }

    // ── 绑定回填 ────────────────────────────────────────────────────────

    [Fact]
    public void Backfill_VariableStoreAt_ShouldReturnVariableAddress()
    {
        var (Table, _) = CreateResolver();

        var Address = VariableBindingBackfill.ParseAddress("X轴参数.r_定位速度", Table, CreateParser(), "D1600", out var error);

        Assert.Equal(string.Empty, error);
        Assert.Equal("D1200", Address);
    }

    [Fact]
    public void Backfill_VariableNotExist_ShouldRetainOriginalAddressAndDescriptionOriginalReason()
    {
        var (Table, _) = CreateResolver();

        var Address = VariableBindingBackfill.ParseAddress("不存在的变量", Table, CreateParser(), "D1600", out var error);

        Assert.Contains("不在变量表中", error);
        Assert.Equal("D1600", Address);
    }

    [Fact]
    public void Backfill_WordBitAddress_ShouldAcceptAndOriginalSampleBackfill()
    {
        // D1210.0 是 D 字内位（汇川位打包）：2026-09-17 起按整字读-改-写可寻址
        var (Table, _) = CreateResolver();

        var Address = VariableBindingBackfill.ParseAddress("X轴命令.b_轴使能", Table, CreateParser(), "M220", out var error);

        Assert.Equal(string.Empty, error);
        Assert.Equal("D1210.0", Address);
    }

    [Fact]
    public void Backfill_Unbound_ShouldKeepOriginalAddressNoneError()
    {
        var (Table, _) = CreateResolver();

        var Address = VariableBindingBackfill.ParseAddress(null, Table, CreateParser(), "M220", out var error);

        Assert.Equal(string.Empty, error);
        Assert.Equal("M220", Address);
    }

    [Fact]
    public void Backfill_RecipeConfig_ShouldBatchBackfillParameterItemAndWidgetAddress()
    {
        var Config = new RecipeConfig
        {
            Parameter =
            [
                new RecipeParameterSpec { Key = "X.LocateSpeed", Name = "X 轴定位速度", Address = 1600, Variable = "X轴参数.r_定位速度" },
            ],
            Widget = new RecipeWidgetConfig { RecipeNumberAddress = 3330, RecipeNumberVariable = "X轴命令.i_手动速度档位" },
        };

        var Warning = VariableBindingBackfill.ApplyRecipeConfig(Config, CreateDocument(), CreateParser());

        Assert.Empty(Warning);
        Assert.Equal(1200, Config.Parameter[0].Address);       // D1200 → 1200
        Assert.Equal(1212, Config.Widget.RecipeNumberAddress);    // D1212 → 1212
    }

    [Fact]
    public void Backfill_RecipeConfig_VariableFailEffectShouldGiveWarningAndNotChangeAddress()
    {
        var Config = new RecipeConfig
        {
            Parameter =
            [
                new RecipeParameterSpec { Key = "X.LocateSpeed", Name = "X 轴定位速度", Address = 1600, Variable = "已删除的变量" },
            ],
        };

        var Warning = VariableBindingBackfill.ApplyRecipeConfig(Config, CreateDocument(), CreateParser());

        Assert.Single(Warning);
        Assert.Equal(1600, Config.Parameter[0].Address);
    }

    // ── OPC UA 变量符号寻址 ─────────────────────────────────────────────

    [Fact]
    public void Resolver_VariableHit_ShouldByVariableNameGenerateNodeId()
    {
        var (_, Resolver) = CreateResolver();

        Assert.True(Resolver.TryMatchVariable(ModbusArea.HoldingRegister, 1200, out _, out var Count));
        Assert.Equal(2, Count);   // REAL 占 2 字
        Assert.Equal("ns=2;s=X轴参数.r_定位速度", Resolver.ResolveVariableText("X轴参数.r_定位速度"));
    }

    [Fact]
    public void Resolver_VariableMiss_ShouldFallbackSoftElementReverseTemplate()
    {
        var (_, Resolver) = CreateResolver();

        Assert.False(Resolver.TryMatchVariable(ModbusArea.HoldingRegister, 1600, out _, out _));
        Assert.Equal("ns=2;s=D1600", Resolver.ResolveText(ModbusArea.HoldingRegister, 1600));
    }

    [Fact]
    public async Task SimulationClient_VariableBindingSection_ShouldByVariableSectionPointTypeConvertReadWriteRoundTrip()
    {
        // REAL 变量占 D1200–D1201：读(1200,2) 命中变量段 → 读 Float32 节点 →
        // 经寄存器编解码器（CDAB）拆为 2 个寄存器，与业务侧解码语义一致
        var Document = CreateDocument();
        var Table = new VariableTableService(Document, CreateParser(), NullLogger<VariableTableService>.Instance);
        var Resolver = new NodeIdResolver(new PlcConnectionOptions(), new InovanceAddressParser(PlcSeries.SmallPlc), Table);
        var Device = new VirtualPlcDevice(NullLogger<VirtualPlcDevice>.Instance);
        var Client = new SimulationOpcUaClient(Device, Resolver, NullLogger<SimulationOpcUaClient>.Instance);
        try
        {
            await Client.ConnectAsync();

            // 写入 Float32 = 3.14 的寄存器对（CDAB：D1200=低字 0xF5C3，D1201=高字 0x4048）
            await Client.WriteMultiRegisterAsync(1200, RegisterCodec.Encode(TagDataType.Float32, 3.14));

            var ReadBackOnce = await Client.ReadHoldingRegisterAsync(1200, 2);
            var Value = RegisterCodec.Decode(TagDataType.Float32, ReadBackOnce);

            Assert.Equal(3.14, (float)Value, 0.001);

            // 单字 INT 变量：按变量节点读写
            await Client.WriteSingleRegisterAsync(1212, 2);
            var gear = await Client.ReadHoldingRegisterAsync(1212, 1);
            Assert.Equal((ushort)2, gear[0]);
        }
        finally
        {
            await Client.DisposeAsync();
            await Device.DisposeAsync();
        }
    }
}
