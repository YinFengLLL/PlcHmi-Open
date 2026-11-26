using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using 上位机.核心.抽象;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// 手动使能保持服务（现场口径 2026-10-15）。
///
/// <para><b>做什么：</b>周期把手动使能状态写进 PLC 的 <see cref="手动使能寄存器"/>：
/// 使能打开 → 持续置位 M666，使能关闭 → 持续复位 M666。</para>
///
/// <para><b>为什么需要周期重写：</b>只在开关切换时写一次的话，PLC 程序自己复位该位
/// （急停、模式切换、上电初始化）后，界面上仍显示"已使能"而 PLC 侧认为"未使能"——
/// 操作员按点动没反应却查不出原因。周期重写让 PLC 侧最终与界面一致。</para>
///
/// <para><b>失败不影响任何操作：</b>与页面上下文同步同口径——写不进去只记日志。
/// 手动使能是"允许手动"的握手信号，写失败绝不能让界面卡住；下个周期会自动重试。</para>
/// </summary>
public sealed class 手动使能保持服务 : IHostedService
{
    /// <summary>
    /// 重写周期。500ms 与报警扫描同量级：足够在 PLC 复位该位后"立刻"补上，
    /// 又不至于让这条握手信号占掉明显带宽。
    /// </summary>
    private static readonly TimeSpan 保持间隔 = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// 写失败后的退避上限。
    /// <para><b>为什么必须退避：</b>Modbus 单连接是<b>串行</b>的
    /// （<c>FluentModbusTCP客户端</c> 的全部读写都过同一把写入闸锁），
    /// 若这个线圈在 PLC 侧不响应、每次写都要等满读超时，那么每 500ms 撞一次
    /// 就等于把链路占满——连页面读取都排不上队，界面看起来就是"卡住"。
    /// 退避后最坏情况是每 10s 一次，链路始终留有余量。</para>
    /// </summary>
    private static readonly TimeSpan 失败退避上限 = TimeSpan.FromSeconds(10);

    private readonly 手动控件服务接口 _手动;
    private readonly ILogger<手动使能保持服务> _日志;

    private CancellationTokenSource? _取消令牌源;
    private Task? _循环;

    public 手动使能保持服务(手动控件服务接口 手动, ILogger<手动使能保持服务> 日志)
    {
        _手动 = 手动;
        _日志 = 日志;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken 取消令牌)
    {
        _取消令牌源 = CancellationTokenSource.CreateLinkedTokenSource(取消令牌);
        _循环 = Task.Run(() => 异步保持循环(_取消令牌源.Token), _取消令牌源.Token);

        _日志.LogInformation(
            "手动使能保持已启动：每 {Interval}ms 写 M{Coil}", 保持间隔.TotalMilliseconds, 手动使能寄存器.线圈);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken 取消令牌)
    {
        if (_取消令牌源 is null)
        {
            return;
        }

        await _取消令牌源.CancelAsync().ConfigureAwait(false);

        if (_循环 is not null)
        {
            try
            {
                // 带超时：循环若因 bug 卡住，不能让程序退出无限期挂在这里
                await _循环.WaitAsync(TimeSpan.FromSeconds(5), 取消令牌).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _日志.LogWarning("等待手动使能保持循环退出超时，已放弃等待");
            }
            catch (OperationCanceledException)
            {
                // 外部取消（程序退出），正常
            }
            catch (Exception 异常)
            {
                _日志.LogWarning(异常, "手动使能保持循环异常退出");
            }
        }

        _取消令牌源.Dispose();
        _取消令牌源 = null;

        _日志.LogInformation("手动使能保持已停止");
    }

    private async Task 异步保持循环(CancellationToken 取消令牌)
    {
        var 下次间隔 = 保持间隔;

        while (!取消令牌.IsCancellationRequested)
        {
            try
            {
                await _手动.异步同步启用(取消令牌).ConfigureAwait(false);
                下次间隔 = 保持间隔;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception 异常)
            {
                // 单次写失败不能打断循环：一次网络抖动不该让使能信号此后再也不写。
                // 但要退避：写不通时每 500ms 撞一次串行的 Modbus 闸锁会把链路占满。
                下次间隔 = TimeSpan.FromMilliseconds(
                    Math.Min(失败退避上限.TotalMilliseconds, 下次间隔.TotalMilliseconds * 2));

                if (下次间隔 <= 保持间隔 * 2)
                {
                    // 首次失败要看得见（现场排查"使能写不进去"全靠这条）
                    _日志.LogWarning(异常, "写入手动使能失败，退避 {Delay}ms 后重试", 下次间隔.TotalMilliseconds);
                }
                else
                {
                    // 持续失败只留痕：否则一次链路故障会把日志刷爆（保持间隔是 500ms）
                    _日志.LogDebug(异常, "写入手动使能仍失败，继续退避 {Delay}ms", 下次间隔.TotalMilliseconds);
                }
            }

            try
            {
                await Task.Delay(下次间隔, 取消令牌).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
