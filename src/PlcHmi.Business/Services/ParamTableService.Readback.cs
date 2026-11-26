using Microsoft.Extensions.Logging;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

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
public sealed partial class 参数表格服务
{
    /// <summary>回读间隔（与动作按钮同节奏：2s 一次，够快又不挤占串行 Modbus 链路）。</summary>
    public static readonly TimeSpan 回读间隔 = TimeSpan.FromSeconds(2);

    /// <summary>标识 → 回读到的延时值（秒）。</summary>
    private readonly Dictionary<string, double> _回读延时 = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>标识 → 回读到的按钮状态。</summary>
    private readonly Dictionary<string, bool> _回读按钮 = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>回读互斥：慢速串行链路上上次没读完时新的节拍又到了，在途即跳过本次。</summary>
    private int _回读在途;

    /// <inheritdoc />
    public event EventHandler<string>? 回读已变更;

    /// <inheritdoc />
    public double? 回读延时值(string 标识) =>
        _回读延时.TryGetValue(标识, out var 值) ? 值 : null;

    /// <inheritdoc />
    public bool? 回读按钮状态(string 标识) =>
        _回读按钮.TryGetValue(标识, out var 值) ? 值 : null;

    /// <inheritdoc />
    public async Task 异步回读(CancellationToken 取消令牌 = default)
    {
        // 在途即跳过：Modbus 是串行链路，上次回读还没做完时再发一轮只会互相挤占
        if (Interlocked.CompareExchange(ref _回读在途, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await 回读延时(取消令牌).ConfigureAwait(false);
            await 回读按钮(取消令牌).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _回读在途, 0);
        }
    }

    /// <summary>逐条回读延时值（秒 = DINT 毫秒 ÷ 1000）。</summary>
    private async Task 回读延时(CancellationToken 取消令牌)
    {
        foreach (var 条目 in _延迟)
        {
            if (!条目.启用 || string.IsNullOrWhiteSpace(条目.地址))
            {
                continue;
            }

            try
            {
                var 毫秒 = await _写入器
                    .异步读取寄存器地址32(条目.地址, 取消令牌)
                    .ConfigureAwait(false);

                var 秒 = 毫秒 / 1000.0;
                var 有变化 = !_回读延时.TryGetValue(条目.标识, out var 旧值) || Math.Abs(旧值 - 秒) > 1e-9;

                _回读延时[条目.标识] = 秒;

                if (有变化)
                {
                    回读已变更?.Invoke(this, 条目.标识);
                }
            }
            catch (Exception 异常)
            {
                // 单项容错：地址配错 / 无应答只跳过这一条，绝不让整轮回读失败
                _日志.LogDebug(异常, "回读延时「{Name}」({Address}) 失败，本轮跳过", 条目.名称, 条目.地址);
            }
        }
    }

    /// <summary>逐条回读按钮状态。</summary>
    private async Task 回读按钮(CancellationToken 取消令牌)
    {
        foreach (var 条目 in _按钮)
        {
            if (!条目.启用 || string.IsNullOrWhiteSpace(条目.地址))
            {
                continue;
            }

            try
            {
                var 状态 = await _写入器
                    .异步读取线圈地址(条目.地址, 取消令牌)
                    .ConfigureAwait(false);

                var 有变化 = !_回读按钮.TryGetValue(条目.标识, out var 旧值) || 旧值 != 状态;

                _回读按钮[条目.标识] = 状态;

                if (有变化)
                {
                    回读已变更?.Invoke(this, 条目.标识);
                }
            }
            catch (Exception 异常)
            {
                _日志.LogDebug(异常, "回读按钮「{Name}」({Address}) 失败，本轮跳过", 条目.名称, 条目.地址);
            }
        }
    }
}
