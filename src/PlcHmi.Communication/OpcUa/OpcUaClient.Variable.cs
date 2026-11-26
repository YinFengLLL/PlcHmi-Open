using Opc.Ua;
using Opc.Ua.Client;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;
using PlcHmi.Communication.Modbus;

namespace PlcHmi.Communication.OpcUa;

/// <summary>
/// OPC UA 客户端的变量绑定读写部分。
///
/// <para><b>变量段的语义</b>：变量绑定的地址段整段对应 PLC 服务端上的一个类型化节点
/// （如 REAL 变量节点值就是浮点数本身），与逐寄存器节点完全不同——
/// 因此读写前先把连续地址切成操作块，变量段经
/// <see cref="RegisterCodec"/> 做“类型值 ↔ 寄存器数组”转换（红线 R1：字序处理只走编解码器）。</para>
/// </summary>
public sealed partial class OpcUaClient
{
    /// <summary>
    /// 读一个变量节点并按其数据类型转换为寄存器数组（字序由寄存器编解码器统一处理，红线 R1）。
    /// 类型转换失败或占用字数与请求不符时返回 <c>false</c>，调用方回退到逐寄存器路径。
    /// </summary>
    private async Task<bool> TryReadVariableBlock(
        ISession Session, VariableTableEntry Variable, int countValue, ushort[] target, int targetOffset, CancellationToken cancellationToken)
    {
        try
        {
            var ValueList = await BatchRead(Session, [_resolver.ResolveVariableSectionPoint(Variable.VariableName)], cancellationToken)
                .ConfigureAwait(false);

            if (ValueList.Count != 1 || ValueList[0].Value is null)
            {
                return false;
            }

            var registerList = RegisterCodec.Encode(Variable.DataType, Convert.ToDouble(ValueList[0].Value));

            if (registerList.Length != countValue)
            {
                return false;
            }

            Array.Copy(registerList, 0, target, targetOffset, countValue);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (InvalidCastException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    /// <summary>
    /// 把寄存器片段按变量数据类型还原为单值并写入变量节点（字序由寄存器编解码器统一处理）。
    /// 类型转换失败或占用字数与请求不符时返回 <c>false</c>，调用方回退到逐寄存器路径。
    /// </summary>
    private async Task<bool> TryWriteVariableBlock(
        ISession Session, VariableTableEntry Variable, IReadOnlyList<ushort> ValueList, int sourceOffset, int countValue,
        CancellationToken cancellationToken)
    {
        if (countValue <= 0 || sourceOffset + countValue > ValueList.Count)
        {
            return false;
        }

        var sliceSection = new ushort[countValue];
        for (var Entry = 0; Entry < countValue; Entry++)
        {
            sliceSection[Entry] = ValueList[sourceOffset + Entry];
        }

        object singleValue;
        try
        {
            singleValue = RegisterCodec.Decode(Variable.DataType, sliceSection);
        }
        catch (ArgumentException)
        {
            return false;
        }

        var writeCollection = new WriteValueCollection
        {
            new WriteValue
            {
                NodeId = _resolver.ResolveVariableSectionPoint(Variable.VariableName),
                AttributeId = Attributes.Value,
                Value = new DataValue(new Variant(singleValue)),
            },
        };

        await BatchWrite(Session, writeCollection, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 把连续地址区间切分为读写操作块：<b>变量首地址命中的整段优先</b>（一块 = 一个变量节点），
    /// 其余地址按连续段合并（一次批量请求）。
    ///
    /// <para>轮询引擎合并的是“同功能码区”的连续地址，其中可能嵌着变量绑定段——
    /// 变量段必须整段单独读写（节点值是完整类型值），不能拆成逐寄存器节点。</para>
    /// </summary>
    private List<(int StartAt, int Count, VariableTableEntry? Variable)> SplitOperationBlock(
        ModbusArea areaValue, int startAddress, int countValue)
    {
        var blockList = new List<(int, int, VariableTableEntry?)>();
        var sectionStart = 0;
        var sectionLength = 0;
        var Offset = 0;

        while (Offset < countValue)
        {
            if (_resolver.TryMatchVariable(areaValue, startAddress + Offset, out var Variable, out var variableCount)
                && variableCount <= countValue - Offset)
            {
                if (sectionLength > 0)
                {
                    blockList.Add((sectionStart, sectionLength, null));
                    sectionLength = 0;
                }

                blockList.Add((startAddress + Offset, variableCount, Variable));
                Offset += variableCount;
            }
            else
            {
                if (sectionLength == 0)
                {
                    sectionStart = startAddress + Offset;
                }

                sectionLength++;
                Offset++;
            }
        }

        if (sectionLength > 0)
        {
            blockList.Add((sectionStart, sectionLength, null));
        }

        return blockList;
    }
}
