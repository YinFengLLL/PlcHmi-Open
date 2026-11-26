using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using 上位机.应用.服务;
using 上位机.应用.视图;
using 上位机.业务.服务;
using 上位机.核心.抽象;

namespace 上位机.应用.视图模型;

/// <summary>
/// 弹窗宿主：把「M 触发线圈 → 弹窗」翻译成窗口的显示与关闭。
///
/// <para><b>数据流：</b>弹窗监控服务轮询触发线圈 → 触发键变化 → 这里定位弹窗定义 →
/// 打开/关闭 <see cref="弹窗窗口"/>。触发线圈断开后窗口自动关闭（实时关系，不做锁存）。</para>
///
/// <para><b>显示方式：</b>用与<b>登录界面同一套窗口参数</b>的独立窗口（固定宽度、
/// 高度自适应内容、以主窗为所有者居中），因此内容永远完整可见；不用主窗内的悬浮层——
/// 那会被主窗行高与裁剪影响（现场已实测到内容显示不全）。</para>
///
/// <para>按钮是<b>复归型</b>：按住置位、松开复位（手势接在 <see cref="弹窗窗口"/> 里）。</para>
/// </summary>
public sealed partial class 弹窗宿主视图模型 : ObservableObject
{
    private readonly 弹窗监控服务 _监控;
    private readonly ILogger<弹窗宿主视图模型> _日志;

    /// <summary>正在显示的弹窗窗口；null = 无。</summary>
    private 弹窗窗口? _窗口;

    /// <summary>当前显示的弹窗；null = 无。</summary>
    [ObservableProperty]
    private 弹窗定义? _当前弹窗;

    public 弹窗宿主视图模型(弹窗监控服务 监控, ILogger<弹窗宿主视图模型> 日志)
    {
        _监控 = 监控;
        _日志 = 日志;

        // 触发事件来自轮询线程（后台）：必须切到界面线程再动窗口与属性，
        // 否则轻则提示不刷新、重则抛跨线程访问异常。
        _监控.触发已变更 += (_, _) => 界面派发.投递(定位弹窗);

        定位弹窗();
    }

    /// <summary>是否正在显示弹窗。</summary>
    public bool 有弹窗 => 当前弹窗 is not null;

    /// <summary>按住按钮（复归型：置位对应线圈）。</summary>
    public Task 异步按下(int 按钮序号) =>
        当前弹窗 is null
            ? Task.CompletedTask
            : _监控.异步按下(当前弹窗, 按钮序号);

    /// <summary>松开按钮（复归型：复位对应线圈；PLC 逻辑随后复位触发线圈 → 弹窗关闭）。</summary>
    public Task 异步松开(int 按钮序号) =>
        当前弹窗 is null
            ? Task.CompletedTask
            : _监控.异步松开(当前弹窗, 按钮序号);

    /// <summary>
    /// 供窗口手势用的即发即忘版本：失败必定留痕。
    /// <para>曾经的 <c>_ = _宿主.异步按下(0)</c> 直接丢弃 Task，PLC 未连接/超时时的
    /// 异常落在无人观察的任务上，直到 GC 才经 CrashGuard 变成"程序异常"红条——
    /// 正是已知历史缺陷"重试任务异常未被观察"的同款路径。</para>
    /// </summary>
    public void 即发即忘按下(int 按钮序号) =>
        异步安全调用.即发即忘(() => 异步按下(按钮序号), _日志, $"弹窗按钮{按钮序号 + 1}按住");

    /// <summary>即发即忘松开（失败必定留痕，见 <see cref="即发即忘按下"/>）。</summary>
    public void 即发即忘松开(int 按钮序号) =>
        异步安全调用.即发即忘(() => 异步松开(按钮序号), _日志, $"弹窗按钮{按钮序号 + 1}松开");

    private void 定位弹窗()
    {
        var 键 = _监控.当前触发键;

        当前弹窗 = 键 is null
            ? null
            : _监控.弹窗集合.FirstOrDefault(定义 => 定义.键 == 键);

        OnPropertyChanged(nameof(有弹窗));

        if (当前弹窗 is null)
        {
            关闭窗口();
            return;
        }

        if (_窗口 is null)
        {
            显示窗口(当前弹窗);
            return;
        }

        // 触发从一个弹窗切到另一个：同一个窗口换内容即可
        _窗口.更新(当前弹窗);
    }

    private void 显示窗口(弹窗定义 定义)
    {
        // 主窗尚未创建（极早的触发）时先不显示；下次触发起伏时会重新进入这里
        if (Application.Current?.MainWindow is not { } 主窗)
        {
            return;
        }

        _窗口 = 弹窗窗口.创建(this, 定义);
        _窗口.Owner = 主窗;

        // 窗口被真正关闭（程序退出 / 主窗关闭）后清引用，避免下次触发复用一个已关闭的窗口
        _窗口.Closed += (_, _) => _窗口 = null;

        _窗口.Show();
    }

    private void 关闭窗口()
    {
        if (_窗口 is null)
        {
            return;
        }

        // 先清引用再关；走强制关闭，避免 Closing 拦截把窗口留在屏幕上
        var 待关 = _窗口;
        _窗口 = null;

        待关.强制关闭();
    }
}
