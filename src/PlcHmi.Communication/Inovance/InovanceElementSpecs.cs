using PlcHmi.Core.Enums;

namespace PlcHmi.Communication.Inovance;

/// <summary>
/// 汇川软元件规格表（基址 / 序号上限 / 专用功能码）与地址前缀对照表。
///
/// <para><b>为什么单独一个文件：</b>这里全是"数据"——从现场文档抄下来的基址与序号上限，
/// 本身没有逻辑；<see cref="InovanceAddressParser"/> 里则全是"算法"。两者混在一个文件里，
/// 既让解析器越过 400 行审查阈值，也让基址表被顺手改错时不容易被看出来。</para>
///
/// <para><b>改动前必读：</b>基址错一个字节，所有数据都会错位。改这里的任何数字之前，
/// 请对照 PLC 文档与 <c>docs/02-参数-示教-机种需求设计.md</c> 的地址规划，
/// 并确认 <see cref="InovanceAddressParser.Reverse"/> 与新表仍然严格互逆 —— 两处必须同改。</para>
/// </summary>
internal static class InovanceSoftElementSpecTable
{
    // ── 小型 PLC（EASY 系列 / H5U / H3U）────────────────────────────────
    // 来源：EMQX Neuron 汇川插件文档、factory-builder 帮助平台（两处交叉验证一致）
    public static readonly Dictionary<SoftElement, SoftElementSpec> SmallPlc = new()
    {
        // 位元件 —— 线圈区，功能码 01 / 05 / 0F
        [SoftElement.M] = new(SoftElement.M, ModbusArea.Coil, 0x0000, 10, 7999),
        [SoftElement.B] = new(SoftElement.B, ModbusArea.Coil, 0x3000, 10, 32767),
        [SoftElement.S] = new(SoftElement.S, ModbusArea.Coil, 0xE000, 10, 4095),

        // X 是物理输入，Modbus 语义上属于离散输入表（FC02）；Y 是输出，属线圈表（FC01）。
        // 2026-10-17 现场第二次报障后修正：此前把 X 也放进线圈表，等于用 FC01 读输入区——
        // 现场表现正是"Y 一直正确、X 一直对不上"（读到的是同段地址上无关的位）。
        // 兜底仍在：读不到（抛异常）或读全 0 而另一位区有信号时，轮询引擎会切到另一位区
        // （见 PollingEngine.IO.cs），个别把 X 放在线圈表的机型仍能自愈。
        [SoftElement.X] = new(SoftElement.X, ModbusArea.DiscreteInput, 0xF800, 8, 1023),   // X0-X1777，八进制
        [SoftElement.Y] = new(SoftElement.Y, ModbusArea.Coil, 0xFC00, 8, 1023),   // Y0-Y1777，八进制
        [SoftElement.SpecialRegister] = new(SoftElement.SpecialRegister, ModbusArea.Coil, 0x2400, 10, 1023),
        [SoftElement.T] = new(SoftElement.T, ModbusArea.Coil, 0xF000, 10, 511),
        [SoftElement.C] = new(SoftElement.C, ModbusArea.Coil, 0xF400, 10, 255),

        // 字元件 —— 保持寄存器区，功能码 03 / 06 / 10
        [SoftElement.D] = new(SoftElement.D, ModbusArea.HoldingRegister, 0x0000, 10, 7999),
        [SoftElement.R] = new(SoftElement.R, ModbusArea.HoldingRegister, 0x3000, 10, 32767),
        [SoftElement.SystemUnifiedRegister] = new(SoftElement.SystemUnifiedRegister, ModbusArea.HoldingRegister, 0x2400, 10, 1023),
    };

    // ── 中型 PLC（AM600 / AC800，CODESYS 体系）──────────────────────────
    public static readonly Dictionary<SoftElement, SoftElementSpec> mediumPlc = new()
    {
        [SoftElement.Q] = new(SoftElement.Q, ModbusArea.Coil, 0x0000, 10, 65535),
        [SoftElement.Interface] = new(SoftElement.Interface, ModbusArea.DiscreteInput, 0x0000, 10, 65535),
        [SoftElement.MW] = new(SoftElement.MW, ModbusArea.HoldingRegister, 0x0000, 10, 65535),

        // 汇川中型 PLC 的 SM / SD 需专用功能码，标准功能码无法访问
        [SoftElement.SpecialRegister] = new(SoftElement.SpecialRegister, ModbusArea.Coil, 0x0000, 10, 7999,
            readFunctionCanCode: 0x31, writeSingleFunctionCanCode: 0x35, writeMultiFunctionCanCode: 0x3F),
        [SoftElement.SystemUnifiedRegister] = new(SoftElement.SystemUnifiedRegister, ModbusArea.HoldingRegister, 0x0000, 10, 7999,
            readFunctionCanCode: 0x33, writeSingleFunctionCanCode: 0x36, writeMultiFunctionCanCode: 0x40),
    };

    /// <summary>
    /// 地址前缀 → 软元件。
    ///
    /// <para><b>为什么用显式表而不是 <c>Enum.TryParse</c>：</b>前缀是现场约定（<c>"X"/"SM"/"QX"</c>），
    /// 与枚举成员名是两件事。此前靠枚举名去反解前缀，等于把"现场地址写法"绑死在代码标识符上——
    /// 枚举成员一旦改名（哪怕只是换成中文），解析就会静默失配。显式表把两者解耦。</para>
    ///
    /// <para>中型 PLC 的位寻址写作 <c>QX0.3</c> / <c>IX1.0</c>，前缀里的 X 只是"按位"的表示，
    /// 对应枚举为 <see cref="SoftElement.Q"/> 与 <see cref="SoftElement.Interface"/>。</para>
    /// </summary>
    public static readonly Dictionary<string, SoftElement> prefixPairPer = new(StringComparer.OrdinalIgnoreCase)
    {
        ["X"] = SoftElement.X,
        ["Y"] = SoftElement.Y,
        ["M"] = SoftElement.M,
        ["B"] = SoftElement.B,
        ["S"] = SoftElement.S,
        ["D"] = SoftElement.D,
        ["R"] = SoftElement.R,
        ["T"] = SoftElement.T,
        ["C"] = SoftElement.C,
        ["SM"] = SoftElement.SpecialRegister,
        ["SD"] = SoftElement.SystemUnifiedRegister,
        ["Q"] = SoftElement.Q,
        ["QX"] = SoftElement.Q,
        ["I"] = SoftElement.Interface,
        ["IX"] = SoftElement.Interface,
        ["MW"] = SoftElement.MW,
    };

    /// <summary>按机型取规格表。</summary>
    public static Dictionary<SoftElement, SoftElementSpec> ByMachineModel(PlcSeries seriesValue) =>
        seriesValue == PlcSeries.SmallPlc ? SmallPlc : mediumPlc;
}
