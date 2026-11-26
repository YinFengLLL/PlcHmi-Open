using System.Windows;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace 上位机.应用;

/// <summary>
/// <see cref="应用"/> 的「退出生命周期」部分（partial）。
///
/// <para><b>为什么拆出来：</b>App.xaml.cs 已接近 400 行规模门禁基线，
/// 退出收尾（停机 → 日志冲刷 → 互斥释放）是独立职责，单独成文件。</para>
/// </summary>
public partial class 应用
{
    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs 事件参数)
    {
        Log.Information("──── 工控上位机退出 ────");

        try
        {
            if (_主机 is not null)
            {
                // 退出路径刻意同步等待：async void 的 await 续接排在正在关闭的
                // Dispatcher 上，可能永不派发——日志收尾与单实例互斥释放会被跳过
                // （互斥有 OS 进程退出兜底，日志收尾与优雅停机没有）。
                // 关闭流程中允许同步阻塞，上限 3 秒。
                _主机.StopAsync(TimeSpan.FromSeconds(3))
                    .Wait(TimeSpan.FromSeconds(3));
                _主机.Dispose();
                _主机 = null;
            }
        }
        catch (Exception 异常)
        {
            Log.Warning(异常, "停止主机时发生异常");
        }

        Log.CloseAndFlush();

        // 释放单实例互斥：不释放的话，同一会话里下次启动会被自己挡住
        if (_单实例锁 is not null)
        {
            try
            {
                _单实例锁.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 本次并未持有（诊断模式或获取失败），忽略
            }

            _单实例锁.Dispose();
            _单实例锁 = null;
        }

        base.OnExit(事件参数);
    }
}
