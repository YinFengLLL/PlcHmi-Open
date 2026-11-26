using Microsoft.Extensions.Logging;

namespace 上位机.应用.服务;

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
internal static class 异步安全调用
{
    /// <summary>
    /// 执行一个异步操作，失败时记录日志而不是静默丢弃。
    /// </summary>
    /// <param name="operation">要执行的异步操作。</param>
    /// <param name="logger">用于记录异常的日志器。</param>
    /// <param name="context">出错时用于定位的场景描述（例如"同步手动使能状态到 PLC"）。</param>
    public static void 即发即忘(Func<Task> 操作, ILogger 日志, string 上下文)
    {
        ArgumentNullException.ThrowIfNull(操作);
        ArgumentNullException.ThrowIfNull(日志);

        _ = 异步运行(操作, 日志, 上下文);
    }

    private static async Task 异步运行(Func<Task> 操作, ILogger 日志, string 上下文)
    {
        try
        {
            await 操作().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 正常取消（退出 / 切页），不算故障
            日志.LogDebug("后台操作已取消：{Context}", 上下文);
        }
        catch (Exception 异常)
        {
            日志.LogError(异常, "后台操作失败：{Context}", 上下文);
        }
    }
}
