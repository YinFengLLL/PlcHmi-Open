namespace 上位机.应用.服务;

/// <summary>
/// 运行期提示的类别。
///
/// <para><b>为什么要分类：</b>"PLC 未连接 / 连接被中止 / 通信超时"在工控现场是<b>常态</b>
/// （设备没上电、网线没插、正在指数退避重连）。若把它和真正的代码缺陷混在一条红条里喊
/// "程序异常"，操作员会很快学会无视顶栏——真正的缺陷就此淹没在噪声里。</para>
/// </summary>
public enum 运行期错误类别
{
    /// <summary>真正的程序缺陷 / 非预期异常：红条，并计入"建议重启"统计。</summary>
    程序异常 = 0,

    /// <summary>
    /// 可预期的现场状况（PLC 未连接 / 连接被中止 / 通信超时）：橙条，
    /// <b>不计入</b>重启统计，连接恢复后自动消失。
    /// </summary>
    设备未连接 = 1,
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
public interface 运行期错误接收器接口
{
    /// <summary>最近一次错误（无错误时为 <c>null</c>）。</summary>
    string? 最后错误 { get; }

    /// <summary>最近一次错误的类别。</summary>
    运行期错误类别 最后类别 { get; }

    /// <summary>短时间内异常次数达到阈值，建议重启（只统计 <see cref="运行期错误类别.程序异常"/>）。</summary>
    bool 应重启 { get; }

    /// <summary>错误集合变化（可能来自任意线程，订阅方自行切 UI 线程）。</summary>
    event EventHandler? 已变更;

    /// <summary>记录一次运行期错误。</summary>
    /// <param name="消息">给操作员看的一句话。</param>
    /// <param name="类别">类别，默认按程序异常处理（严进宽出：宁可多报也不漏报真缺陷）。</param>
    void 上报(string 消息, 运行期错误类别 类别 = 运行期错误类别.程序异常);
}

/// <summary>默认实现：进程内单例。</summary>
public sealed class 运行期错误服务 : 运行期错误接收器接口
{
    /// <summary>判定"反复出错"的时间窗口。</summary>
    private static readonly TimeSpan 窗口 = TimeSpan.FromSeconds(60);

    /// <summary>窗口内达到这个次数就建议重启。</summary>
    private const int 重启阈值 = 5;

    private readonly object _同步 = new();

    private DateTime _窗口启动 = DateTime.Now;
    private int _窗口内数量;

    /// <inheritdoc />
    public event EventHandler? 已变更;

    /// <inheritdoc />
    public string? 最后错误 { get; private set; }

    /// <inheritdoc />
    public 运行期错误类别 最后类别 { get; private set; } = 运行期错误类别.程序异常;

    /// <inheritdoc />
    public bool 应重启 { get; private set; }

    /// <inheritdoc />
    public void 上报(string 消息, 运行期错误类别 类别 = 运行期错误类别.程序异常)
    {
        lock (_同步)
        {
            最后错误 = 消息;
            最后类别 = 类别;

            // 设备类提示不进"反复出错"统计：连接抖十次也不代表程序不可信
            if (类别 is 运行期错误类别.程序异常)
            {
                var 当前时刻 = DateTime.Now;

                if (当前时刻 - _窗口启动 > 窗口)
                {
                    _窗口启动 = 当前时刻;
                    _窗口内数量 = 0;
                    应重启 = false;
                }

                _窗口内数量++;

                if (_窗口内数量 >= 重启阈值)
                {
                    应重启 = true;
                }
            }
        }

        已变更?.Invoke(this, EventArgs.Empty);
    }
}
