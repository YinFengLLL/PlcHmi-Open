using 上位机.核心.枚举;

namespace 上位机.核心.抽象;

/// <summary>设备服务。封装启停复位急停命令，并跟踪节拍、产量与工位完成状态。</summary>
public interface 设备服务接口
{
    /// <summary>当前设备状态。</summary>
    设备状态 状态 { get; }

    /// <summary>当前节拍（秒）。</summary>
    double 周期时间秒 { get; }

    /// <summary>当前产量。</summary>
    long 输出数量 { get; }

    /// <summary>目标产量。</summary>
    /// <remarks>
    /// 启动时可由应用设置赋初值；PLC 目标产量点位（TargetCount）上报后自动覆盖。
    /// </remarks>
    long 目标数量 { get; set; }

    /// <summary>良品数量。</summary>
    long 良品数量 { get; }

    /// <summary>不良品数量。</summary>
    long 不良品数量 { get; }

    /// <summary>
    /// 锁附完成（点位 <c>ScrewDone</c>，默认 D1110）。
    /// <para><c>0</c> = 未完成，<c>1</c> = 完成；地址由管理员在运行监控页「编辑地址」里改。</para>
    /// </summary>
    bool 锁附完成 { get; }

    /// <summary>点胶完成（点位 <c>GlueDone</c>，默认 D1112）。<c>0</c> = 未完成，<c>1</c> = 完成。</summary>
    bool 点胶完成 { get; }

    /// <summary>加工完成（点位 <c>ProcessDone</c>，默认 D1114）。<c>0</c> = 未完成，<c>1</c> = 完成。</summary>
    bool 加工完成 { get; }

    /// <summary>
    /// 已锁附颗数（点位 <c>ScrewCount</c>，默认 D1116，<b>INT</b> / 16 位有符号，占 1 字）。
    /// <para>与产量计数器相互独立——现场用 PLC 自己的锁付计数，不被"数据清除"影响。</para>
    /// </summary>
    long 已锁附颗数 { get; }

    /// <summary>状态变化事件。</summary>
    event EventHandler<设备状态>? 状态已变更;

    /// <summary>节拍更新事件。</summary>
    event EventHandler<double>? 周期时间已变更;

    /// <summary>产量更新事件。</summary>
    event EventHandler<long>? 输出数量已变更;

    /// <summary>目标产量更新事件（PLC 上报时推送）。</summary>
    event EventHandler<long>? 目标数量已变更;

    /// <summary>良品数量更新事件。</summary>
    event EventHandler<long>? 良品数量已变更;

    /// <summary>不良品数量更新事件。</summary>
    event EventHandler<long>? 不良品数量已变更;

    /// <summary>锁附完成状态更新事件。</summary>
    event EventHandler<bool>? 锁附状态已变更;

    /// <summary>点胶完成状态更新事件。</summary>
    event EventHandler<bool>? 点胶状态已变更;

    /// <summary>加工完成状态更新事件。</summary>
    event EventHandler<bool>? 加工状态已变更;

    /// <summary>已锁附颗数更新事件。</summary>
    event EventHandler<long>? 已锁附颗数已变更;

    /// <summary>启动设备。</summary>
    Task 异步启动(CancellationToken 取消令牌 = default);

    /// <summary>停止设备。</summary>
    Task 异步停止(CancellationToken 取消令牌 = default);

    /// <summary>复位。</summary>
    Task 异步重置(CancellationToken 取消令牌 = default);

    /// <summary>
    /// 报警清除（向 PLC 下发清除命令）。
    ///
    /// <para><b>这里刻意没有急停命令</b>：急停必须由<b>物理按钮</b>实现——
    /// 上位机是软件，程序卡死或断线时按钮就失效了，不能作为安全回路的一环。
    /// 上位机只负责把 PLC 上报的急停状态（状态字 <c>4</c> = 报警中）显示出来。</para>
    /// </summary>
    Task 异步清空报警(CancellationToken 取消令牌 = default);

    /// <summary>
    /// 数据清除（向 PLC 下发清除命令）。
    /// <para>复归型：脉冲写命令位，由 PLC 程序清零产量等累计数据。</para>
    /// </summary>
    Task 异步清除数据(CancellationToken 取消令牌 = default);
}
