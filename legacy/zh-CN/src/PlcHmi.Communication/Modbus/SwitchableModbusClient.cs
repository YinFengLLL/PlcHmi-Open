using 上位机.核心.抽象;

namespace 上位机.通信.Modbus;

/// <summary>
/// 可切换的 Modbus 客户端代理。对上层（连接管理器 / 轮询引擎）只是一个普通的
/// <see cref="Modbus客户端接口"/>，内部把请求转发给"当前生效"的具体实现。
///
/// <para><b>为什么需要它：</b>运行模式（真实 / 模拟）要在运行期切换，而连接管理器只注入一次客户端。
/// 有了这层间接，切换时只需替换内部实现，连接管理器无需重建、DI 无需重装。</para>
///
/// <para><b>线程安全：</b>切换与读取可能来自不同线程，故用一个轻量锁保护当前引用；
/// 每次调用先取快照再转发——切换瞬间仍在途的请求落到旧实现上，会因"未连接"而失败，
/// 由连接管理器按既有逻辑判为断线并重连，不会读到错位的值。</para>
///
/// <para><b>释放责任：</b>实现方（真实 / 仿真客户端）由 DI 容器持有并释放，
/// 代理自身<b>不释放</b>内部实现，避免重复释放。</para>
/// </summary>
public sealed class 可切换Modbus客户端 : Modbus客户端接口
{
    private readonly object _切换同步 = new();
    private Modbus客户端接口 _当前;

    public 可切换Modbus客户端(Modbus客户端接口 初始客户端)
    {
        ArgumentNullException.ThrowIfNull(初始客户端);
        _当前 = 初始客户端;
    }

    /// <summary>当前生效的实现（仅供诊断 / 测试断言）。</summary>
    public Modbus客户端接口 当前实现
    {
        get
        {
            lock (_切换同步)
            {
                return _当前;
            }
        }
    }

    /// <summary>替换当前实现。调用方应先停止连接，确保没有正在使用旧实现的活动请求。</summary>
    public void 切换实现(Modbus客户端接口 客户端)
    {
        ArgumentNullException.ThrowIfNull(客户端);

        lock (_切换同步)
        {
            _当前 = 客户端;
        }
    }

    /// <inheritdoc />
    public bool 是否已连接 => 当前实现.是否已连接;

    /// <inheritdoc />
    public Task 异步连接(CancellationToken 取消令牌 = default) => 当前实现.异步连接(取消令牌);

    /// <inheritdoc />
    public Task 异步断开() => 当前实现.异步断开();

    // ── 读 ──────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<bool[]> 异步读取线圈(int 地址值, int 数量值, CancellationToken 取消令牌 = default) =>
        当前实现.异步读取线圈(地址值, 数量值, 取消令牌);

    /// <inheritdoc />
    public Task<bool[]> 异步读取离散输入(int 地址值, int 数量值, CancellationToken 取消令牌 = default) =>
        当前实现.异步读取离散输入(地址值, 数量值, 取消令牌);

    /// <inheritdoc />
    public Task<ushort[]> 异步读取保持寄存器(int 地址值, int 数量值, CancellationToken 取消令牌 = default) =>
        当前实现.异步读取保持寄存器(地址值, 数量值, 取消令牌);

    /// <inheritdoc />
    public Task<ushort[]> 异步读取输入寄存器(int 地址值, int 数量值, CancellationToken 取消令牌 = default) =>
        当前实现.异步读取输入寄存器(地址值, 数量值, 取消令牌);

    // ── 写 ──────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task 异步写入单个线圈(int 地址值, bool 值, CancellationToken 取消令牌 = default) =>
        当前实现.异步写入单个线圈(地址值, 值, 取消令牌);

    /// <inheritdoc />
    public Task 异步写入单个寄存器(int 地址值, ushort 值, CancellationToken 取消令牌 = default) =>
        当前实现.异步写入单个寄存器(地址值, 值, 取消令牌);

    /// <inheritdoc />
    public Task 异步写入多个线圈(int 地址值, IReadOnlyList<bool> 值列表, CancellationToken 取消令牌 = default) =>
        当前实现.异步写入多个线圈(地址值, 值列表, 取消令牌);

    /// <inheritdoc />
    public Task 异步写入多个寄存器(int 地址值, IReadOnlyList<ushort> 值列表, CancellationToken 取消令牌 = default) =>
        当前实现.异步写入多个寄存器(地址值, 值列表, 取消令牌);

    /// <inheritdoc />
    /// <remarks>内部实现由 DI 容器释放，代理不代为释放，避免重复释放。</remarks>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
