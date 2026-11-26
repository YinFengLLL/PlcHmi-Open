using PlcHmi.Core.Enums;

namespace PlcHmi.Core.Abstractions;

/// <summary>设备服务。封装启停复位急停命令，并跟踪节拍、产量与工位完成状态。</summary>
public interface IDeviceService
{
    /// <summary>当前设备状态。</summary>
    DeviceState State { get; }

    /// <summary>当前节拍（秒）。</summary>
    double WeekPeriodTimeSeconds { get; }

    /// <summary>当前产量。</summary>
    long OutputCount { get; }

    /// <summary>目标产量。</summary>
    /// <remarks>
    /// 启动时可由应用设置赋初值；PLC 目标产量点位（TargetCount）上报后自动覆盖。
    /// </remarks>
    long TargetCount { get; set; }

    /// <summary>良品数量。</summary>
    long GoodCount { get; }

    /// <summary>不良品数量。</summary>
    long DefectiveCount { get; }

    /// <summary>
    /// 锁附完成（点位 <c>ScrewDone</c>，默认 D1110）。
    /// <para><c>0</c> = 未完成，<c>1</c> = 完成；地址由管理员在运行监控页「编辑地址」里改。</para>
    /// </summary>
    bool ScrewComplete { get; }

    /// <summary>点胶完成（点位 <c>GlueDone</c>，默认 D1112）。<c>0</c> = 未完成，<c>1</c> = 完成。</summary>
    bool DispenseComplete { get; }

    /// <summary>加工完成（点位 <c>ProcessDone</c>，默认 D1114）。<c>0</c> = 未完成，<c>1</c> = 完成。</summary>
    bool ProcessComplete { get; }

    /// <summary>
    /// 已锁附颗数（点位 <c>ScrewCount</c>，默认 D1116，<b>INT</b> / 16 位有符号，占 1 字）。
    /// <para>与产量计数器相互独立——现场用 PLC 自己的锁付计数，不被"数据清除"影响。</para>
    /// </summary>
    long ScrewedPieceCount { get; }

    /// <summary>状态变化事件。</summary>
    event EventHandler<DeviceState>? StateChanged;

    /// <summary>节拍更新事件。</summary>
    event EventHandler<double>? weekPeriodTimeChanged;

    /// <summary>产量更新事件。</summary>
    event EventHandler<long>? outputCountChanged;

    /// <summary>目标产量更新事件（PLC 上报时推送）。</summary>
    event EventHandler<long>? targetCountChanged;

    /// <summary>良品数量更新事件。</summary>
    event EventHandler<long>? goodCountChanged;

    /// <summary>不良品数量更新事件。</summary>
    event EventHandler<long>? defectiveCountChanged;

    /// <summary>锁附完成状态更新事件。</summary>
    event EventHandler<bool>? screwStateChanged;

    /// <summary>点胶完成状态更新事件。</summary>
    event EventHandler<bool>? dispenseStateChanged;

    /// <summary>加工完成状态更新事件。</summary>
    event EventHandler<bool>? processStateChanged;

    /// <summary>已锁附颗数更新事件。</summary>
    event EventHandler<long>? screwedPieceCountChanged;

    /// <summary>启动设备。</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>停止设备。</summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>复位。</summary>
    Task ResetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 报警清除（向 PLC 下发清除命令）。
    ///
    /// <para><b>这里刻意没有急停命令</b>：急停必须由<b>物理按钮</b>实现——
    /// 上位机是软件，程序卡死或断线时按钮就失效了，不能作为安全回路的一环。
    /// 上位机只负责把 PLC 上报的急停状态（状态字 <c>4</c> = 报警中）显示出来。</para>
    /// </summary>
    Task ClearEmptyAlarmAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 数据清除（向 PLC 下发清除命令）。
    /// <para>复归型：脉冲写命令位，由 PLC 程序清零产量等累计数据。</para>
    /// </summary>
    Task ClearRemoveDataAsync(CancellationToken cancellationToken = default);
}
