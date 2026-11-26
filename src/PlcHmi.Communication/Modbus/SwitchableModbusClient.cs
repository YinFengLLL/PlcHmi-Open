using PlcHmi.Core.Abstractions;

namespace PlcHmi.Communication.Modbus;

/// <summary>
/// 可切换的 Modbus 客户端代理。对上层（连接管理器 / 轮询引擎）只是一个普通的
/// <see cref="IModbusClient"/>，内部把请求转发给"当前生效"的具体实现。
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
public sealed class SwitchableModbusClient : IModbusClient
{
    private readonly object _switchSync = new();
    private IModbusClient _current;

    public SwitchableModbusClient(IModbusClient initialClient)
    {
        ArgumentNullException.ThrowIfNull(initialClient);
        _current = initialClient;
    }

    /// <summary>当前生效的实现（仅供诊断 / 测试断言）。</summary>
    public IModbusClient CurrentRealCurrent
    {
        get
        {
            lock (_switchSync)
            {
                return _current;
            }
        }
    }

    /// <summary>替换当前实现。调用方应先停止连接，确保没有正在使用旧实现的活动请求。</summary>
    public void SwitchRealCurrent(IModbusClient Client)
    {
        ArgumentNullException.ThrowIfNull(Client);

        lock (_switchSync)
        {
            _current = Client;
        }
    }

    /// <inheritdoc />
    public bool IsConnected => CurrentRealCurrent.IsConnected;

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken cancellationToken = default) => CurrentRealCurrent.ConnectAsync(cancellationToken);

    /// <inheritdoc />
    public Task DisconnectAsync() => CurrentRealCurrent.DisconnectAsync();

    // ── 读 ──────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<bool[]> ReadCoilAsync(int addressValue, int countValue, CancellationToken cancellationToken = default) =>
        CurrentRealCurrent.ReadCoilAsync(addressValue, countValue, cancellationToken);

    /// <inheritdoc />
    public Task<bool[]> ReadDiscreteInputAsync(int addressValue, int countValue, CancellationToken cancellationToken = default) =>
        CurrentRealCurrent.ReadDiscreteInputAsync(addressValue, countValue, cancellationToken);

    /// <inheritdoc />
    public Task<ushort[]> ReadHoldingRegisterAsync(int addressValue, int countValue, CancellationToken cancellationToken = default) =>
        CurrentRealCurrent.ReadHoldingRegisterAsync(addressValue, countValue, cancellationToken);

    /// <inheritdoc />
    public Task<ushort[]> ReadInputRegisterAsync(int addressValue, int countValue, CancellationToken cancellationToken = default) =>
        CurrentRealCurrent.ReadInputRegisterAsync(addressValue, countValue, cancellationToken);

    // ── 写 ──────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task WriteSingleCoilAsync(int addressValue, bool Value, CancellationToken cancellationToken = default) =>
        CurrentRealCurrent.WriteSingleCoilAsync(addressValue, Value, cancellationToken);

    /// <inheritdoc />
    public Task WriteSingleRegisterAsync(int addressValue, ushort Value, CancellationToken cancellationToken = default) =>
        CurrentRealCurrent.WriteSingleRegisterAsync(addressValue, Value, cancellationToken);

    /// <inheritdoc />
    public Task WriteMultiCoilAsync(int addressValue, IReadOnlyList<bool> ValueList, CancellationToken cancellationToken = default) =>
        CurrentRealCurrent.WriteMultiCoilAsync(addressValue, ValueList, cancellationToken);

    /// <inheritdoc />
    public Task WriteMultiRegisterAsync(int addressValue, IReadOnlyList<ushort> ValueList, CancellationToken cancellationToken = default) =>
        CurrentRealCurrent.WriteMultiRegisterAsync(addressValue, ValueList, cancellationToken);

    /// <inheritdoc />
    /// <remarks>内部实现由 DI 容器释放，代理不代为释放，避免重复释放。</remarks>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
