using 上位机.核心.枚举;

namespace 上位机.核心.模型;

/// <summary>
/// 业务点位定义。对应 <c>config/tags.json</c> 中的一条记录。
/// <para>业务代码中一律使用软元件符号书写地址（如 <c>D100</c>、<c>M200</c>、<c>X10</c>），
/// 由 <c>IAddressResolver</c> 统一换算，禁止出现裸数字 Modbus 地址。</para>
/// </summary>
public sealed class 标签定义
{
    /// <summary>点位名称（英文标识，用于代码引用），如 <c>CycleTime</c>。</summary>
    public string 名称 { get; set; } = string.Empty;

    /// <summary>软元件地址，如 <c>"D10"</c>、<c>"M200"</c>、<c>"X10"</c>。</summary>
    public string 地址 { get; set; } = string.Empty;

    /// <summary>数据类型。</summary>
    public 标签数据类型 数据类型 { get; set; } = 标签数据类型.UInt16;

    /// <summary>访问权限。</summary>
    public 标签访问 访问 { get; set; } = 标签访问.读取;

    /// <summary>轮询分组。</summary>
    public 标签分组 分组 { get; set; } = 标签分组.快;

    /// <summary>显示缩放系数：显示值 = 原始值 × Scale + Offset。</summary>
    public double 缩放 { get; set; } = 1.0;

    /// <summary>显示偏移量。</summary>
    public double 偏移 { get; set; }

    /// <summary>工程单位，如 <c>"mm"</c>、<c>"s"</c>。</summary>
    public string? 单元 { get; set; }

    /// <summary>写入下限（参数校验用）。</summary>
    public double? 最小 { get; set; }

    /// <summary>写入上限（参数校验用）。</summary>
    public double? 最大 { get; set; }

    /// <summary>默认值（恢复默认用）。</summary>
    public double? 默认 { get; set; }

    /// <summary>32 位数据的字序。<b>汇川默认低字在前（CDAB）</b>。</summary>
    public 字顺序 字顺序 { get; set; } = 字顺序.小字节序;

    /// <summary>中文描述/注释。</summary>
    public string? 描述 { get; set; }

    /// <summary>界面显示名称（中文），为空时回退到 <see cref="描述"/> 或 <see cref="名称"/>。</summary>
    public string 显示名称 =>
        !string.IsNullOrWhiteSpace(描述) ? 描述! : 名称;

    /// <summary>是否可读。</summary>
    public bool 可读取 => 访问 is 标签访问.读取 or 标签访问.读取写入;

    /// <summary>是否可写。</summary>
    public bool 可写入 => 访问 is 标签访问.写入 or 标签访问.读取写入;
}

/// <summary>
/// 点位实时值快照。
/// </summary>
/// <param name="Name">点位名称。</param>
/// <param name="Value">原始值（未缩放）。</param>
/// <param name="ScaledValue">缩放后的工程值。</param>
/// <param name="Timestamp">采集时间。</param>
/// <param name="IsValid">数据是否有效（通信正常且解析成功）。</param>
public readonly record struct 标签新值(
    string 名称,
    object? 值,
    double 缩放后新值,
    DateTime 时间戳,
    bool 是否有效)
{
    /// <summary>无效值占位。</summary>
    public static 标签新值 无效(string 名称值) =>
        new(名称值, null, double.NaN, DateTime.Now, false);
}
