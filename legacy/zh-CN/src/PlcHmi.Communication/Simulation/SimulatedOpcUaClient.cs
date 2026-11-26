using Microsoft.Extensions.Logging;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.通信.OPCUA;

namespace 上位机.通信.仿真;

/// <summary>
/// 模拟 OPC UA 客户端。与 <see cref="仿真Modbus客户端"/> 共用 <see cref="虚拟PLC设备"/> 的内存映像——
/// 同一份虚拟 PLC 数据，两条协议路径读写结果一致。
///
/// <para><b>与仿真 Modbus 客户端的差别</b>：每次读写都会先走一遍
/// <see cref="NodeId推导器"/> 推导——即 OPC UA 模式下真实客户端“区域+地址 → NodeId”的完整路径，
/// 映射规则写错（如八进制还原）在这里立刻暴露。取值仍直接映射虚拟内存，
/// 不真正起 OPC UA 服务端（测试无需引入网络栈）。</para>
/// </summary>
public sealed class 仿真OPCUA客户端 : Modbus客户端接口
{
    /// <summary>模拟的网络往返延迟，用于贴近真实响应时间。</summary>
    private static readonly TimeSpan 模拟时延 = TimeSpan.FromMilliseconds(3);

    private readonly 虚拟PLC设备 _设备;
    private readonly NodeId推导器 _推导器;
    private readonly ILogger<仿真OPCUA客户端> _日志;
    private bool _已连接;
    private bool _已释放;

    public 仿真OPCUA客户端(虚拟PLC设备 设备, NodeId推导器 推导器, ILogger<仿真OPCUA客户端> 日志)
    {
        _设备 = 设备;
        _推导器 = 推导器;
        _日志 = 日志;
    }

    /// <inheritdoc />
    public bool 是否已连接 => _已连接;

    /// <inheritdoc />
    public async Task 异步连接(CancellationToken 取消令牌 = default)
    {
        await Task.Delay(模拟时延, 取消令牌).ConfigureAwait(false);
        _已连接 = true;
        _设备.启动();
        _日志.LogInformation("已连接虚拟 PLC（OPC UA 模拟模式）");
    }

    /// <inheritdoc />
    public async Task 异步断开()
    {
        await Task.Delay(模拟时延).ConfigureAwait(false);
        _已连接 = false;
        await _设备.异步停止().ConfigureAwait(false);
        _日志.LogInformation("已断开虚拟 PLC");
    }

    // ── 读 ──────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<bool[]> 异步读取线圈(int 地址值, int 数量值, CancellationToken 取消令牌 = default)
    {
        await 异步确保就绪(取消令牌).ConfigureAwait(false);
        预推导节点(Modbus区域.线圈, 地址值, 数量值);
        return _设备.内存.读取线圈(地址值, 数量值);
    }

    /// <inheritdoc />
    public async Task<bool[]> 异步读取离散输入(int 地址值, int 数量值, CancellationToken 取消令牌 = default)
    {
        await 异步确保就绪(取消令牌).ConfigureAwait(false);
        预推导节点(Modbus区域.离散输入, 地址值, 数量值);
        return _设备.内存.读取离散输入(地址值, 数量值);
    }

    /// <inheritdoc />
    public async Task<ushort[]> 异步读取保持寄存器(int 地址值, int 数量值, CancellationToken 取消令牌 = default)
    {
        await 异步确保就绪(取消令牌).ConfigureAwait(false);
        预推导节点(Modbus区域.保持寄存器, 地址值, 数量值);
        return _设备.内存.读取保持寄存器(地址值, 数量值);
    }

    /// <inheritdoc />
    public async Task<ushort[]> 异步读取输入寄存器(int 地址值, int 数量值, CancellationToken 取消令牌 = default)
    {
        await 异步确保就绪(取消令牌).ConfigureAwait(false);
        预推导节点(Modbus区域.输入寄存器, 地址值, 数量值);
        return _设备.内存.读取输入寄存器(地址值, 数量值);
    }

    // ── 写 ──────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task 异步写入单个线圈(int 地址值, bool 值, CancellationToken 取消令牌 = default)
    {
        await 异步确保就绪(取消令牌).ConfigureAwait(false);
        预推导节点(Modbus区域.线圈, 地址值, 1);
        _设备.内存.写入线圈(地址值, 值);
    }

    /// <inheritdoc />
    public async Task 异步写入单个寄存器(int 地址值, ushort 值, CancellationToken 取消令牌 = default)
    {
        await 异步确保就绪(取消令牌).ConfigureAwait(false);
        预推导节点(Modbus区域.保持寄存器, 地址值, 1);
        _设备.内存.写入保持寄存器(地址值, 值);
    }

    /// <inheritdoc />
    public async Task 异步写入多个线圈(int 地址值, IReadOnlyList<bool> 值列表, CancellationToken 取消令牌 = default)
    {
        await 异步确保就绪(取消令牌).ConfigureAwait(false);
        预推导节点(Modbus区域.线圈, 地址值, 值列表.Count);
        _设备.内存.写入线圈项(地址值, 值列表);
    }

    /// <inheritdoc />
    public async Task 异步写入多个寄存器(int 地址值, IReadOnlyList<ushort> 值列表, CancellationToken 取消令牌 = default)
    {
        await 异步确保就绪(取消令牌).ConfigureAwait(false);
        预推导节点(Modbus区域.保持寄存器, 地址值, 值列表.Count);
        _设备.内存.写入保持寄存器项(地址值, 值列表);
    }

    /// <summary>
    /// 走一遍 NodeId 推导，验证本次操作涉及的全部地址都能映射到合法节点。
    /// <para>推导失败（地址不属于任何软元件区间）抛出与真实客户端一致的异常。</para>
    /// </summary>
    private void 预推导节点(Modbus区域 区域值, int 起始地址, int 数量值)
    {
        for (var 条目 = 0; 条目 < 数量值; 条目++)
        {
            _ = _推导器.推导(区域值, 起始地址 + 条目);
        }
    }

    private async Task 异步确保就绪(CancellationToken 取消令牌)
    {
        if (!_已连接)
        {
            throw new InvalidOperationException("虚拟 PLC 未连接，无法执行读写操作。");
        }

        await Task.Delay(模拟时延, 取消令牌).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_已释放)
        {
            return;
        }

        _已释放 = true;
        await 异步断开().ConfigureAwait(false);
    }
}
