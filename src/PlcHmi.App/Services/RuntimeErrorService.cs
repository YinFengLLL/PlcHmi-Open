namespace PlcHmi.App.Services;

/// <summary>
/// 运行期提示的类别。
///
/// <para><b>为什么要分类：</b>"PLC 未连接 / 连接被中止 / 通信超时"在工控现场是<b>常态</b>
/// （设备没上电、网线没插、正在指数退避重连）。若把它和真正的代码缺陷混在一条红条里喊
/// "程序异常"，操作员会很快学会无视顶栏——真正的缺陷就此淹没在噪声里。</para>
/// </summary>
public enum RuntimeErrorCategory
{
    /// <summary>真正的程序缺陷 / 非预期异常：红条，并计入"建议重启"统计。</summary>
    ProcessIndexException = 0,

    /// <summary>
    /// 可预期的现场状况（PLC 未连接 / 连接被中止 / 通信超时）：橙条，
    /// <b>不计入</b>重启统计，连接恢复后自动消失。
    /// </summary>
    DeviceDisconnected = 1,
}

/// <summary>
/// 运行期严重错误的收集处。
///
/// <para><b>为什么不用 MessageBox：</b>无人值守的工控机上，模态对话框是个陷阱 ——
/// 它阻断界面线程直到有人来点"确定"。夜班没人、操作员在隔壁机台，
/// 结果就是"报警还在响，但界面已经不动了"，而且看不出是卡住还是死了。
/// 弹一次还可能连环弹：异常反复触发时，模态框会一层层叠上去。</para>
///
/// <para>因此改成<b>常驻横幅</b>：错误照样记日志、照样显示，但不阻断操作，
/// 操作员有空时再处理。同时统计短时间内的异常次数 —— 反复出错说明状态已经不可信，
/// 这时才明确提示重启。</para>
/// </summary>
public interface IRuntimeErrorReceiver
{
    /// <summary>最近一次错误（无错误时为 <c>null</c>）。</summary>
    string? LastError { get; }

    /// <summary>最近一次错误的类别。</summary>
    RuntimeErrorCategory LastCategory { get; }

    /// <summary>短时间内异常次数达到阈值，建议重启（只统计 <see cref="RuntimeErrorCategory.ProcessIndexException"/>）。</summary>
    bool ShouldRestart { get; }

    /// <summary>错误集合变化（可能来自任意线程，订阅方自行切 UI 线程）。</summary>
    event EventHandler? changed;

    /// <summary>记录一次运行期错误。</summary>
    /// <param name="Message">给操作员看的一句话。</param>
    /// <param name="Category">类别，默认按程序异常处理（严进宽出：宁可多报也不漏报真缺陷）。</param>
    void Report(string Message, RuntimeErrorCategory Category = RuntimeErrorCategory.ProcessIndexException);
}

/// <summary>默认实现：进程内单例。</summary>
public sealed class RuntimeErrorService : IRuntimeErrorReceiver
{
    /// <summary>判定"反复出错"的时间窗口。</summary>
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    /// <summary>窗口内达到这个次数就建议重启。</summary>
    private const int restartThreshold = 5;

    private readonly object _sync = new();

    private DateTime _windowStart = DateTime.Now;
    private int _windowInnerCount;

    /// <inheritdoc />
    public event EventHandler? changed;

    /// <inheritdoc />
    public string? LastError { get; private set; }

    /// <inheritdoc />
    public RuntimeErrorCategory LastCategory { get; private set; } = RuntimeErrorCategory.ProcessIndexException;

    /// <inheritdoc />
    public bool ShouldRestart { get; private set; }

    /// <inheritdoc />
    public void Report(string Message, RuntimeErrorCategory Category = RuntimeErrorCategory.ProcessIndexException)
    {
        lock (_sync)
        {
            LastError = Message;
            LastCategory = Category;

            // 设备类提示不进"反复出错"统计：连接抖十次也不代表程序不可信
            if (Category is RuntimeErrorCategory.ProcessIndexException)
            {
                var currentTimestamp = DateTime.Now;

                if (currentTimestamp - _windowStart > Window)
                {
                    _windowStart = currentTimestamp;
                    _windowInnerCount = 0;
                    ShouldRestart = false;
                }

                _windowInnerCount++;

                if (_windowInnerCount >= restartThreshold)
                {
                    ShouldRestart = true;
                }
            }
        }

        changed?.Invoke(this, EventArgs.Empty);
    }
}
