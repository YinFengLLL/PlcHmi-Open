using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using PlcHmi.Core.Enums;

namespace PlcHmi.Core.Models;

/// <summary>
/// 历史记录「数据体」中的一个数据项 —— 也就是历史记录表里的一列。
///
/// <para><b>数值口径：</b>支持 <c>INT</c>（16 位整数）、<c>DINT</c>（32 位整数）与
/// <c>REAL</c>（32 位浮点，2026-10-24 现场要求）。
/// 整数类通常把工程量放大传输（如扭力 6.00 传 600），因此每项可配置
/// <see cref="DecimalBit"/>：显示时按 10 的幂除回去（<c>600</c> + 小数位 2 → <c>6.00</c>）；
/// <b>REAL 本身已是工程量</b>，小数位只表示"显示保留几位小数"，不再做缩放
/// （<c>12.3456</c> + 小数位 2 → <c>12.35</c>）。</para>
///
/// <para><b>两种显示增强（2026-10-17 现场要求）：</b>
/// ① <see cref="ShowAsAscii"/>——PLC 常把 4 个字符打包进一个 D 字（如批次号 "AB12"），
/// 勾选后按 PLC 内存顺序拆成 4 个字节逐字节转 ASCII 显示；
/// ② <see cref="Color"/>——给这一列标红 / 黄 / 绿，让关键列在长表格里一眼可辨。
/// 两者都只影响"怎么显示"，不改变采集到的原始值，也不改变 CSV 之外的数据链。</para>
///
/// <para><b>解码不走捷径：</b>地址解析与字序解码一律交给
/// <c>地址解析器</c> 与 <c>寄存器编解码器</c>，本类型只承载"这一列怎么显示"
/// 的信息，绝不在界面层自己拼字序（审查红线 R1）。</para>
/// </summary>
public sealed class HistoryDataItem
{
    /// <summary>列名，如 <c>实际扭力</c>。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>读取地址，如 <c>D2109</c>。只允许字元件（D / R 区），位元件不可作为数值来源。</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// 数值类型：INT（16 位，占 1 字）、DINT（32 位整数，占 2 字）或
    /// REAL（32 位浮点，占 2 字，字序由 <see cref="HistoryRecordConfig.WordOrder"/> 决定）。
    /// </summary>
    public TagDataType DataType { get; set; } = TagDataType.Int16;

    /// <summary>
    /// 小数位数（0–4）。
    /// <para>整数类型（INT / DINT）：显示值 = PLC 原始值 ÷ 10<sup>小数位</sup>（<c>600</c> → <c>6.00</c>）。</para>
    /// <para>REAL：原始值就是工程量，小数位只决定<b>显示保留几位</b>（<c>12.3456</c> → <c>12.35</c>），不缩放。</para>
    /// </summary>
    public int DecimalBit { get; set; }

    /// <summary>单位（可选），仅用于表头与 CSV 表头，如 <c>N·m</c>。不参与换算。</summary>
    public string? Unit { get; set; }

    /// <summary>
    /// 是否按 <b>ASCII 字符</b>显示。<b>仅 DINT 有效</b>（32 位 = 4 字节 = 4 个字符）。
    ///
    /// <para>现场常把字符打包进寄存器（批次号 "AB12"、产品码 "K7-9"）：勾选后按 PLC 的
    /// 内存顺序拆成 4 个字节逐字节转 ASCII；不可打印字节（&lt; 0x20 或 &gt; 0x7E）用
    /// <c>.</c> 占位，避免把控制字符直接画到表格里。</para>
    ///
    /// <para>勾选后 <see cref="DecimalBit"/> 不参与显示（字符没有小数点）。</para>
    /// </summary>
    public bool ShowAsAscii { get; set; }

    /// <summary>
    /// 这一列的强调色（无 / 红 / 黄 / 绿）。
    /// <para>只影响显示：表格文字与表头按此着色，让关键列（如"结果"）一眼可辨。
    /// 颜色是<b>管理员显式配置</b>的标注，不承载采集语义；文字始终在，颜色不单独传达信息。</para>
    /// </summary>
    public HistoryColumnColor Color { get; set; } = HistoryColumnColor.None;

    /// <summary>类型的中文简称，供配置界面下拉展示。</summary>
    [JsonIgnore]
    public string TypeText => DataType switch
    {
        TagDataType.Int16 => "INT",
        TagDataType.Int32 => "DINT",
        TagDataType.Float32 => "REAL",
        _ => DataType.ToString(),
    };

    /// <summary>
    /// 该列文字使用的画刷资源键（供界面绑定）。
    /// <para>与主题里的语义色一一对应；<b>不选颜色（<see cref="HistoryColumnColor.None"/>）时回落正文色</b>，
    /// 与加这个功能之前的观感完全一致——颜色是可选项，不是必填项。</para>
    /// </summary>
    [JsonIgnore]
    public string ColorKey => Color switch
    {
        HistoryColumnColor.Red => "DangerBrush",
        HistoryColumnColor.Yellow => "WarningBrush",
        HistoryColumnColor.Green => "SuccessBrush",
        _ => "TextPrimaryBrush",
    };

    /// <summary>
    /// 表头文字使用的画刷资源键。
    /// <para>与 <see cref="ColorKey"/> 的差别只在"不选颜色"时的回落值：表头原本用次要色
    /// （比正文弱一级），选了吗颜色就跟数据列同色，没选就还是那个次要色——
    /// 老配置升级后表头观感零变化。</para>
    /// </summary>
    [JsonIgnore]
    public string HeaderColorKey => Color switch
    {
        HistoryColumnColor.Red => "DangerBrush",
        HistoryColumnColor.Yellow => "WarningBrush",
        HistoryColumnColor.Green => "SuccessBrush",
        _ => "TextSecondaryBrush",
    };

    /// <summary>ASCII 显示是否可用（仅 DINT：4 字节刚好 4 个字符）。</summary>
    [JsonIgnore]
    public bool SettableAscii => DataType == TagDataType.Int32;

    /// <summary>
    /// 把 PLC 原始值转成显示文本。
    /// <para><see cref="ShowAsAscii"/> 且为 DINT 时按 ASCII 显示（忽略 <see cref="DecimalBit"/>）；
    /// REAL 只按小数位四舍五入（不缩放）；
    /// 其余整数类型按 <see cref="DecimalBit"/> 做数值缩放。</para>
    /// </summary>
    /// <param name="SourceValue">采集到的原始值（已在采集侧按类型解码）。</param>
    /// <param name="wordOrder">32 位数据的字序，决定 ASCII 的字节排列顺序；缺省按汇川默认的小字节序。</param>
    public string ShowText(double SourceValue, WordOrder? wordOrder = null)
    {
        if (ShowAsAscii && SettableAscii)
        {
            return ConvertAsciiText((int)SourceValue, wordOrder ?? WordOrder.LittleEndian);
        }

        var bitCount = Math.Clamp(DecimalBit, 0, HistoryRecordConfig.maxDecimalBit);
        var format = "F" + bitCount.ToString(CultureInfo.InvariantCulture);

        // REAL 已是工程量：只控制显示位数。若照整数那样 ÷10^位数，
        // 12.35 会被显示成 0.12 —— 这是"能存但显示错"，比报错更难查
        if (DataType == TagDataType.Float32)
        {
            return SourceValue.ToString(format, CultureInfo.InvariantCulture);
        }

        if (bitCount == 0)
        {
            return SourceValue.ToString("F0", CultureInfo.InvariantCulture);
        }

        // 先除再格式化：600 → 6 → "6.00"。除不尽的尾数由 F 格式四舍五入，避免出现 5.999999
        return (SourceValue / Math.Pow(10, bitCount)).ToString(format, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 把 32 位值按 PLC 的字符打包顺序拆成 4 个字节转 ASCII。
    ///
    /// <para><b>字节顺序（2026-10-17 现场确认）：</b>字符按<b>字内低字节在前</b>打包——
    /// 首字符落在第一个字的低字节（现场实例：读到的字是 <c>19279</c>（0x4B4F）时应显示
    /// <c>OK</c>，即 0x4F='O' 在低字节、0x4B='K' 在高字节）。
    /// 两个字之间的先后仍由 <paramref name="wordOrder"/> 决定，因此汇川默认的小字节序（CDAB）
    /// 下就是<b>整体小端</b>读取。</para>
    /// </summary>
    private static string ConvertAsciiText(int Value, WordOrder wordOrder)
    {
        Span<byte> byteValue = stackalloc byte[4];

        if (wordOrder is WordOrder.BigEndian)
        {
            // 高字在前：第一个字是 V 的高 16 位 → [高字低字节, 高字高字节, 低字低字节, 低字高字节]
            byteValue[0] = (byte)(Value >> 16);
            byteValue[1] = (byte)(Value >> 24);
            byteValue[2] = (byte)Value;
            byteValue[3] = (byte)(Value >> 8);
        }
        else
        {
            // 低字在前（CDAB，汇川默认）：整体小端 → [低字低字节, 低字高字节, 高字低字节, 高字高字节]
            byteValue[0] = (byte)Value;
            byteValue[1] = (byte)(Value >> 8);
            byteValue[2] = (byte)(Value >> 16);
            byteValue[3] = (byte)(Value >> 24);
        }

        var builder = new StringBuilder(4);

        foreach (var Bit in byteValue)
        {
            // 不可打印字节用 '.' 占位：控制字符画到表格里会直接把行高撑坏
            builder.Append(Bit is >= 0x20 and <= 0x7E ? (char)Bit : '.');
        }

        return builder.ToString();
    }

    /// <summary>深拷贝。</summary>
    public HistoryDataItem Clone() => new()
    {
        Name = Name,
        Address = Address,
        DataType = DataType,
        DecimalBit = DecimalBit,
        Unit = Unit,
        ShowAsAscii = ShowAsAscii,
        Color = Color,
    };
}

/// <summary>
/// 历史记录列的强调色（管理员为每个数据项选一种）。
/// <para>红 = 危险 / 不合格，黄 = 关注 / 警告，绿 = 合格 / 通过；
/// 命名沿用主题里的语义色（Danger / Warning / Success），便于界面直接映射。</para>
/// </summary>
public enum HistoryColumnColor
{
    /// <summary>不着色（默认，正文色）。</summary>
    None = 0,

    /// <summary>红。</summary>
    Red = 1,

    /// <summary>黄。</summary>
    Yellow = 2,

    /// <summary>绿。</summary>
    Green = 3,
}

/// <summary>
/// 历史记录配置。对应 <c>config/history-record.json</c>。
///
/// <para><b>工作原理：</b>上位机周期性读取 <see cref="TriggerAddress"/> 指定的 M 位，
/// 检测到 <b>0 → 1 的上升沿</b>时，把 <see cref="DataItemCollection"/> 里各地址的值读回来，
/// 拼成一条记录落盘。这样"PLC 完成一次动作"就对应历史表里的一行。</para>
///
/// <para><b>为什么是上升沿而不是"为 1 就记"：</b>PLC 的完成位置位后会保持到下次动作，
/// 按电平记录会每个扫描周期写一行，一天就能把磁盘写满；按沿记录才是"一次动作一条"。</para>
/// </summary>
public sealed class HistoryRecordConfig
{
    /// <summary>数据项上限。表格列宽有限，且一次采集要发出足够多的请求。</summary>
    public const int maxDataItemCount = 32;

    /// <summary>小数位数上限。</summary>
    public const int maxDecimalBit = 4;

    /// <summary>历史保留天数下限。</summary>
    public const int minRetainDaysNumber = 1;

    /// <summary>历史保留天数上限。</summary>
    public const int maxRetainDaysNumber = 365;

    /// <summary>数据体名称，显示在页面上，如 <c>锁螺丝记录</c>。</summary>
    public string Title { get; set; } = "历史记录";

    /// <summary>触发位地址，如 <c>M700</c>。<b>只允许 M 区</b>（位软元件），按上升沿记录。</summary>
    public string TriggerAddress { get; set; } = "M700";

    /// <summary>
    /// 读取完成后是否由上位机把触发位复位为 0（握手）。
    ///
    /// <para><b>默认开启</b>：PLC 置位 → 上位机读走数据 → 上位机复位，一次动作一条记录，
    /// PLC 程序无需自己清位。若 PLC 程序自己管理该位（读完自动清零），
    /// 关掉即可——此时上位机只按上升沿记录，绝不写触发位。</para>
    /// </summary>
    public bool ReadAfterResetTriggerBit { get; set; } = true;

    /// <summary>是否启用采集。停用后不再记录新数据，已有历史仍可查看 / 导出。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>32 位数据的字序。汇川默认小字节序（CDAB）。</summary>
    public WordOrder WordOrder { get; set; } = WordOrder.LittleEndian;

    /// <summary>历史保留天数，超期文件在启动与跨日时清理。</summary>
    public int RetainDays { get; set; } = 90;

    /// <summary>数据项列表（即表格的列，按显示顺序）。</summary>
    public List<HistoryDataItem> DataItemCollection { get; set; } = new();

    /// <summary>深拷贝（保存 / 编辑前先复制，避免对话框直接改到运行中的配置）。</summary>
    public HistoryRecordConfig Clone() => new()
    {
        Title = Title,
        TriggerAddress = TriggerAddress,
        ReadAfterResetTriggerBit = ReadAfterResetTriggerBit,
        Enabled = Enabled,
        WordOrder = WordOrder,
        RetainDays = RetainDays,
        DataItemCollection = DataItemCollection.Select(EntryItem => EntryItem.Clone()).ToList(),
    };
}

/// <summary>
/// 一条历史记录：触发时刻 + 与配置数据项<b>按下标一一对应</b>的原始数值。
/// <para>存原始值而不是格式化后的文本：换算规则（小数位）以后可能调整，
/// 存文本会让老数据与新数据格式不一致，且无法再参与统计。</para>
/// </summary>
public sealed class HistoryRecordEntry
{
    /// <summary>触发时刻（采集到上升沿的时间）。</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>各数据项的 PLC 原始值，顺序与 <see cref="HistoryRecordConfig.DataItemCollection"/> 一致。</summary>
    public List<double> NumericValueCollection { get; set; } = new();

    /// <summary>
    /// 工单号：写下这条记录时"上一次扫码的结果"（2026-11-12 现场要求）。
    ///
    /// <para><b>不落盘</b>（<see cref="JsonIgnoreAttribute"/>）：扫码信息只在本进程内有效，
    /// 关软件即清空。重新加载的历史文件里该字段为空，界面显示为空而不是伪造一个值。</para>
    /// </summary>
    [JsonIgnore]
    public string WorkOrder { get; set; } = string.Empty;

    /// <summary>深拷贝。</summary>
    public HistoryRecordEntry Clone() => new()
    {
        Timestamp = Timestamp,
        NumericValueCollection = [.. NumericValueCollection],
        WorkOrder = WorkOrder,
    };
}

/// <summary><c>config/history-record.json</c> 的顶层结构。</summary>
public sealed class HistoryRecordConfigFile
{
    /// <summary>数据体配置。</summary>
    public HistoryRecordConfig? Config { get; set; }
}

/// <summary>历史记录配置的校验结果。</summary>
public sealed class HistoryConfigValidate
{
    /// <summary>是否通过。</summary>
    public bool IsValid { get; init; } = true;

    /// <summary>错误列表。</summary>
    public List<string> ErrorCollection { get; init; } = new();

    /// <summary>给管理员看的一句话。</summary>
    public string Summary => IsValid ? "校验通过" : string.Join("；", ErrorCollection);
}
