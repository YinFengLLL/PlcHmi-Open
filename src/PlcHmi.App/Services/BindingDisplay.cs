using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.App.Services;

/// <summary>
/// 绑定地址的界面显示规则。
///
/// <para><b>为什么需要统一规则</b>：OPC UA 模式下绑定了变量的元件按变量名符号寻址，
/// 物理地址只在 Modbus 模式下有意义。此时界面上继续显示物理地址会误导操作员
/// （PLC 改基址后变量名不变、地址早就不对了）。</para>
///
/// <list type="bullet">
/// <item>OPC UA 模式 + 已绑定变量 → 显示<b>变量名</b>（不关心物理地址）</item>
/// <item>Modbus 模式或未绑定变量 → 显示软元件地址</item>
/// </list>
/// </summary>
public static class BindingDisplay
{
    /// <summary>当前是否为 OPC UA 通讯模式（该模式下按变量名符号寻址）。</summary>
    public static bool IsVariableAddressing(PlcConnectionOptions? Options) =>
        Options is not null && Options.CommunicationProtocol == CommunicationProtocol.OPCUA;

    /// <summary>地址列的显示文本：OPC UA 且已绑定变量时显示变量名，否则显示物理地址。</summary>
    public static string AddressText(bool VariableAddressing, string? VariableName, string Address) =>
        VariableAddressing && !string.IsNullOrWhiteSpace(VariableName) ? VariableName.Trim() : Address;

    /// <summary>
    /// 未绑定变量时的占位提示。
    /// <para>OPC UA 模式未绑定变量时会退化为按软元件反解寻址（如 <c>ns=2;s=D100</c>），
    /// 因此不是错误，但应提示操作员可改用变量绑定。</para>
    /// </summary>
    public const string unboundHint = "（未绑定变量）";
}
