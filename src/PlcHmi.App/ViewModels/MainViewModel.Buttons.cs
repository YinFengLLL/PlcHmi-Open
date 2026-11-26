using System.Threading;
using PlcHmi.App.Services;
using PlcHmi.Business.Services;
using PlcHmi.Core.Enums;

namespace PlcHmi.App.ViewModels;

/// <summary>
/// <see cref="MasterViewModel"/> 的「当前页 PLC 状态周期回读」部分（partial）。
///
/// <para><b>问题（2026-10-15 现场反馈）：</b>切换型动作按钮的状态来自 PLC。上位机置位后，
/// PLC 程序（或手动页、断电重启）把它复位时，界面上还停在"已激活"——
/// 原先只有重新进入页面才会回读，操作员必须"离开再回来"才看得到真实状态。</para>
///
/// <para><b>做法：</b>复用主界面已有的 1s 时钟，按各自的间隔给<b>当前页面</b>
/// 触发一次回读（页面实现 <see cref="IActionButtonPage"/>）。不新增定时器、不打扰其它页面。</para>
///
/// <para><b>两路节奏不同（2026-10-16 增补状态灯）：</b>回原状态灯只读一路线圈，要求跟得紧 → 1s；
/// 动作按钮一趟要读好几路线圈 → 2s 够用，也更不挤占串行的 Modbus 链路。</para>
///
/// <para><b>为什么按连接状态先拦一道：</b>未连接时每次回读都会逐个按钮抛异常并记一条警告，
/// 2 秒一次的节奏会把日志刷满；未连接本来也没有"PLC 侧复位"可同步。</para>
/// </summary>
public sealed partial class MasterViewModel
{
    /// <summary>
    /// 动作按钮回读间隔。2s 是"够快"与"别挤轮询"的折中：PLC 侧复位后两秒内界面就跟上，
    /// 而一页通常只有几个按钮（几次读线圈），不会把串行的 Modbus 链路占住。
    /// </summary>
    private static readonly TimeSpan actionReadBackInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 状态灯回读间隔。1s：回原状态是"能不能动"的前置条件，操作员盯着它看；
    /// 而它只有一路线圈读，1s 一次的代价可以忽略。
    /// </summary>
    private static readonly TimeSpan statusLampReadBackInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 辅助地址回读间隔。3s：这些值用于"看着 PLC 现状改一个数"，不需要秒级跟随；
    /// 一页最多十几行、每行一次读，放慢一点更不挤占串行的 Modbus 链路。
    /// </summary>
    private static readonly TimeSpan auxAddressReadBackInterval = TimeSpan.FromSeconds(3);

    private DateTime _lastActionReadBackTimestamp = DateTime.MinValue;
    private DateTime _lastStatusLampReadBackTimestamp = DateTime.MinValue;
    private DateTime _lastParameterReadBackTimestamp = DateTime.MinValue;
    private DateTime _lastAuxAddressReadBackTimestamp = DateTime.MinValue;

    /// <summary>
    /// 在途标志：慢速串行 Modbus 链路上，上次回读还没做完时新的节拍又到了，
    /// 两次读请求会互相挤占（源码注释自己也提醒过"挤占轮询"）。在途即跳过本次。
    /// </summary>
    private int _actionReadBackAtPurpose;

    /// <summary>状态灯回读的在途标志（与动作按钮各管各的节拍）。</summary>
    private int _statusLampReadBackAtPurpose;

    /// <summary>参数回读的在途标志（延时表 + 按钮表，可能几十项，更要防叠加）。</summary>
    private int _parameterReadBackAtPurpose;

    /// <summary>辅助地址回读的在途标志（一页十几行，独立一路，别拖慢动作按钮跟随）。</summary>
    private int _auxAddressReadBackAtPurpose;

    /// <summary>
    /// 时钟节拍调用（1s 一次）：到点且当前页支持时，触发状态灯、动作按钮与参数的回读。
    /// <para>即发即忘：回读失败只记日志，绝不能影响时钟与界面。</para>
    /// </summary>
    private void ReadBackCurrentPageStateAsync()
    {
        if (_connect.State is not ConnectionState.Connected)
        {
            return;   // 未连接：没有可同步的 PLC 状态，且会把日志刷满
        }

        var currentAt = DateTime.UtcNow;

        if (CurrentPage is IActionButtonPage pageItem)
        {
            ReadBackStatusLamp(pageItem, currentAt);
            ReadBackActionButton(pageItem, currentAt);
            ReadBackAuxAddress(pageItem, currentAt);
        }

        if (CurrentPage is IParameterReadBackPage parameterPage)
        {
            ReadBackParameter(parameterPage, currentAt);
        }
    }

    /// <summary>
    /// 参数页回读（延时值 + 按钮状态）：2s 一次。
    /// <para>与动作按钮同节奏：一页可能有几十条延时，每条一次 DINT 读；
    /// 2s 是"跟上 PLC"与"别把串行链路占住"的折中。在途即跳过本次。</para>
    /// </summary>
    private void ReadBackParameter(IParameterReadBackPage parameterPage, DateTime currentAt)
    {
        if (currentAt - _lastParameterReadBackTimestamp < ParameterTableService.readBackInterval
            || Interlocked.CompareExchange(ref _parameterReadBackAtPurpose, 1, 0) != 0)
        {
            return;
        }

        _lastParameterReadBackTimestamp = currentAt;

        SafeInvokeAsync.FireAndForget(async () =>
        {
            try
            {
                await parameterPage.RefreshParameterReadBackAsync().ConfigureAwait(true);
            }
            finally
            {
                Interlocked.Exchange(ref _parameterReadBackAtPurpose, 0);
            }
        }, _log, $"回读「{CurrentPage?.Title}」参数");
    }

    /// <summary>回原状态灯：1s 一次（未回原时灯要能立刻跟着闪起来）。</summary>
    private void ReadBackStatusLamp(IActionButtonPage pageItem, DateTime currentAt)
    {
        if (currentAt - _lastStatusLampReadBackTimestamp < statusLampReadBackInterval
            || Interlocked.CompareExchange(ref _statusLampReadBackAtPurpose, 1, 0) != 0)
        {
            return;
        }

        _lastStatusLampReadBackTimestamp = currentAt;

        SafeInvokeAsync.FireAndForget(async () =>
        {
            try
            {
                await pageItem.RefreshStatusLampAsync().ConfigureAwait(true);

                // 与状态灯同节拍：当前浮高（锁附页）也只是一次两字读取
                await pageItem.RefreshCurrentFloatHeightAsync().ConfigureAwait(true);
            }
            finally
            {
                Interlocked.Exchange(ref _statusLampReadBackAtPurpose, 0);
            }
        }, _log, $"回读「{CurrentPage?.Title}」状态灯");
    }

    /// <summary>辅助地址：3s 一次（一页十几行，放慢一点更不挤占串行链路）。</summary>
    private void ReadBackAuxAddress(IActionButtonPage pageItem, DateTime currentAt)
    {
        if (currentAt - _lastAuxAddressReadBackTimestamp < auxAddressReadBackInterval
            || Interlocked.CompareExchange(ref _auxAddressReadBackAtPurpose, 1, 0) != 0)
        {
            return;
        }

        _lastAuxAddressReadBackTimestamp = currentAt;

        SafeInvokeAsync.FireAndForget(async () =>
        {
            try
            {
                await pageItem.RefreshAuxAddressStateAsync().ConfigureAwait(true);
            }
            finally
            {
                Interlocked.Exchange(ref _auxAddressReadBackAtPurpose, 0);
            }
        }, _log, $"回读「{CurrentPage?.Title}」辅助地址");
    }

    /// <summary>切换型动作按钮：2s 一次（一组按钮要读好几路线圈，不宜更密）。</summary>
    private void ReadBackActionButton(IActionButtonPage pageItem, DateTime currentAt)
    {
        if (currentAt - _lastActionReadBackTimestamp < actionReadBackInterval
            || Interlocked.CompareExchange(ref _actionReadBackAtPurpose, 1, 0) != 0)
        {
            return;
        }

        _lastActionReadBackTimestamp = currentAt;

        // 经 异步安全调用 接管：失败留痕、不落成无人观察的 Task。
        // 在途标志在 finally 放行——读失败也要放行，否则一次超时就永久停止回读。
        SafeInvokeAsync.FireAndForget(async () =>
        {
            try
            {
                await pageItem.RefreshActionButtonStateAsync().ConfigureAwait(true);
            }
            finally
            {
                Interlocked.Exchange(ref _actionReadBackAtPurpose, 0);
            }
        }, _log, $"回读「{CurrentPage?.Title}」动作按钮状态");
    }
}
