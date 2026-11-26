namespace PlcHmi.Core.Enums;

/// <summary>
/// 通讯协议类型。决定 DI 装配哪套客户端实现（<c>appsettings.json → PLC连接.通讯协议</c>）。
/// <para>两套协议实现共用 <c>Modbus客户端接口</c> 契约，业务层对协议无感知。</para>
/// </summary>
public enum CommunicationProtocol
{
    /// <summary>Modbus TCP（FluentModbus 主站，现有默认实现）。</summary>
    ModbusTCP = 0,

    /// <summary>OPC UA 客户端，连接 PLC 内置的 OPC UA 服务端。</summary>
    OPCUA = 1,
}
