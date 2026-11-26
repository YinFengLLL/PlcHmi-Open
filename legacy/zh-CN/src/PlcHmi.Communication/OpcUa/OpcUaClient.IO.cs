using Opc.Ua;
using Opc.Ua.Client;
using 上位机.核心.枚举;
using 上位机.核心.模型;
using 上位机.通信.Modbus;

namespace 上位机.通信.OPCUA;

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
public sealed partial class OPCUA客户端
{
    // ── 读 ──────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<bool[]> 异步读取线圈(int 地址值, int 数量值, CancellationToken 取消令牌 = default)
    {
        校验数量(数量值, 最大批量节点数, "读线圈");
        return 异步执行(会话 => 读位区域(会话, Modbus区域.线圈, 地址值, 数量值, 取消令牌), 取消令牌);
    }

    /// <inheritdoc />
    public Task<bool[]> 异步读取离散输入(int 地址值, int 数量值, CancellationToken 取消令牌 = default)
    {
        校验数量(数量值, 最大批量节点数, "读离散输入");
        return 异步执行(会话 => 读位区域(会话, Modbus区域.离散输入, 地址值, 数量值, 取消令牌), 取消令牌);
    }

    /// <inheritdoc />
    public Task<ushort[]> 异步读取保持寄存器(int 地址值, int 数量值, CancellationToken 取消令牌 = default)
    {
        校验数量(数量值, 最大批量节点数, "读保持寄存器");
        return 异步执行(会话 => 读字区域(会话, Modbus区域.保持寄存器, 地址值, 数量值, 取消令牌), 取消令牌);
    }

    /// <inheritdoc />
    public Task<ushort[]> 异步读取输入寄存器(int 地址值, int 数量值, CancellationToken 取消令牌 = default)
    {
        校验数量(数量值, 最大批量节点数, "读输入寄存器");
        return 异步执行(会话 => 读字区域(会话, Modbus区域.输入寄存器, 地址值, 数量值, 取消令牌), 取消令牌);
    }

    // ── 写 ──────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task 异步写入单个线圈(int 地址值, bool 值, CancellationToken 取消令牌 = default) =>
        异步执行(会话 => 写位区域(会话, Modbus区域.线圈, 地址值, [值], 取消令牌), 取消令牌);

    /// <inheritdoc />
    public Task 异步写入单个寄存器(int 地址值, ushort 值, CancellationToken 取消令牌 = default) =>
        异步执行(会话 => 写字区域(会话, Modbus区域.保持寄存器, 地址值, [值], 取消令牌), 取消令牌);

    /// <inheritdoc />
    public Task 异步写入多个线圈(int 地址值, IReadOnlyList<bool> 值列表, CancellationToken 取消令牌 = default)
    {
        ArgumentNullException.ThrowIfNull(值列表);
        校验数量(值列表.Count, 最大批量节点数, "写多线圈");
        return 异步执行(会话 => 写位区域(会话, Modbus区域.线圈, 地址值, 值列表, 取消令牌), 取消令牌);
    }

    /// <inheritdoc />
    public Task 异步写入多个寄存器(int 地址值, IReadOnlyList<ushort> 值列表, CancellationToken 取消令牌 = default)
    {
        ArgumentNullException.ThrowIfNull(值列表);
        校验数量(值列表.Count, 最大批量节点数, "写多寄存器");
        return 异步执行(会话 => 写字区域(会话, Modbus区域.保持寄存器, 地址值, 值列表, 取消令牌), 取消令牌);
    }

    // ── 执行骨架 ────────────────────────────────────────────────────────

    /// <summary>串行化执行一个读操作，并把 OPC UA 特有异常翻译为业务层通用异常。</summary>
    private async Task<T> 异步执行<T>(Func<ISession, Task<T>> 动作, CancellationToken 取消令牌)
    {
        await _写入闸锁.WaitAsync(取消令牌).ConfigureAwait(false);
        try
        {
            var 会话 = 确保已连接();
            return await 动作(会话).ConfigureAwait(false);
        }
        catch (ServiceResultException 异常)
        {
            throw new InvalidOperationException($"OPC UA 操作失败：{异常.Message}", 异常);
        }
        finally
        {
            _写入闸锁.Release();
        }
    }

    /// <summary>串行化执行一个写操作（无返回值）。</summary>
    private async Task 异步执行(Func<ISession, Task> 动作, CancellationToken 取消令牌)
    {
        await _写入闸锁.WaitAsync(取消令牌).ConfigureAwait(false);
        try
        {
            var 会话 = 确保已连接();
            await 动作(会话).ConfigureAwait(false);
        }
        catch (ServiceResultException 异常)
        {
            throw new InvalidOperationException($"OPC UA 操作失败：{异常.Message}", 异常);
        }
        finally
        {
            _写入闸锁.Release();
        }
    }

    /// <summary>读一段位区域：变量段读变量 Bool 节点，其余批量读后按原地址序拼回结果数组。</summary>
    private async Task<bool[]> 读位区域(
        ISession 会话, Modbus区域 区域值, int 地址值, int 数量值, CancellationToken 取消令牌)
    {
        var 结果值 = new bool[数量值];

        foreach (var 块 in 切分操作块(区域值, 地址值, 数量值))
        {
            if (块.变量 is { } 变量)
            {
                var 值列表 = await 批量读(会话, [_推导器.推导变量节点(变量.变量名)], 取消令牌)
                    .ConfigureAwait(false);

                for (var 条目 = 0; 条目 < 块.数量; 条目++)
                {
                    结果值[块.起始 - 地址值 + 条目] = Convert.ToBoolean(值列表[0].Value);
                }

                continue;
            }

            var 节点列表 = 构建节点列表(区域值, 块.起始, 块.数量);
            var 批量值 = await 批量读(会话, 节点列表, 取消令牌).ConfigureAwait(false);

            for (var 条目 = 0; 条目 < 块.数量; 条目++)
            {
                结果值[块.起始 - 地址值 + 条目] = Convert.ToBoolean(批量值[条目].Value);
            }
        }

        return 结果值;
    }

    /// <summary>读一段字区域。变量段经寄存器编解码器做类型↔寄存器转换，其余节点逐寄存器归一为 UInt16。</summary>
    private async Task<ushort[]> 读字区域(
        ISession 会话, Modbus区域 区域值, int 地址值, int 数量值, CancellationToken 取消令牌)
    {
        var 结果值 = new ushort[数量值];

        foreach (var 块 in 切分操作块(区域值, 地址值, 数量值))
        {
            var 目标偏移 = 块.起始 - 地址值;

            if (块.变量 is { } 变量 &&
                await 尝试读变量块(会话, 变量, 块.数量, 结果值, 目标偏移, 取消令牌).ConfigureAwait(false))
            {
                continue;
            }

            var 节点列表 = 构建节点列表(区域值, 块.起始, 块.数量);
            var 值列表 = await 批量读(会话, 节点列表, 取消令牌).ConfigureAwait(false);

            for (var 条目 = 0; 条目 < 块.数量; 条目++)
            {
                结果值[目标偏移 + 条目] = Convert.ToUInt16(值列表[条目].Value);
            }
        }

        return 结果值;
    }

    private async Task 写位区域(
        ISession 会话, Modbus区域 区域值, int 地址值, IReadOnlyList<bool> 值列表, CancellationToken 取消令牌)
    {
        foreach (var 块 in 切分操作块(区域值, 地址值, 值列表.Count))
        {
            var 写入集合 = new WriteValueCollection();

            if (块.变量 is { } 变量)
            {
                // 位变量的首地址即该 Bool 节点：块内全部值只对首地址有效，取第一个值写入
                写入集合.Add(new WriteValue
                {
                    NodeId = _推导器.推导变量节点(变量.变量名),
                    AttributeId = Attributes.Value,
                    Value = new DataValue(new Variant(值列表[块.起始 - 地址值])),
                });

                await 批量写(会话, 写入集合, 取消令牌).ConfigureAwait(false);
                continue;
            }

            for (var 条目 = 0; 条目 < 块.数量; 条目++)
            {
                var 偏移 = 块.起始 - 地址值 + 条目;
                写入集合.Add(new WriteValue
                {
                    NodeId = _推导器.推导(区域值, 块.起始 + 条目),
                    AttributeId = Attributes.Value,
                    Value = new DataValue(new Variant(值列表[偏移])),
                });
            }

            await 批量写(会话, 写入集合, 取消令牌).ConfigureAwait(false);
        }
    }

    private async Task 写字区域(
        ISession 会话, Modbus区域 区域值, int 地址值, IReadOnlyList<ushort> 值列表, CancellationToken 取消令牌)
    {
        foreach (var 块 in 切分操作块(区域值, 地址值, 值列表.Count))
        {
            var 源偏移 = 块.起始 - 地址值;

            if (块.变量 is { } 变量 &&
                await 尝试写变量块(会话, 变量, 值列表, 源偏移, 块.数量, 取消令牌).ConfigureAwait(false))
            {
                continue;
            }

            var 写入集合 = new WriteValueCollection();

            for (var 条目 = 0; 条目 < 块.数量; 条目++)
            {
                写入集合.Add(new WriteValue
                {
                    NodeId = _推导器.推导(区域值, 块.起始 + 条目),
                    AttributeId = Attributes.Value,
                    Value = new DataValue(new Variant(值列表[源偏移 + 条目])),
                });
            }

            await 批量写(会话, 写入集合, 取消令牌).ConfigureAwait(false);
        }
    }

    private List<NodeId> 构建节点列表(Modbus区域 区域值, int 段起始, int 段数量)
    {
        var 节点列表 = new List<NodeId>(段数量);

        for (var 条目 = 0; 条目 < 段数量; 条目++)
        {
            节点列表.Add(_推导器.推导(区域值, 段起始 + 条目));
        }

        return 节点列表;
    }

    /// <summary>批量读一组节点，任一节点返回 Bad 状态码即抛出（调用方据此判定断线 / 地址无效）。</summary>
    private static async Task<DataValueCollection> 批量读(
        ISession 会话, IReadOnlyList<NodeId> 节点列表, CancellationToken 取消令牌)
    {
        var 请求集合 = new ReadValueIdCollection(
            节点列表.Select(节点 => new ReadValueId { NodeId = 节点, AttributeId = Attributes.Value }));

        // 取消令牌必须贯通到真正的网络 IO：否则停止轮询时在途请求
        // 要等满 OperationTimeout 才能结束（连接管理器停止超时的来源之一）
        var 响应 = await 会话.ReadAsync(null, 0, TimestampsToReturn.Neither, 请求集合, 取消令牌)
            .ConfigureAwait(false);

        foreach (var 诊断 in 响应.DiagnosticInfos)
        {
            if (诊断 is not null)
            {
                throw new InvalidOperationException($"OPC UA 服务端返回诊断信息：{诊断}");
            }
        }

        foreach (var 值 in 响应.Results)
        {
            if (StatusCode.IsBad(值.StatusCode))
            {
                throw new ServiceResultException(值.StatusCode);
            }
        }

        return 响应.Results;
    }

    private static async Task 批量写(ISession 会话, WriteValueCollection 写入集合, CancellationToken 取消令牌)
    {
        var 响应 = await 会话.WriteAsync(null, 写入集合, 取消令牌).ConfigureAwait(false);

        foreach (var 诊断 in 响应.DiagnosticInfos)
        {
            if (诊断 is not null)
            {
                throw new InvalidOperationException($"OPC UA 服务端返回诊断信息：{诊断}");
            }
        }

        foreach (var 状态码 in 响应.Results)
        {
            if (StatusCode.IsBad(状态码))
            {
                throw new ServiceResultException(状态码);
            }
        }
    }

    private static void 校验数量(int 数量值, int 最大数量, string 操作)
    {
        if (数量值 <= 0 || 数量值 > 最大数量)
        {
            throw new ArgumentOutOfRangeException(nameof(数量值), 数量值,
                $"{操作}：单次数量必须在 1-{最大数量} 之间。");
        }
    }
}
