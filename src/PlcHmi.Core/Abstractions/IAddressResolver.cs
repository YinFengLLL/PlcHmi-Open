using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Core.Abstractions;

/// <summary>
/// 汇川软元件地址解析器契约。<b>这是整个通信层的核心</b>：
/// 把 <c>"X10"</c> 这类软元件符号换算成可以直接发给 Modbus 的地址。
/// </summary>
/// <remarks>
/// 汇川 PLC 的地址映射不是标准 4xxxx 顺序编号，而是“功能区起始地址 + 软元件序号”。
/// 其中 X / Y 必须按 <b>八进制</b> 解析序号——<c>X10</c> 是第 8 个点而不是第 10 个。
/// 不同机型系列的地址体系完全不同，故解析器按 <see cref="PlcSeries"/> 区分。
/// </remarks>
public interface IAddressParser
{
    /// <summary>当前解析器适用的机型系列。</summary>
    PlcSeries Series { get; }

    /// <summary>解析软元件地址。失败时抛出 <see cref="Exceptions.AddressParseException"/>。</summary>
    /// <param name="softElement">软元件符号，如 <c>"X10"</c>、<c>"D100"</c>、<c>"MW20"</c>。</param>
    PlcAddress Parse(string SoftElement);

    /// <summary>尝试解析软元件地址，不抛异常。</summary>
    bool TryParse(string SoftElement, out PlcAddress? addressValue);

    /// <summary>按数据类型计算占用的寄存器数量（32 位数据占 2 个）。</summary>
    int GetRegisterCount(TagDataType DataType);
}
