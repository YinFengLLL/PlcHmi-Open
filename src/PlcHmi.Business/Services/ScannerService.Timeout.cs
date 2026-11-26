using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// <see cref="ScannerService"/> 的「触发型等码窗口与超时保护」部分（partial，2026-11-12）。
///
/// <para><b>为什么需要它：</b>触发型下 PLC 只知道"我要求扫了"，并不知道扫码枪有没有扫到。
/// 扫码枪没读出码（漏贴、脏污、对焦失败）时，如果没有超时收口，PLC 会一直等成功信号，
/// 工位就卡在那里。所以每次触发都开一个窗口：
/// 在 <see cref="ScannerConfig.TimeoutMilliseconds"/> 内收到条码 → 写 <c>1</c>（成功）；
/// 到点还没收到 → 写 <c>2</c>（失败）并下发释放指令，让这一件判失败、产线继续走。</para>
///
/// <para><b>晚到的码不算数：</b>窗口关闭后再收到的条码一律忽略——PLC 已经按"失败"处理了，
/// 此时再补一个 <c>1</c> 会让同一件既失败又成功。需要人工补扫时请重新触发。</para>
///
/// <para><b>为什么用序号而不是 CancellationTokenSource：</b>窗口的关闭方有三个
/// （扫到码 / 新触发取代 / 服务停止），而超时任务在飞行中。CTS 在"取消后立刻 Dispose"
/// 与"定时器仍在等"之间很容易踩到 ObjectDisposedException；序号方案只做一次
/// 锁内比较，作废旧窗口就是 <c>序号 + 1</c>，没有可释放的资源。</para>
/// </summary>
public sealed partial class ScannerService
{
    private readonly object _waitCodeLock = new();

    /// <summary>等码窗口序号：每次开启 / 关闭都 +1，超时任务只在序号未变时收口。</summary>
    private int _waitCodeIndex;

    /// <summary>是否正处在一次触发后的等码窗口内（只有"有窗口"时收到的条码才算成功）。</summary>
    private bool _waitCode;

    /// <summary>
    /// PLC 触发被检测到之后调用：开一个等码窗口，超时未收到条码则判失败。
    /// </summary>
    private void OpenWaitCodeWindow()
    {
        int Sequence;

        lock (_waitCodeLock)
        {
            // 上一次还没扫到就又来了新触发：按"新的这一件"处理，旧窗口直接作废（不写 2）
            Sequence = ++_waitCodeIndex;
            _waitCode = true;
        }

        var TimeoutMilliseconds = Math.Clamp(_config.TimeoutMilliseconds, 200, 60000);

        _ = Task.Run(() => WaitCodeTimeout(Sequence, TimeoutMilliseconds));
    }

    /// <summary>关闭当前等码窗口（服务停止时调用；作废在飞行的超时任务）。</summary>
    private void CloseWaitCodeWindow()
    {
        lock (_waitCodeLock)
        {
            _waitCode = false;
            _waitCodeIndex++;
        }
    }

    /// <summary>
    /// 认领一次"扫码成功"：触发型必须处在等码窗口内才算数（并顺手关窗）。
    /// <para>手动型不看触发寄存器，任何条码都算。</para>
    /// </summary>
    private bool ClaimScanResult()
    {
        if (_config.WorkMode != ScanWorkMode.TriggerKind)
        {
            return true;
        }

        lock (_waitCodeLock)
        {
            if (!_waitCode)
            {
                return false;
            }

            _waitCode = false;
            _waitCodeIndex++;   // 让本次触发的超时任务作废
            return true;
        }
    }

    private async Task WaitCodeTimeout(int Sequence, int TimeoutMilliseconds)
    {
        await Task.Delay(TimeoutMilliseconds).ConfigureAwait(false);

        lock (_waitCodeLock)
        {
            if (!_waitCode || _waitCodeIndex != Sequence)
            {
                return;   // 已扫到码 / 已被新一轮触发取代 / 服务已停止
            }

            _waitCode = false;
            _waitCodeIndex++;
        }

        _log.LogWarning(
            "触发型扫码超时：{Timeout} ms 内未收到条码，写失败信号 D{Address} = 2 并释放",
            TimeoutMilliseconds, _successRegister);

        await WriteSignalAsync(2, "扫码失败信号").ConfigureAwait(false);
        SendCommand(_config.ReleaseCommand, "释放");
    }

    /// <summary>下发一条 ASCII 指令（指令为空表示该扫码枪不需要，直接跳过）。</summary>
    private void SendCommand(string? command, string usePurpose)
    {
        var Text = command?.Trim() ?? string.Empty;

        if (Text.Length == 0)
        {
            return;
        }

        var Transport = _transport;

        if (Transport is null || !Transport.Opened)
        {
            _log.LogWarning("触发型：扫码枪未连接，{Use}指令「{Command}」未下发", usePurpose, Text);
            return;
        }

        try
        {
            if (Transport.SenderCommand(Text))
            {
                _log.LogInformation("触发型：已下发{Use}指令「{Command}」", usePurpose, Text);
            }
            else
            {
                _log.LogWarning("触发型：{Use}指令「{Command}」下发失败（通道写入被拒）", usePurpose, Text);
            }
        }
        catch (InvalidOperationException exception)
        {
            _log.LogWarning(exception, "触发型：下发{Use}指令失败", usePurpose);
        }
        catch (IOException exception)
        {
            _log.LogWarning(exception, "触发型：下发{Use}指令失败", usePurpose);
        }
        catch (SocketException exception)
        {
            _log.LogWarning(exception, "触发型：下发{Use}指令失败", usePurpose);
        }
    }
}
