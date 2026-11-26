using Microsoft.Extensions.Logging;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// 连接管理器。负责连接生命周期、心跳保活与断线指数退避重连。
///
/// <para><b>为什么要自己做重连：</b>汇川 PLC 断线后不会自动恢复，
/// 必须由上位机主动关闭连接再重新建立，并做退避以避免把 PLC 打挂。</para>
/// </summary>
public sealed class 连接管理器 : 连接管理器接口, IAsyncDisposable
{
    private readonly Modbus客户端接口 _客户端;
    private readonly PLC连接选项 _选项;
    private readonly ILogger<连接管理器> _日志;

    private CancellationTokenSource? _取消令牌源;
    private Task? _工作线程;
    private volatile 连接状态 _状态值 = 连接状态.已断开;

    public 连接管理器(Modbus客户端接口 客户端, PLC连接选项 选项, ILogger<连接管理器> 日志)
    {
        _客户端 = 客户端;
        _选项 = 选项;
        _日志 = 日志;
    }

    /// <inheritdoc />
    public 连接状态 状态 => _状态值;

    /// <inheritdoc />
    public event EventHandler<连接状态>? 状态已变更;

    /// <inheritdoc />
    public Task 异步启动(CancellationToken 取消令牌 = default)
    {
        if (_工作线程 is not null)
        {
            return Task.CompletedTask;
        }

        _取消令牌源 = CancellationTokenSource.CreateLinkedTokenSource(取消令牌);
        _工作线程 = Task.Run(() => 异步维护连接(_取消令牌源.Token), CancellationToken.None);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task 异步停止(CancellationToken 取消令牌 = default)
    {
        if (_取消令牌源 is null || _工作线程 is null)
        {
            return;
        }

        await _取消令牌源.CancelAsync().ConfigureAwait(false);

        var 等待超时 = TimeSpan.FromSeconds(5);

        try
        {
            await _工作线程.WaitAsync(等待超时, 取消令牌).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 5 秒内没退出 = 重连循环卡住的征兆，不是正常关闭，必须留下痕迹
            _日志.LogWarning("等待连接管理器退出超时（{Seconds}s），已放弃等待", 等待超时.TotalSeconds);
        }
        catch (OperationCanceledException)
        {
            // 外部取消（程序退出），正常
        }
        catch (Exception 异常)
        {
            // 防御：任何残留异常都不该拖住程序退出
            _日志.LogWarning(异常, "连接管理器异常退出");
        }
        finally
        {
            _取消令牌源.Dispose();
            _取消令牌源 = null;
            _工作线程 = null;
        }

        await 异步安全断开().ConfigureAwait(false);
        设置状态(连接状态.已断开);
    }

    /// <summary>连接维持循环：连接 → 心跳 → 断线后指数退避重连。</summary>
    private async Task 异步维护连接(CancellationToken 取消令牌)
    {
        var 延迟值 = TimeSpan.FromMilliseconds(_选项.重连初始延迟毫秒);
        var 最大延迟 = TimeSpan.FromMilliseconds(_选项.重连最大延迟毫秒);

        // 显式记录"是否为本次断开后的第一次尝试"。
        // 原实现用 `delay == 初始值` 反推，有两个问题：
        //   1) 状态语义与退避参数耦合——改一个退避数字会连带改变状态机行为；
        //   2) 退避值恰好回到初始值时（例如把上限调小），判断会失真。
        var 是否首次尝试 = true;

        while (!取消令牌.IsCancellationRequested)
        {
            if (_客户端.是否已连接)
            {
                是否首次尝试 = true;
                await 异步心跳(取消令牌).ConfigureAwait(false);
                continue;
            }

            设置状态(是否首次尝试 ? 连接状态.连接中 : 连接状态.重连中);

            try
            {
                await _客户端.异步连接(取消令牌).ConfigureAwait(false);

                设置状态(连接状态.已连接);
                是否首次尝试 = true;
                延迟值 = TimeSpan.FromMilliseconds(_选项.重连初始延迟毫秒);   // 成功后重置退避
                continue;
            }
            catch (OperationCanceledException) when (取消令牌.IsCancellationRequested)
            {
                break;
            }
            catch (Exception 异常)
            {
                _日志.LogWarning("连接 PLC 失败（{Delay}ms 后重试）：{Message}",
                    延迟值.TotalMilliseconds, 异常.Message);

                是否首次尝试 = false;

                // 不自动重连时直接进入 Faulted。
                // 原实现先 SetState(Reconnecting) 再 SetState(Faulted)，界面会无谓地闪一下。
                if (!_选项.自动重连)
                {
                    设置状态(连接状态.故障);
                    break;
                }

                设置状态(连接状态.重连中);
            }

            try
            {
                await Task.Delay(延迟值, 取消令牌).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // 指数退避
            延迟值 = TimeSpan.FromMilliseconds(Math.Min(延迟值.TotalMilliseconds * 2, 最大延迟.TotalMilliseconds));
        }
    }

    /// <summary>心跳：无专用寄存器，读取一个必然存在的寄存器保活并探测链路。</summary>
    private async Task 异步心跳(CancellationToken 取消令牌)
    {
        try
        {
            await Task.Delay(_选项.心跳间隔毫秒, 取消令牌).ConfigureAwait(false);

            // 读 D0 探活：汇川 D 区自 0 起，该地址必然存在。
            // 只关心"请求能否成功往返"，不关心读回的值。
            await _客户端.异步读取保持寄存器(0, 1, 取消令牌).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
        catch (Exception 异常)
        {
            _日志.LogWarning("心跳失败，判定为断线：{Message}", 异常.Message);
            await 异步安全断开().ConfigureAwait(false);

            // 此处不设状态：交给主循环按 isFirstAttempt 统一决定 Connecting / Reconnecting，
            // 避免"这里设一次、主循环再设一次"造成状态抖动。
        }
    }

    private async Task 异步安全断开()
    {
        try
        {
            await _客户端.异步断开().ConfigureAwait(false);
        }
        catch (Exception 异常)
        {
            _日志.LogDebug(异常, "断开连接时出现异常，已忽略");
        }
    }

    private void 设置状态(连接状态 状态值)
    {
        if (_状态值 == 状态值)
        {
            return;
        }

        _状态值 = 状态值;
        _日志.LogInformation("PLC 连接状态：{State}", 状态值);
        状态已变更?.Invoke(this, 状态值);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await 异步停止().ConfigureAwait(false);
    }
}
