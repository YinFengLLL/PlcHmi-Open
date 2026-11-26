namespace 上位机.应用.服务;

/// <summary>
/// UI 线程调度统一入口。
///
/// <para>采集 / 连接 / 报警扫描都在后台线程推送事件，更新绑定属性前必须切回 UI 线程。
/// 本类是全站唯一的切线程入口：<see cref="视图模型基址"/> 与 <c>MainViewModel</c> 都走这里，
/// 避免各写一份导致行为不一致（曾出现过主窗口那份误用 <c>Invoke</c>）。</para>
/// </summary>
internal static class 界面派发
{
    /// <summary>
    /// 切到 UI 线程执行。
    ///
    /// <para><b>必须用 <c>BeginInvoke</c> 而不是 <c>Invoke</c></b>：<c>Invoke</c> 会同步阻塞后台线程
    /// 直到 UI 处理完。一旦 UI 线程同时在等待后台（例如被一个同步的 Modbus 调用占住），
    /// 双方互等就是<b>死锁</b>。<c>BeginInvoke</c> 只排队、不等人，后台线程立刻返回。</para>
    ///
    /// <para>代价是刷新任务会排队；因为数据源本身"值变化才推送"，队列长度有限，可接受。</para>
    /// </summary>
    public static void 投递(Action 动作)
    {
        var 调度器 = System.Windows.Application.Current?.Dispatcher;

        if (调度器 is null)
        {
            return;   // 应用已退出，丢弃这次刷新
        }

        if (调度器.CheckAccess())
        {
            动作();
            return;
        }

        // 关闭过程中排队会抛异常，直接丢弃
        if (调度器.HasShutdownStarted || 调度器.HasShutdownFinished)
        {
            return;
        }

        try
        {
            调度器.BeginInvoke(动作);
        }
        catch (TaskCanceledException)
        {
            // 关闭过程中排队被取消，忽略
        }
        catch (InvalidOperationException)
        {
            // Dispatcher 已不可用，忽略
        }
    }
}
