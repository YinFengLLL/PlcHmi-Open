using Microsoft.Extensions.Logging;

namespace PlcHmi.App.Services;

/// <summary>
/// 安全的"即发即忘"调用。
///
/// <para>属性 setter 与无返回命令里不能 <c>await</c>，于是常见写法是 <c>_ = XxxAsync();</c>。
/// 直接丢弃 Task 会让异常一起消失——任务失败时既无提示、日志也无痕迹，现场表现为
/// "点了没反应，还查不出原因"。</para>
///
/// <para><b>注意：</b>把 <c>_ = XxxAsync();</c> 包进 <c>try/catch</c> 是无效的——
/// <c>try</c> 只能捕获调用瞬间同步抛出的异常；一旦方法内部已经 <c>await</c> 过，
/// 之后的失败会落在 Task 上，不会被这里捕获。所以必须由本类接管。</para>
///
/// <para>本类不改变原有的非阻塞语义：调用方立即返回，操作在后台继续，失败必定被记录。</para>
/// </summary>
internal static class SafeInvokeAsync
{
    /// <summary>
    /// 执行一个异步操作，失败时记录日志而不是静默丢弃。
    /// </summary>
    /// <param name="operation">要执行的异步操作。</param>
    /// <param name="logger">用于记录异常的日志器。</param>
    /// <param name="context">出错时用于定位的场景描述（例如"同步手动使能状态到 PLC"）。</param>
    public static void FireAndForget(Func<Task> operateOperation, ILogger Log, string context)
    {
        ArgumentNullException.ThrowIfNull(operateOperation);
        ArgumentNullException.ThrowIfNull(Log);

        _ = RunAsync(operateOperation, Log, context);
    }

    private static async Task RunAsync(Func<Task> operateOperation, ILogger Log, string context)
    {
        try
        {
            await operateOperation().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 正常取消（退出 / 切页），不算故障
            Log.LogDebug("后台操作已取消：{Context}", context);
        }
        catch (Exception exception)
        {
            Log.LogError(exception, "后台操作失败：{Context}", context);
        }
    }
}
