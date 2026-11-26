using Microsoft.Extensions.Logging;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// 参数表格服务的「PLC 回读」部分（partial）。
///
/// <para><b>为什么延时与按钮要回读：</b>它们的地址由管理员在界面上配置，不在 <c>tags.json</c> 里，
/// 因此不走轮询引擎。但 PLC 侧同样可能改写这些值——切换机种、HMI 旁的操作面板、断电前的保持状态——
/// 界面只显示本地配置值会与设备实际不符（现场反馈"改了的延时没生效"）。</para>
///
/// <para><b>为什么不自己起轮询循环：</b>与弹窗监控不同，这里只有参数页可见时才有意义；
/// 由 <c>主视图模型</c> 的 1s 时钟按页触发（见 <c>MainViewModel.Buttons.cs</c> 的
/// <c>异步刷新参数回读</c>），不新增常驻循环、不打扰其它页面。</para>
///
/// <para><b>逐项容错：</b>某一项读失败（地址写错、PLC 无应答）只跳过该项，
/// 不影响其它项，也不向上抛异常——回读是"让界面跟上 PLC"，不该把页面操作打断。</para>
/// </summary>
public sealed partial class ParameterTableService
{
    /// <summary>回读间隔（与动作按钮同节奏：2s 一次，够快又不挤占串行 Modbus 链路）。</summary>
    public static readonly TimeSpan readBackInterval = TimeSpan.FromSeconds(2);

    /// <summary>标识 → 回读到的延时值（秒）。</summary>
    private readonly Dictionary<string, double> _readBackDelay = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>标识 → 回读到的按钮状态。</summary>
    private readonly Dictionary<string, bool> _readBackButton = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>回读互斥：慢速串行链路上上次没读完时新的节拍又到了，在途即跳过本次。</summary>
    private int _readBackAtPurpose;

    /// <inheritdoc />
    public event EventHandler<string>? readBackChanged;

    /// <inheritdoc />
    public double? ReadBackDelayValue(string Id) =>
        _readBackDelay.TryGetValue(Id, out var Value) ? Value : null;

    /// <inheritdoc />
    public bool? ReadBackButtonState(string Id) =>
        _readBackButton.TryGetValue(Id, out var Value) ? Value : null;

    /// <inheritdoc />
    public async Task ReadBackAsync(CancellationToken cancellationToken = default)
    {
        // 在途即跳过：Modbus 是串行链路，上次回读还没做完时再发一轮只会互相挤占
        if (Interlocked.CompareExchange(ref _readBackAtPurpose, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await ReadBackDelay(cancellationToken).ConfigureAwait(false);
            await ReadBackButton(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _readBackAtPurpose, 0);
        }
    }

    /// <summary>逐条回读延时值（秒 = DINT 毫秒 ÷ 1000）。</summary>
    private async Task ReadBackDelay(CancellationToken cancellationToken)
    {
        foreach (var Entry in _delay)
        {
            if (!Entry.Enabled || string.IsNullOrWhiteSpace(Entry.Address))
            {
                continue;
            }

            try
            {
                var milliseconds = await _writer
                    .ReadRegisterAddress32Async(Entry.Address, cancellationToken)
                    .ConfigureAwait(false);

                var seconds = milliseconds / 1000.0;
                var hasChange = !_readBackDelay.TryGetValue(Entry.Id, out var oldValue) || Math.Abs(oldValue - seconds) > 1e-9;

                _readBackDelay[Entry.Id] = seconds;

                if (hasChange)
                {
                    readBackChanged?.Invoke(this, Entry.Id);
                }
            }
            catch (Exception exception)
            {
                // 单项容错：地址配错 / 无应答只跳过这一条，绝不让整轮回读失败
                _log.LogDebug(exception, "回读延时「{Name}」({Address}) 失败，本轮跳过", Entry.Name, Entry.Address);
            }
        }
    }

    /// <summary>逐条回读按钮状态。</summary>
    private async Task ReadBackButton(CancellationToken cancellationToken)
    {
        foreach (var Entry in _button)
        {
            if (!Entry.Enabled || string.IsNullOrWhiteSpace(Entry.Address))
            {
                continue;
            }

            try
            {
                var State = await _writer
                    .ReadCoilAddressAsync(Entry.Address, cancellationToken)
                    .ConfigureAwait(false);

                var hasChange = !_readBackButton.TryGetValue(Entry.Id, out var oldValue) || oldValue != State;

                _readBackButton[Entry.Id] = State;

                if (hasChange)
                {
                    readBackChanged?.Invoke(this, Entry.Id);
                }
            }
            catch (Exception exception)
            {
                _log.LogDebug(exception, "回读按钮「{Name}」({Address}) 失败，本轮跳过", Entry.Name, Entry.Address);
            }
        }
    }
}
