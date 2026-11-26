using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// 手动使能保持服务（现场口径 2026-10-15）。
///
/// <para><b>做什么：</b>周期把手动使能状态写进 PLC 的 <see cref="ManualEnableRegister"/>：
/// 使能打开 → 持续置位 M666，使能关闭 → 持续复位 M666。</para>
///
/// <para><b>为什么需要周期重写：</b>只在开关切换时写一次的话，PLC 程序自己复位该位
/// （急停、模式切换、上电初始化）后，界面上仍显示"已使能"而 PLC 侧认为"未使能"——
/// 操作员按点动没反应却查不出原因。周期重写让 PLC 侧最终与界面一致。</para>
///
/// <para><b>失败不影响任何操作：</b>与页面上下文同步同口径——写不进去只记日志。
/// 手动使能是"允许手动"的握手信号，写失败绝不能让界面卡住；下个周期会自动重试。</para>
/// </summary>
public sealed class ManualEnableHoldService : IHostedService
{
    /// <summary>
    /// 重写周期。500ms 与报警扫描同量级：足够在 PLC 复位该位后"立刻"补上，
    /// 又不至于让这条握手信号占掉明显带宽。
    /// </summary>
    private static readonly TimeSpan holdInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// 写失败后的退避上限。
    /// <para><b>为什么必须退避：</b>Modbus 单连接是<b>串行</b>的
    /// （<c>FluentModbusTCP客户端</c> 的全部读写都过同一把写入闸锁），
    /// 若这个线圈在 PLC 侧不响应、每次写都要等满读超时，那么每 500ms 撞一次
    /// 就等于把链路占满——连页面读取都排不上队，界面看起来就是"卡住"。
    /// 退避后最坏情况是每 10s 一次，链路始终留有余量。</para>
    /// </summary>
    private static readonly TimeSpan failedBackoffUpperLimit = TimeSpan.FromSeconds(10);

    private readonly IManualWidgetService _manual;
    private readonly ILogger<ManualEnableHoldService> _log;

    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _loopRing;

    public ManualEnableHoldService(IManualWidgetService Manual, ILogger<ManualEnableHoldService> Log)
    {
        _manual = Manual;
        _log = Log;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopRing = Task.Run(() => HoldLoopRingAsync(_cancellationTokenSource.Token), _cancellationTokenSource.Token);

        _log.LogInformation(
            "手动使能保持已启动：每 {Interval}ms 写 M{Coil}", holdInterval.TotalMilliseconds, ManualEnableRegister.Coil);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cancellationTokenSource is null)
        {
            return;
        }

        await _cancellationTokenSource.CancelAsync().ConfigureAwait(false);

        if (_loopRing is not null)
        {
            try
            {
                // 带超时：循环若因 bug 卡住，不能让程序退出无限期挂在这里
                await _loopRing.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _log.LogWarning("等待手动使能保持循环退出超时，已放弃等待");
            }
            catch (OperationCanceledException)
            {
                // 外部取消（程序退出），正常
            }
            catch (Exception exception)
            {
                _log.LogWarning(exception, "手动使能保持循环异常退出");
            }
        }

        _cancellationTokenSource.Dispose();
        _cancellationTokenSource = null;

        _log.LogInformation("手动使能保持已停止");
    }

    private async Task HoldLoopRingAsync(CancellationToken cancellationToken)
    {
        var nextInterval = holdInterval;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _manual.SyncEnabledAsync(cancellationToken).ConfigureAwait(false);
                nextInterval = holdInterval;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                // 单次写失败不能打断循环：一次网络抖动不该让使能信号此后再也不写。
                // 但要退避：写不通时每 500ms 撞一次串行的 Modbus 闸锁会把链路占满。
                nextInterval = TimeSpan.FromMilliseconds(
                    Math.Min(failedBackoffUpperLimit.TotalMilliseconds, nextInterval.TotalMilliseconds * 2));

                if (nextInterval <= holdInterval * 2)
                {
                    // 首次失败要看得见（现场排查"使能写不进去"全靠这条）
                    _log.LogWarning(exception, "写入手动使能失败，退避 {Delay}ms 后重试", nextInterval.TotalMilliseconds);
                }
                else
                {
                    // 持续失败只留痕：否则一次链路故障会把日志刷爆（保持间隔是 500ms）
                    _log.LogDebug(exception, "写入手动使能仍失败，继续退避 {Delay}ms", nextInterval.TotalMilliseconds);
                }
            }

            try
            {
                await Task.Delay(nextInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
