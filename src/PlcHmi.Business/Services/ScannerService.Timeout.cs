using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// <see cref="扫码枪服务"/> 的「触发型等码窗口与超时保护」部分（partial，2026-11-12）。
///
/// <para><b>为什么需要它：</b>触发型下 PLC 只知道"我要求扫了"，并不知道扫码枪有没有扫到。
/// 扫码枪没读出码（漏贴、脏污、对焦失败）时，如果没有超时收口，PLC 会一直等成功信号，
/// 工位就卡在那里。所以每次触发都开一个窗口：
/// 在 <see cref="扫码枪配置.超时毫秒"/> 内收到条码 → 写 <c>1</c>（成功）；
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
public sealed partial class 扫码枪服务
{
    private readonly object _等码锁 = new();

    /// <summary>等码窗口序号：每次开启 / 关闭都 +1，超时任务只在序号未变时收口。</summary>
    private int _等码序号;

    /// <summary>是否正处在一次触发后的等码窗口内（只有"有窗口"时收到的条码才算成功）。</summary>
    private bool _等码中;

    /// <summary>
    /// PLC 触发被检测到之后调用：开一个等码窗口，超时未收到条码则判失败。
    /// </summary>
    private void 打开等码窗口()
    {
        int 序号;

        lock (_等码锁)
        {
            // 上一次还没扫到就又来了新触发：按"新的这一件"处理，旧窗口直接作废（不写 2）
            序号 = ++_等码序号;
            _等码中 = true;
        }

        var 超时毫秒 = Math.Clamp(_配置.超时毫秒, 200, 60000);

        _ = Task.Run(() => 等码超时(序号, 超时毫秒));
    }

    /// <summary>关闭当前等码窗口（服务停止时调用；作废在飞行的超时任务）。</summary>
    private void 关闭等码窗口()
    {
        lock (_等码锁)
        {
            _等码中 = false;
            _等码序号++;
        }
    }

    /// <summary>
    /// 认领一次"扫码成功"：触发型必须处在等码窗口内才算数（并顺手关窗）。
    /// <para>手动型不看触发寄存器，任何条码都算。</para>
    /// </summary>
    private bool 认领扫码结果()
    {
        if (_配置.工作模式 != 扫码工作模式.触发型)
        {
            return true;
        }

        lock (_等码锁)
        {
            if (!_等码中)
            {
                return false;
            }

            _等码中 = false;
            _等码序号++;   // 让本次触发的超时任务作废
            return true;
        }
    }

    private async Task 等码超时(int 序号, int 超时毫秒)
    {
        await Task.Delay(超时毫秒).ConfigureAwait(false);

        lock (_等码锁)
        {
            if (!_等码中 || _等码序号 != 序号)
            {
                return;   // 已扫到码 / 已被新一轮触发取代 / 服务已停止
            }

            _等码中 = false;
            _等码序号++;
        }

        _日志.LogWarning(
            "触发型扫码超时：{Timeout} ms 内未收到条码，写失败信号 D{Address} = 2 并释放",
            超时毫秒, _成功寄存器);

        await 异步写信号(2, "扫码失败信号").ConfigureAwait(false);
        下发指令(_配置.释放指令, "释放");
    }

    /// <summary>下发一条 ASCII 指令（指令为空表示该扫码枪不需要，直接跳过）。</summary>
    private void 下发指令(string? 指令, string 用途)
    {
        var 文本 = 指令?.Trim() ?? string.Empty;

        if (文本.Length == 0)
        {
            return;
        }

        var 传输 = _传输;

        if (传输 is null || !传输.已打开)
        {
            _日志.LogWarning("触发型：扫码枪未连接，{Use}指令「{Command}」未下发", 用途, 文本);
            return;
        }

        try
        {
            if (传输.发送指令(文本))
            {
                _日志.LogInformation("触发型：已下发{Use}指令「{Command}」", 用途, 文本);
            }
            else
            {
                _日志.LogWarning("触发型：{Use}指令「{Command}」下发失败（通道写入被拒）", 用途, 文本);
            }
        }
        catch (InvalidOperationException 异常)
        {
            _日志.LogWarning(异常, "触发型：下发{Use}指令失败", 用途);
        }
        catch (IOException 异常)
        {
            _日志.LogWarning(异常, "触发型：下发{Use}指令失败", 用途);
        }
        catch (SocketException 异常)
        {
            _日志.LogWarning(异常, "触发型：下发{Use}指令失败", 用途);
        }
    }
}
