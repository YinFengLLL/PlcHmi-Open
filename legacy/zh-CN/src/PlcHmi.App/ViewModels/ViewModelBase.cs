using CommunityToolkit.Mvvm.ComponentModel;
using 上位机.应用.服务;

namespace 上位机.应用.视图模型;

/// <summary>
/// 所有页面 ViewModel 的基类。
/// </summary>
public abstract partial class 视图模型基类 : ObservableObject
{
    /// <summary>页面标题，显示在内容区顶部。</summary>
    [ObservableProperty]
    private string _标题 = string.Empty;

    /// <summary>页面副标题/说明。</summary>
    [ObservableProperty]
    private string _副标题 = string.Empty;

    /// <summary>是否正在处理耗时操作（用于按钮禁用与加载提示）。</summary>
    [ObservableProperty]
    private bool _是否忙碌;

    /// <summary>页面被首次激活时调用，用于加载数据。</summary>
    public virtual Task 异步初始化() => Task.CompletedTask;

    /// <summary>
    /// 切换到 UI 线程执行。轮询在后台线程推送事件，更新绑定属性前必须切回 UI 线程。
    ///
    /// <para>实现已统一迁移到 <see cref="界面派发.投递"/>（全站唯一入口，仅用 <c>BeginInvoke</c>）。
    /// 保留本方法是为了不动 8 个页面 ViewModel 的调用点。</para>
    /// </summary>
    protected static void 在界面线程执行(Action 动作) => 界面派发.投递(动作);
}

/// <summary>
/// 「需要周期回读 PLC 状态的页面」（锁附示教 / 点胶示教 / 标定）。
///
/// <para><b>为什么需要这个接口（2026-10-15 现场反馈）：</b>切换型按钮的状态来自 PLC——
/// 上位机置位后，PLC 程序（或手动页、断电重启）可能把它复位。原先只有<b>重新进入页面</b>
/// 才回读一次，于是按钮会一直停在"已激活"，操作员必须离开再回来才看得到真实状态。
/// 由 <see cref="主视图模型"/> 对<b>当前页面</b>周期性触发一次回读，
/// 就能在不离开页面的情况下自动跟上 PLC。</para>
///
/// <para>只回读当前页：三页共用同一套按钮，但一次只看得到一页——给所有页都定时回读纯属浪费带宽
/// （Modbus 单连接是串行的，每多一次请求都是挤占轮询）。</para>
/// </summary>
public interface 动作按钮页接口
{
    /// <summary>从 PLC 回读各动作按钮的线圈状态（单项容错：读不到的按钮只跳过）。</summary>
    Task 异步刷新动作按钮状态();

    /// <summary>
    /// 回读本页的固定状态灯（各轴回原状态等）。默认无灯，什么都不做。
    /// <para><b>为什么与动作按钮分成两个方法：</b>两者节奏不同——回原状态要跟得紧（1s），
    /// 而按钮组一趟要读好几路线圈（2s 够用，也更不挤占轮询）。分开后各按各的节拍走。</para>
    /// </summary>
    Task 异步刷新状态灯() => Task.CompletedTask;

    /// <summary>
    /// 回读本页辅助地址的显示值（2026-11-18：辅助地址改为"实时显示 PLC 现值"）。
    /// <para><b>为什么不与动作按钮同一条任务：</b>辅助地址一页可能有十几行、每行一次读，
    /// 混进按钮回读会把"PLC 复位后按钮两秒内跟上"的时间预算吃掉——现场行为自检就卡在这条上。
    /// 单独一路、自己的节拍与在途守卫，谁也不拖累谁。</para>
    /// </summary>
    Task 异步刷新辅助地址状态() => Task.CompletedTask;

    /// <summary>
    /// 回读本页的实时显示项（锁附页的「当前浮高」）。默认没有，什么都不做。
    /// <para>与状态灯同节奏（1s）：都只是一次小读取，且操作员是盯着看的。</para>
    /// </summary>
    Task 异步刷新当前浮高() => Task.CompletedTask;
}

/// <summary>
/// 「需要周期回读 PLC 参数」的页面（参数设置页）。
///
/// <para><b>2026-10-17 现场要求：</b>参数页的延时表与按钮表绑定的 D/M 地址不在 <c>tags.json</c> 里
/// （地址由管理员在界面上配），因此不走轮询引擎；而这些值 PLC 侧同样可能改写
/// （切换机种、HMI 旁的操作面板、断电前的保持状态）。由 <see cref="主视图模型"/> 的 1s 时钟
/// 按 2s 间隔触发一次回读，让界面显示 PLC 当下真实值。</para>
///
/// <para>与 <see cref="动作按钮页接口"/> 分开：那类页面回读的是"按钮 / 状态灯"这类少量线圈，
/// 参数页回读的是"每页可能几十条"的数值与状态，节奏与语义都不同。</para>
/// </summary>
public interface 参数回读页接口
{
    /// <summary>回读本页的延时值与按钮状态（单项容错：读不到的项只跳过）。</summary>
    Task 异步刷新参数回读();
}
