using Microsoft.Extensions.Logging;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// 连接管理器。负责连接生命周期、心跳保活与断线指数退避重连。
///
/// <para><b>为什么要自己做重连：</b>汇川 PLC 断线后不会自动恢复，
/// 必须由上位机主动关闭连接再重新建立，并做退避以避免把 PLC 打挂。</para>
/// </summary>
public sealed class ConnectionManager : IConnectManager, IAsyncDisposable
{
    private readonly IModbusClient _client;
    private readonly PlcConnectionOptions _options;
    private readonly ILogger<ConnectionManager> _log;

    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _workerThread;
    private volatile ConnectionState _stateValue = ConnectionState.Disconnected;

    public ConnectionManager(IModbusClient Client, PlcConnectionOptions Options, ILogger<ConnectionManager> Log)
    {
        _client = Client;
        _options = Options;
        _log = Log;
    }

    /// <inheritdoc />
    public ConnectionState State => _stateValue;

    /// <inheritdoc />
    public event EventHandler<ConnectionState>? StateChanged;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_workerThread is not null)
        {
            return Task.CompletedTask;
        }

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _workerThread = Task.Run(() => MaintainConnectAsync(_cancellationTokenSource.Token), CancellationToken.None);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_cancellationTokenSource is null || _workerThread is null)
        {
            return;
        }

        await _cancellationTokenSource.CancelAsync().ConfigureAwait(false);

        var waitTimeout = TimeSpan.FromSeconds(5);

        try
        {
            await _workerThread.WaitAsync(waitTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 5 秒内没退出 = 重连循环卡住的征兆，不是正常关闭，必须留下痕迹
            _log.LogWarning("等待连接管理器退出超时（{Seconds}s），已放弃等待", waitTimeout.TotalSeconds);
        }
        catch (OperationCanceledException)
        {
            // 外部取消（程序退出），正常
        }
        catch (Exception exception)
        {
            // 防御：任何残留异常都不该拖住程序退出
            _log.LogWarning(exception, "连接管理器异常退出");
        }
        finally
        {
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = null;
            _workerThread = null;
        }

        await SafetyDisconnectAsync().ConfigureAwait(false);
        SetState(ConnectionState.Disconnected);
    }

    /// <summary>连接维持循环：连接 → 心跳 → 断线后指数退避重连。</summary>
    private async Task MaintainConnectAsync(CancellationToken cancellationToken)
    {
        var delayValue = TimeSpan.FromMilliseconds(_options.ReconnectInitialDelayMilliseconds);
        var maxDelay = TimeSpan.FromMilliseconds(_options.ReconnectMaxDelayMilliseconds);

        // 显式记录"是否为本次断开后的第一次尝试"。
        // 原实现用 `delay == 初始值` 反推，有两个问题：
        //   1) 状态语义与退避参数耦合——改一个退避数字会连带改变状态机行为；
        //   2) 退避值恰好回到初始值时（例如把上限调小），判断会失真。
        var isFirstTry = true;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (_client.IsConnected)
            {
                isFirstTry = true;
                await HeartbeatAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            SetState(isFirstTry ? ConnectionState.Connecting : ConnectionState.Reconnecting);

            try
            {
                await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);

                SetState(ConnectionState.Connected);
                isFirstTry = true;
                delayValue = TimeSpan.FromMilliseconds(_options.ReconnectInitialDelayMilliseconds);   // 成功后重置退避
                continue;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _log.LogWarning("连接 PLC 失败（{Delay}ms 后重试）：{Message}",
                    delayValue.TotalMilliseconds, exception.Message);

                isFirstTry = false;

                // 不自动重连时直接进入 Faulted。
                // 原实现先 SetState(Reconnecting) 再 SetState(Faulted)，界面会无谓地闪一下。
                if (!_options.AutoReconnect)
                {
                    SetState(ConnectionState.Fault);
                    break;
                }

                SetState(ConnectionState.Reconnecting);
            }

            try
            {
                await Task.Delay(delayValue, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // 指数退避
            delayValue = TimeSpan.FromMilliseconds(Math.Min(delayValue.TotalMilliseconds * 2, maxDelay.TotalMilliseconds));
        }
    }

    /// <summary>心跳：无专用寄存器，读取一个必然存在的寄存器保活并探测链路。</summary>
    private async Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_options.HeartbeatIntervalMilliseconds, cancellationToken).ConfigureAwait(false);

            // 读 D0 探活：汇川 D 区自 0 起，该地址必然存在。
            // 只关心"请求能否成功往返"，不关心读回的值。
            await _client.ReadHoldingRegisterAsync(0, 1, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
        catch (Exception exception)
        {
            _log.LogWarning("心跳失败，判定为断线：{Message}", exception.Message);
            await SafetyDisconnectAsync().ConfigureAwait(false);

            // 此处不设状态：交给主循环按 isFirstAttempt 统一决定 Connecting / Reconnecting，
            // 避免"这里设一次、主循环再设一次"造成状态抖动。
        }
    }

    private async Task SafetyDisconnectAsync()
    {
        try
        {
            await _client.DisconnectAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _log.LogDebug(exception, "断开连接时出现异常，已忽略");
        }
    }

    private void SetState(ConnectionState StateValue)
    {
        if (_stateValue == StateValue)
        {
            return;
        }

        _stateValue = StateValue;
        _log.LogInformation("PLC 连接状态：{State}", StateValue);
        StateChanged?.Invoke(this, StateValue);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
