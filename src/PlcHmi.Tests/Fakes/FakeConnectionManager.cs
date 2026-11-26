using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;

namespace PlcHmi.Tests.Fake;

/// <summary>
/// 连接管理器的测试替身：只记录 Start / Stop 被调用的次数，不真的起循环。
///
/// <para><b>为什么需要它</b>：PLC 连接参数（IP / 端口）改完必须"断开重连"才能生效——
/// 通信客户端每次连接时才读共享参数。这条链路是连接参数管理的关键一环，
/// 用替身就能直接断言"保存后确实重启了连接"，而不用碰真实网络。</para>
/// </summary>
public sealed class FakeConnectManager : IConnectManager
{
    private int _startCount;
    private int _stopCount;
    private ConnectionState _stateValue = ConnectionState.Disconnected;

    /// <summary>StartAsync 被调用的次数。</summary>
    public int StartCount => _startCount;

    /// <summary>StopAsync 被调用的次数。</summary>
    public int StopCount => _stopCount;

    /// <summary>按计数推断是否处于"运行中"（Start 多于 Stop）。</summary>
    public bool IsRunning => _startCount > _stopCount;

    /// <summary>
    /// 连接状态。<b>赋值会真的触发 <see cref="状态值已变更"/></b> —— 替身必须忠实于契约。
    ///
    /// <para>此前这里是自动属性、事件从不触发，虽只是 CS0067 一个警告，但危害在于：
    /// 任何"监听状态变化"的测试用例都会<b>静默通过却什么也没验证</b>。
    /// 一个不会发事件的替身，比没有替身更危险。</para>
    /// </summary>
    public ConnectionState State
    {
        get => _stateValue;
        set
        {
            if (_stateValue == value)
            {
                return;
            }

            _stateValue = value;
            StateChanged?.Invoke(this, value);
        }
    }

    /// <inheritdoc />
    public event EventHandler<ConnectionState>? StateChanged;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _startCount);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _stopCount);
        return Task.CompletedTask;
    }
}
