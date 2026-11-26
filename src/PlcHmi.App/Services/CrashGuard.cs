using System.IO;
using System.Net.Sockets;
using Serilog;

namespace PlcHmi.App.Services;

/// <summary>
/// 全局异常处理器安装器。
///
/// <para><b>设计要点（为什么单独一个类）：</b></para>
/// <list type="number">
/// <item><b>不弹模态框</b>：无人值守的机台上，模态框会阻断界面直到有人来点"确定"——
/// 结果是"报警还在响，界面已经不动了"。所以只记日志 + 顶栏常驻横幅。</item>
/// <item><b>区分"程序缺陷"与"现场常态"</b>：PLC 未连接 / 连接被中止 / 通信超时在车间里
/// 天天发生（设备没上电、网线没插、正在指数退避重连）。把它们和真正的代码缺陷混在一条红条里
/// 喊"程序异常"，操作员很快就会学会无视顶栏，真缺陷反而被噪声淹没。
/// 详见 <see cref="RuntimeErrorCategory"/>。</item>
/// </list>
///
/// <para>本类只安装处理器，不参与业务；<see cref="App.OnStartup"/> 里一行调用即可。</para>
/// </summary>
public static class ExceptionHandlerInstaller
{
    /// <summary>PLC 未连接时给操作员看的一句话（代替"程序异常"）。</summary>
    public const string disconnectedHint = "PLC 未连接，数据未刷新";

    /// <summary>安装三个全局异常处理器。</summary>
    /// <param name="errorList">运行期错误收集处（与界面横幅订阅的是同一个实例）。</param>
    public static void SafeInstall(RuntimeErrorService errorList)
    {
        ArgumentNullException.ThrowIfNull(errorList);

        System.Windows.Application.Current.DispatcherUnhandledException += (_, Parameter) =>
        {
            if (IsConnectCategoryException(Parameter.Exception))
            {
                // 界面线程上抛的连接类异常多半来自"点开页面时读 PLC"（PLC 未连接）
                Log.Warning(Parameter.Exception, "界面线程出现通信异常（按可预期状况处理）");
                errorList.Report(disconnectedHint, RuntimeErrorCategory.DeviceDisconnected);
            }
            else
            {
                Log.Error(Parameter.Exception, "界面线程未处理异常");
                errorList.Report(Description(Parameter.Exception));
            }

            Parameter.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, Parameter) =>
        {
            Log.Fatal(Parameter.ExceptionObject as Exception, "应用程序域未处理异常");

            // 进程即将终止：尽力让错误可见（界面可能已经没了）
            if (Parameter.ExceptionObject is Exception causeLife)
            {
                errorList.Report(Description(causeLife));
            }
        };

        TaskScheduler.UnobservedTaskException += (_, Parameter) =>
        {
            // 后台任务里"发出去没人接"的异常。.NET Core 默认静默吞掉，
            // 于是故障只表现为"某个功能一直不更新"，日志里什么都没有。
            if (IsConnectCategoryException(Parameter.Exception))
            {
                // 现场日志里最常见的一条就是它：FluentModbus 的 Connect 是同步阻塞调用，
                // 其内部 ConnectAsync 在超时或被 Dispose 时会把 Socket 异常留在无人观察的任务上，
                // 直到 GC 才浮出来。真实原因已由连接层记成「连接 PLC … 失败」，
                // 这里只留一句可读提示，不再冒充"程序异常"。
                Log.Warning("后台连接任务出现通信异常（已在连接层记录，按可预期状况处理）");
                errorList.Report(disconnectedHint, RuntimeErrorCategory.DeviceDisconnected);
                Parameter.SetObserved();
                return;
            }

            Log.Error(Parameter.Exception, "未观察到的后台任务异常");
            errorList.Report(Description(Parameter.Exception));
            Parameter.SetObserved();
        };
    }

    /// <summary>把异常压成一行可读文案（横幅只有一行位置）。</summary>
    public static string Description(Exception exception) =>
        $"{exception.GetType().Name}：{exception.Message}";

    /// <summary>
    /// 判断异常是否属于"可预期的现场状况"：PLC 未连接、连接被中止、通信超时。
    ///
    /// <para>判定依据按"最不易误判"排序：先看异常类型（Socket / 超时 / IO），
    /// 再退到文案匹配（"未连接"）——文案匹配只作为兜底，避免漏判。</para>
    /// </summary>
    public static bool IsConnectCategoryException(Exception? exception)
    {
        if (exception is null)
        {
            return false;
        }

        return exception switch
        {
            // 连接被拒绝 / 被中止 / 网络不可达
            SocketException => true,

            // 连接超时 / 读写超时
            TimeoutException => true,

            // 对端复位、网卡断开、Socket 被 Dispose 都会上抛 IO 异常
            IOException => true,

            // 后台任务的异常几乎总是被 AggregateException 包着（现场日志里就是这个形状）
            AggregateException aggregate => aggregate.Flatten().InnerExceptions.Any(IsConnectCategoryException),

            _ => exception.Message.Contains("未连接", StringComparison.Ordinal),
        };
    }
}
