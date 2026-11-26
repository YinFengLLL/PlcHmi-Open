using Opc.Ua;
using Opc.Ua.Client;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;
using PlcHmi.Communication.Modbus;

namespace PlcHmi.Communication.OpcUa;

/// <summary>
/// OPC UA 客户端的读写语义部分。
///
/// <para><b>语义翻译规则：</b></para>
/// <list type="bullet">
/// <item>线圈 / 离散输入（M、X、Y…位元件）→ Read/Write Bool 节点</item>
/// <item>保持寄存器 / 输入寄存器（D、R…字元件）→ Read/Write UInt16 节点</item>
/// <item><b>变量绑定的地址段优先</b>：整段匹配到变量表变量时读写变量名节点，
/// 多字变量（REAL/Int32 等）经寄存器编解码器做类型↔寄存器转换（红线 R1：字序处理只走编解码器）</item>
/// <item>其余连续地址合并为一次批量请求</item>
/// </list>
/// </summary>
public sealed partial class OpcUaClient
{
    // ── 读 ──────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<bool[]> ReadCoilAsync(int addressValue, int countValue, CancellationToken cancellationToken = default)
    {
        ValidateCount(countValue, maxBatchSectionPointCount, "读线圈");
        return ExecuteAsync(Session => ReadBitAreaRange(Session, ModbusArea.Coil, addressValue, countValue, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool[]> ReadDiscreteInputAsync(int addressValue, int countValue, CancellationToken cancellationToken = default)
    {
        ValidateCount(countValue, maxBatchSectionPointCount, "读离散输入");
        return ExecuteAsync(Session => ReadBitAreaRange(Session, ModbusArea.DiscreteInput, addressValue, countValue, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<ushort[]> ReadHoldingRegisterAsync(int addressValue, int countValue, CancellationToken cancellationToken = default)
    {
        ValidateCount(countValue, maxBatchSectionPointCount, "读保持寄存器");
        return ExecuteAsync(Session => ReadWordArea(Session, ModbusArea.HoldingRegister, addressValue, countValue, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<ushort[]> ReadInputRegisterAsync(int addressValue, int countValue, CancellationToken cancellationToken = default)
    {
        ValidateCount(countValue, maxBatchSectionPointCount, "读输入寄存器");
        return ExecuteAsync(Session => ReadWordArea(Session, ModbusArea.InputRegister, addressValue, countValue, cancellationToken), cancellationToken);
    }

    // ── 写 ──────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task WriteSingleCoilAsync(int addressValue, bool Value, CancellationToken cancellationToken = default) =>
        ExecuteAsync(Session => WriteBitArea(Session, ModbusArea.Coil, addressValue, [Value], cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task WriteSingleRegisterAsync(int addressValue, ushort Value, CancellationToken cancellationToken = default) =>
        ExecuteAsync(Session => WriteWordArea(Session, ModbusArea.HoldingRegister, addressValue, [Value], cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task WriteMultiCoilAsync(int addressValue, IReadOnlyList<bool> ValueList, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ValueList);
        ValidateCount(ValueList.Count, maxBatchSectionPointCount, "写多线圈");
        return ExecuteAsync(Session => WriteBitArea(Session, ModbusArea.Coil, addressValue, ValueList, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task WriteMultiRegisterAsync(int addressValue, IReadOnlyList<ushort> ValueList, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ValueList);
        ValidateCount(ValueList.Count, maxBatchSectionPointCount, "写多寄存器");
        return ExecuteAsync(Session => WriteWordArea(Session, ModbusArea.HoldingRegister, addressValue, ValueList, cancellationToken), cancellationToken);
    }

    // ── 执行骨架 ────────────────────────────────────────────────────────

    /// <summary>串行化执行一个读操作，并把 OPC UA 特有异常翻译为业务层通用异常。</summary>
    private async Task<T> ExecuteAsync<T>(Func<ISession, Task<T>> action, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var Session = EnsureConnected();
            return await action(Session).ConfigureAwait(false);
        }
        catch (ServiceResultException exception)
        {
            throw new InvalidOperationException($"OPC UA 操作失败：{exception.Message}", exception);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>串行化执行一个写操作（无返回值）。</summary>
    private async Task ExecuteAsync(Func<ISession, Task> action, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var Session = EnsureConnected();
            await action(Session).ConfigureAwait(false);
        }
        catch (ServiceResultException exception)
        {
            throw new InvalidOperationException($"OPC UA 操作失败：{exception.Message}", exception);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>读一段位区域：变量段读变量 Bool 节点，其余批量读后按原地址序拼回结果数组。</summary>
    private async Task<bool[]> ReadBitAreaRange(
        ISession Session, ModbusArea areaValue, int addressValue, int countValue, CancellationToken cancellationToken)
    {
        var resultValue = new bool[countValue];

        foreach (var block in SplitOperationBlock(areaValue, addressValue, countValue))
        {
            if (block.Variable is { } Variable)
            {
                var ValueList = await BatchRead(Session, [_resolver.ResolveVariableSectionPoint(Variable.VariableName)], cancellationToken)
                    .ConfigureAwait(false);

                for (var Entry = 0; Entry < block.Count; Entry++)
                {
                    resultValue[block.StartAt - addressValue + Entry] = Convert.ToBoolean(ValueList[0].Value);
                }

                continue;
            }

            var sectionPointList = BuildSectionPointList(areaValue, block.StartAt, block.Count);
            var batchValue = await BatchRead(Session, sectionPointList, cancellationToken).ConfigureAwait(false);

            for (var Entry = 0; Entry < block.Count; Entry++)
            {
                resultValue[block.StartAt - addressValue + Entry] = Convert.ToBoolean(batchValue[Entry].Value);
            }
        }

        return resultValue;
    }

    /// <summary>读一段字区域。变量段经寄存器编解码器做类型↔寄存器转换，其余节点逐寄存器归一为 UInt16。</summary>
    private async Task<ushort[]> ReadWordArea(
        ISession Session, ModbusArea areaValue, int addressValue, int countValue, CancellationToken cancellationToken)
    {
        var resultValue = new ushort[countValue];

        foreach (var block in SplitOperationBlock(areaValue, addressValue, countValue))
        {
            var targetOffset = block.StartAt - addressValue;

            if (block.Variable is { } Variable &&
                await TryReadVariableBlock(Session, Variable, block.Count, resultValue, targetOffset, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var sectionPointList = BuildSectionPointList(areaValue, block.StartAt, block.Count);
            var ValueList = await BatchRead(Session, sectionPointList, cancellationToken).ConfigureAwait(false);

            for (var Entry = 0; Entry < block.Count; Entry++)
            {
                resultValue[targetOffset + Entry] = Convert.ToUInt16(ValueList[Entry].Value);
            }
        }

        return resultValue;
    }

    private async Task WriteBitArea(
        ISession Session, ModbusArea areaValue, int addressValue, IReadOnlyList<bool> ValueList, CancellationToken cancellationToken)
    {
        foreach (var block in SplitOperationBlock(areaValue, addressValue, ValueList.Count))
        {
            var writeCollection = new WriteValueCollection();

            if (block.Variable is { } Variable)
            {
                // 位变量的首地址即该 Bool 节点：块内全部值只对首地址有效，取第一个值写入
                writeCollection.Add(new WriteValue
                {
                    NodeId = _resolver.ResolveVariableSectionPoint(Variable.VariableName),
                    AttributeId = Attributes.Value,
                    Value = new DataValue(new Variant(ValueList[block.StartAt - addressValue])),
                });

                await BatchWrite(Session, writeCollection, cancellationToken).ConfigureAwait(false);
                continue;
            }

            for (var Entry = 0; Entry < block.Count; Entry++)
            {
                var Offset = block.StartAt - addressValue + Entry;
                writeCollection.Add(new WriteValue
                {
                    NodeId = _resolver.Resolve(areaValue, block.StartAt + Entry),
                    AttributeId = Attributes.Value,
                    Value = new DataValue(new Variant(ValueList[Offset])),
                });
            }

            await BatchWrite(Session, writeCollection, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteWordArea(
        ISession Session, ModbusArea areaValue, int addressValue, IReadOnlyList<ushort> ValueList, CancellationToken cancellationToken)
    {
        foreach (var block in SplitOperationBlock(areaValue, addressValue, ValueList.Count))
        {
            var sourceOffset = block.StartAt - addressValue;

            if (block.Variable is { } Variable &&
                await TryWriteVariableBlock(Session, Variable, ValueList, sourceOffset, block.Count, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var writeCollection = new WriteValueCollection();

            for (var Entry = 0; Entry < block.Count; Entry++)
            {
                writeCollection.Add(new WriteValue
                {
                    NodeId = _resolver.Resolve(areaValue, block.StartAt + Entry),
                    AttributeId = Attributes.Value,
                    Value = new DataValue(new Variant(ValueList[sourceOffset + Entry])),
                });
            }

            await BatchWrite(Session, writeCollection, cancellationToken).ConfigureAwait(false);
        }
    }

    private List<NodeId> BuildSectionPointList(ModbusArea areaValue, int sectionStart, int sectionCount)
    {
        var sectionPointList = new List<NodeId>(sectionCount);

        for (var Entry = 0; Entry < sectionCount; Entry++)
        {
            sectionPointList.Add(_resolver.Resolve(areaValue, sectionStart + Entry));
        }

        return sectionPointList;
    }

    /// <summary>批量读一组节点，任一节点返回 Bad 状态码即抛出（调用方据此判定断线 / 地址无效）。</summary>
    private static async Task<DataValueCollection> BatchRead(
        ISession Session, IReadOnlyList<NodeId> sectionPointList, CancellationToken cancellationToken)
    {
        var requestCollection = new ReadValueIdCollection(
            sectionPointList.Select(sectionPoint => new ReadValueId { NodeId = sectionPoint, AttributeId = Attributes.Value }));

        // 取消令牌必须贯通到真正的网络 IO：否则停止轮询时在途请求
        // 要等满 OperationTimeout 才能结束（连接管理器停止超时的来源之一）
        var response = await Session.ReadAsync(null, 0, TimestampsToReturn.Neither, requestCollection, cancellationToken)
            .ConfigureAwait(false);

        foreach (var Diagnostic in response.DiagnosticInfos)
        {
            if (Diagnostic is not null)
            {
                throw new InvalidOperationException($"OPC UA 服务端返回诊断信息：{Diagnostic}");
            }
        }

        foreach (var Value in response.Results)
        {
            if (StatusCode.IsBad(Value.StatusCode))
            {
                throw new ServiceResultException(Value.StatusCode);
            }
        }

        return response.Results;
    }

    private static async Task BatchWrite(ISession Session, WriteValueCollection writeCollection, CancellationToken cancellationToken)
    {
        var response = await Session.WriteAsync(null, writeCollection, cancellationToken).ConfigureAwait(false);

        foreach (var Diagnostic in response.DiagnosticInfos)
        {
            if (Diagnostic is not null)
            {
                throw new InvalidOperationException($"OPC UA 服务端返回诊断信息：{Diagnostic}");
            }
        }

        foreach (var stateCode in response.Results)
        {
            if (StatusCode.IsBad(stateCode))
            {
                throw new ServiceResultException(stateCode);
            }
        }
    }

    private static void ValidateCount(int countValue, int maxCount, string operateOperation)
    {
        if (countValue <= 0 || countValue > maxCount)
        {
            throw new ArgumentOutOfRangeException(nameof(countValue), countValue,
                $"{operateOperation}：单次数量必须在 1-{maxCount} 之间。");
        }
    }
}
