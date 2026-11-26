using Microsoft.Extensions.Logging;

namespace 上位机.应用.服务;

/// <summary>
/// 界面命令的失败处理：<b>记录日志 + 弹出错误框</b>。
///
/// <para><b>为什么抽成公共方法：</b>「try → await 服务调用 → catch (Exception) 记日志 + 弹框」
/// 这段样板在多个 ViewModel 的命令里逐字重复。重复带来的真实代价不是行数，
/// 而是**处理方式会漂移**：有的只顾记日志不提示（操作员以为命令生效了），
/// 有的提示了却不记日志（事后查不到）。抽到一处后，失败语义只有一种。</para>
///
/// <para>注意本方法与 <see cref="异步安全调用.即发即忘"/> 的分工：
/// 那个用于属性 setter / 无返回命令的"即发即忘"（只记日志，不打扰操作员）；
/// 本方法用于用户主动点击、且必须给出反馈的命令。</para>
/// </summary>
internal static class 界面命令
{
    /// <summary>
    /// 执行一个由用户触发的异步命令。失败时记录错误并弹出「{动作}失败」提示框，
    /// <b>不向上抛</b> —— 命令处理器里再抛异常只会变成未处理异常。
    /// </summary>
    /// <param name="日志">用于记录异常的日志器。</param>
    /// <param name="对话框">用于提示操作员的对话框服务。</param>
    /// <param name="动作">动作名，如"新增一栏""启动"；同时也是失败提示框的标题前缀。</param>
    /// <param name="命令">要执行的异步命令。</param>
    /// <param name="上下文">可选的定位信息（如"索引 3"），只进日志，不进提示框。</param>
    /// <returns>命令是否成功。调用方据此决定要不要弹"成功"提示 ——
    /// 失败时照样弹成功提示，比不提示更糟。</returns>
    public static async Task<bool> 异步执行(
        ILogger 日志, 对话框服务接口 对话框, string 动作, Func<Task> 命令, string? 上下文 = null)
    {
        ArgumentNullException.ThrowIfNull(命令);

        try
        {
            // ConfigureAwait(true)：await 之后要回 UI 线程继续改绑定属性。
            // 用 false 会让属性变更通知从线程池线程发出，是"偶发界面不刷新"的常见来源。
            await 命令().ConfigureAwait(true);
            return true;
        }
        catch (Exception 异常)
        {
            日志.LogError(异常, "{Action}失败{Context}", 动作,
                上下文 is null ? string.Empty : $"（{上下文}）");
            对话框.错误信息($"{动作}失败", 异常.Message);
            return false;
        }
    }
}
