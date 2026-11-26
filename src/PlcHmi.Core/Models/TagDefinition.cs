using PlcHmi.Core.Enums;

namespace PlcHmi.Core.Models;

/// <summary>
/// 业务点位定义。对应 <c>config/tags.json</c> 中的一条记录。
/// <para>业务代码中一律使用软元件符号书写地址（如 <c>D100</c>、<c>M200</c>、<c>X10</c>），
/// 由 <c>IAddressResolver</c> 统一换算，禁止出现裸数字 Modbus 地址。</para>
/// </summary>
public sealed class TagDefinition
{
    /// <summary>点位名称（英文标识，用于代码引用），如 <c>CycleTime</c>。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>软元件地址，如 <c>"D10"</c>、<c>"M200"</c>、<c>"X10"</c>。</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>数据类型。</summary>
    public TagDataType DataType { get; set; } = TagDataType.UInt16;

    /// <summary>访问权限。</summary>
    public TagAccessQuery AccessQuery { get; set; } = TagAccessQuery.Read;

    /// <summary>轮询分组。</summary>
    public TagGroup Group { get; set; } = TagGroup.Fast;

    /// <summary>显示缩放系数：显示值 = 原始值 × Scale + Offset。</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>显示偏移量。</summary>
    public double Offset { get; set; }

    /// <summary>工程单位，如 <c>"mm"</c>、<c>"s"</c>。</summary>
    public string? Unit { get; set; }

    /// <summary>写入下限（参数校验用）。</summary>
    public double? Min { get; set; }

    /// <summary>写入上限（参数校验用）。</summary>
    public double? Max { get; set; }

    /// <summary>默认值（恢复默认用）。</summary>
    public double? Default { get; set; }

    /// <summary>32 位数据的字序。<b>汇川默认低字在前（CDAB）</b>。</summary>
    public WordOrder WordOrder { get; set; } = WordOrder.LittleEndian;

    /// <summary>中文描述/注释。</summary>
    public string? Description { get; set; }

    /// <summary>界面显示名称（中文），为空时回退到 <see cref="Description"/> 或 <see cref="Name"/>。</summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Description) ? Description! : Name;

    /// <summary>是否可读。</summary>
    public bool Readable => AccessQuery is TagAccessQuery.Read or TagAccessQuery.ReadWrite;

    /// <summary>是否可写。</summary>
    public bool Writable => AccessQuery is TagAccessQuery.Write or TagAccessQuery.ReadWrite;
}

/// <summary>
/// 点位实时值快照。
/// </summary>
/// <param name="Name">点位名称。</param>
/// <param name="Value">原始值（未缩放）。</param>
/// <param name="ScaledValue">缩放后的工程值。</param>
/// <param name="Timestamp">采集时间。</param>
/// <param name="IsValid">数据是否有效（通信正常且解析成功）。</param>
public readonly record struct TagNewValue(
    string Name,
    object? Value,
    double scaleAfterNewValue,
    DateTime timeStamp,
    bool IsValid)
{
    /// <summary>无效值占位。</summary>
    public static TagNewValue Invalid(string nameValue) =>
        new(nameValue, null, double.NaN, DateTime.Now, false);
}
