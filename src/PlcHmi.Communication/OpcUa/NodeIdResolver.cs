using System.Collections.Concurrent;
using System.Globalization;
using Opc.Ua;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;
using PlcHmi.Communication.Inovance;

namespace PlcHmi.Communication.OpcUa;

/// <summary>
/// OPC UA NodeId 推导器。把“区域 + Modbus 地址”推导为 PLC 服务端上的节点标识。
///
/// <para><b>为什么需要它：</b>业务层只持有软元件地址体系（X10 / D100），
/// 但接口契约传给客户端的是已换算的 Modbus 地址。OPC UA 服务端按软元件符号组织节点，
/// 因此客户端必须先经 <see cref="InovanceAddressParser.Reverse"/> 还原软元件符号，再按
/// <c>PLC连接选项.OPCUA节点格式</c> 模板拼出 NodeId——例如 D100 → <c>ns=2;s=D100</c>。</para>
///
/// <para><b>变量绑定优先</b>：配置条目绑定了变量表变量时，该地址段以<b>变量名</b>做符号寻址
/// （模板 <c>OPCUA变量格式</c>，默认 <c>ns=2;s=X轴命令.b_轴使能</c>）——
/// 变量节点的值类型与占用字数由 PLC 程序决定，不受基址调整影响。</para>
///
/// <para>推导结果按 (区域, 地址) 缓存，避免快轮询下每周期重复字符串拼接与解析；
/// 变量表重新导入时缓存整体失效重建。</para>
/// </summary>
public sealed class NodeIdResolver
{
    private readonly PlcConnectionOptions _options;
    private readonly InovanceAddressParser _parser;
    private readonly IVariableTableService? _variableTable;
    private readonly ConcurrentDictionary<(ModbusArea Area, int Address), NodeId> _bufferStore = new();

    public NodeIdResolver(PlcConnectionOptions Options, InovanceAddressParser Parser, IVariableTableService? VariableTable = null)
    {
        _options = Options ?? throw new ArgumentNullException(nameof(Options));
        _parser = Parser ?? throw new ArgumentNullException(nameof(Parser));
        _variableTable = VariableTable;

        if (_variableTable is not null)
        {
            // 重新导入变量表后，地址→变量的映射可能变化，旧缓存全部作废
            _variableTable.alreadyReImport += (_, _) => _bufferStore.Clear();
        }
    }

    /// <summary>推导指定区域与 Modbus 地址对应的 NodeId（带缓存）。</summary>
    public NodeId Resolve(ModbusArea areaValue, int modbusAddressValue) =>
        _bufferStore.GetOrAdd((areaValue, modbusAddressValue),
            Key => NodeId.Parse(ResolveText(Key.Area, Key.Address)));

    /// <summary>
    /// 尝试把 (区域, 起始地址) 匹配到变量表中的变量（按变量首地址）。
    /// 命中时返回条目并经 <paramref name="registerCountValue"/> 给出该变量的占用字数。
    /// </summary>
    public bool TryMatchVariable(ModbusArea areaValue, int startAddressValue, out VariableTableEntry? Entry, out int registerCountValue)
    {
        if (_variableTable is not null)
        {
            Entry = _variableTable.ByFirstAddressFind(areaValue, startAddressValue, out registerCountValue);
            return Entry is not null;
        }

        Entry = null;
        registerCountValue = 0;
        return false;
    }

    /// <summary>按变量名推导 NodeId（模板 <c>OPCUA变量格式</c>，默认 <c>ns=2;s={变量}</c>）。</summary>
    public string ResolveVariableText(string VariableName) =>
        _options.OpcUaVariableFormat
            .Replace("{命名空间}", _options.OpcUaNamespaceIndex.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{变量}", VariableName, StringComparison.Ordinal);

    /// <summary>按变量名推导 NodeId 对象。</summary>
    public NodeId ResolveVariableSectionPoint(string VariableName) => NodeId.Parse(ResolveVariableText(VariableName));

    /// <summary>推导指定区域与 Modbus 地址对应的 NodeId 文本（不做缓存，供测试与诊断）。</summary>
    public string ResolveText(ModbusArea areaValue, int modbusAddressValue)
    {
        var addressValue = _parser.Reverse(areaValue, modbusAddressValue);

        // 来源形如 "D100" / "X10"，前缀部分即软元件符号
        var symbolEnd = 0;
        while (symbolEnd < addressValue.Source.Length && !char.IsDigit(addressValue.Source[symbolEnd]))
        {
            symbolEnd++;
        }

        var softElement = addressValue.Source[..symbolEnd];
        var IndexText = addressValue.Source[symbolEnd..];

        return _options.OpcUaSectionPointFormat
            .Replace("{命名空间}", _options.OpcUaNamespaceIndex.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{软元件}", softElement, StringComparison.Ordinal)
            .Replace("{序号}", IndexText, StringComparison.Ordinal);
    }
}
