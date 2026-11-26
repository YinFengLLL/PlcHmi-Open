using System.Windows;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace PlcHmi.App;

/// <summary>
/// <see cref="App"/> 的「退出生命周期」部分（partial）。
///
/// <para><b>为什么拆出来：</b>App.xaml.cs 已接近 400 行规模门禁基线，
/// 退出收尾（停机 → 日志冲刷 → 互斥释放）是独立职责，单独成文件。</para>
/// </summary>
public partial class App
{
    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs eventArgs)
    {
        Log.Information("──── 工控上位机退出 ────");

        try
        {
            if (_host is not null)
            {
                // 退出路径刻意同步等待：async void 的 await 续接排在正在关闭的
                // Dispatcher 上，可能永不派发——日志收尾与单实例互斥释放会被跳过
                // （互斥有 OS 进程退出兜底，日志收尾与优雅停机没有）。
                // 关闭流程中允许同步阻塞，上限 3 秒。
                _host.StopAsync(TimeSpan.FromSeconds(3))
                    .Wait(TimeSpan.FromSeconds(3));
                _host.Dispose();
                _host = null;
            }
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "停止主机时发生异常");
        }

        Log.CloseAndFlush();

        // 释放单实例互斥：不释放的话，同一会话里下次启动会被自己挡住
        if (_singleInstanceLock is not null)
        {
            try
            {
                _singleInstanceLock.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 本次并未持有（诊断模式或获取失败），忽略
            }

            _singleInstanceLock.Dispose();
            _singleInstanceLock = null;
        }

        base.OnExit(eventArgs);
    }
}
