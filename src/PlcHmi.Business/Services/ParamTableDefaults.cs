using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// 参数表格的首次运行默认值。
///
/// <para>值与范围都以秒为单位；写入 PLC 时换算成 <b>32 位整数（DINT）毫秒</b>（×1000，占 2 字）。
/// 延时默认地址由 <paramref name="delayAddressMin"/> 推导，<b>间隔为 <see cref="ParameterEntryDefinition.delayOccupyWordCount"/></b>
/// （每条占 2 字，相邻地址会互相吃高位字），改专属区时不会再漏改这里
/// （曾硬编码 D700–D702 踩过坑）。</para>
/// </summary>
public static class ParameterTableDefaultValue
{
    /// <summary>创建默认延时条目（取料 / 压装 / 放料）。</summary>
    public static IEnumerable<ParameterEntryDefinition> CreateDelay(int delayAddressMin) =>
    [
        new() { Id = "delay-1", Name = "取料延时", Address = $"D{delayAddressMin}", Value = 0.5, Unit = "s", Min = 0, Max = 2147483.647, Order = 1 },
        new() { Id = "delay-2", Name = "压装延时", Address = $"D{delayAddressMin + ParameterEntryDefinition.delayOccupyWordCount}", Value = 0.8, Unit = "s", Min = 0, Max = 2147483.647, Order = 2 },
        new() { Id = "delay-3", Name = "放料延时", Address = $"D{delayAddressMin + ParameterEntryDefinition.delayOccupyWordCount * 2}", Value = 0.3, Unit = "s", Min = 0, Max = 2147483.647, Order = 3 },
    ];

    /// <summary>创建默认按钮条目。按钮地址统一放 M5000–M5099：与系统保留段彻底隔开。</summary>
    public static IEnumerable<ParameterEntryDefinition> CreateButton() =>
    [
        new() { Id = "btn-1", Name = "MES 开关", Address = "M5000", Order = 1 },
    ];
}
