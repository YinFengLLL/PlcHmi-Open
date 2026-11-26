using 上位机.核心.抽象;
using 上位机.核心.枚举;

namespace 上位机.上位机测试.替身;

/// <summary>
/// 连接管理器的测试替身：只记录 Start / Stop 被调用的次数，不真的起循环。
///
/// <para><b>为什么需要它</b>：PLC 连接参数（IP / 端口）改完必须"断开重连"才能生效——
/// 通信客户端每次连接时才读共享参数。这条链路是连接参数管理的关键一环，
/// 用替身就能直接断言"保存后确实重启了连接"，而不用碰真实网络。</para>
/// </summary>
public sealed class 替身连接管理器 : 连接管理器接口
{
    private int _启动次数;
    private int _停止次数;
    private 连接状态 _状态值 = 连接状态.已断开;

    /// <summary>StartAsync 被调用的次数。</summary>
    public int 启动次数 => _启动次数;

    /// <summary>StopAsync 被调用的次数。</summary>
    public int 停止次数 => _停止次数;

    /// <summary>按计数推断是否处于"运行中"（Start 多于 Stop）。</summary>
    public bool 是否运行中 => _启动次数 > _停止次数;

    /// <summary>
    /// 连接状态。<b>赋值会真的触发 <see cref="状态值已变更"/></b> —— 替身必须忠实于契约。
    ///
    /// <para>此前这里是自动属性、事件从不触发，虽只是 CS0067 一个警告，但危害在于：
    /// 任何"监听状态变化"的测试用例都会<b>静默通过却什么也没验证</b>。
    /// 一个不会发事件的替身，比没有替身更危险。</para>
    /// </summary>
    public 连接状态 状态
    {
        get => _状态值;
        set
        {
            if (_状态值 == value)
            {
                return;
            }

            _状态值 = value;
            状态已变更?.Invoke(this, value);
        }
    }

    /// <inheritdoc />
    public event EventHandler<连接状态>? 状态已变更;

    /// <inheritdoc />
    public Task 异步启动(CancellationToken 取消令牌 = default)
    {
        Interlocked.Increment(ref _启动次数);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task 异步停止(CancellationToken 取消令牌 = default)
    {
        Interlocked.Increment(ref _停止次数);
        return Task.CompletedTask;
    }
}
