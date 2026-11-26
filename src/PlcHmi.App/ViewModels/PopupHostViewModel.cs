using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using PlcHmi.App.Services;
using PlcHmi.App.Views;
using PlcHmi.Business.Services;
using PlcHmi.Core.Abstractions;

namespace PlcHmi.App.ViewModels;

/// <summary>
/// 弹窗宿主：把「M 触发线圈 → 弹窗」翻译成窗口的显示与关闭。
///
/// <para><b>数据流：</b>弹窗监控服务轮询触发线圈 → 触发键变化 → 这里定位弹窗定义 →
/// 打开/关闭 <see cref="PopupWindow"/>。触发线圈断开后窗口自动关闭（实时关系，不做锁存）。</para>
///
/// <para><b>显示方式：</b>用与<b>登录界面同一套窗口参数</b>的独立窗口（固定宽度、
/// 高度自适应内容、以主窗为所有者居中），因此内容永远完整可见；不用主窗内的悬浮层——
/// 那会被主窗行高与裁剪影响（现场已实测到内容显示不全）。</para>
///
/// <para>按钮是<b>复归型</b>：按住置位、松开复位（手势接在 <see cref="PopupWindow"/> 里）。</para>
/// </summary>
public sealed partial class PopupHostViewModel : ObservableObject
{
    private readonly PopupMonitorService _monitorControl;
    private readonly ILogger<PopupHostViewModel> _log;

    /// <summary>正在显示的弹窗窗口；null = 无。</summary>
    private PopupWindow? _window;

    /// <summary>当前显示的弹窗；null = 无。</summary>
    [ObservableProperty]
    private PopupDefinition? _currentPopup;

    public PopupHostViewModel(PopupMonitorService MonitorControl, ILogger<PopupHostViewModel> Log)
    {
        _monitorControl = MonitorControl;
        _log = Log;

        // 触发事件来自轮询线程（后台）：必须切到界面线程再动窗口与属性，
        // 否则轻则提示不刷新、重则抛跨线程访问异常。
        _monitorControl.triggerChanged += (_, _) => UiDispatch.Post(PositioningPopup);

        PositioningPopup();
    }

    /// <summary>是否正在显示弹窗。</summary>
    public bool HasPopup => CurrentPopup is not null;

    /// <summary>按住按钮（复归型：置位对应线圈）。</summary>
    public Task PressAsync(int buttonIndex) =>
        CurrentPopup is null
            ? Task.CompletedTask
            : _monitorControl.PressAsync(CurrentPopup, buttonIndex);

    /// <summary>松开按钮（复归型：复位对应线圈；PLC 逻辑随后复位触发线圈 → 弹窗关闭）。</summary>
    public Task ReleaseAsync(int buttonIndex) =>
        CurrentPopup is null
            ? Task.CompletedTask
            : _monitorControl.ReleaseAsync(CurrentPopup, buttonIndex);

    /// <summary>
    /// 供窗口手势用的即发即忘版本：失败必定留痕。
    /// <para>曾经的 <c>_ = _宿主.异步按下(0)</c> 直接丢弃 Task，PLC 未连接/超时时的
    /// 异常落在无人观察的任务上，直到 GC 才经 CrashGuard 变成"程序异常"红条——
    /// 正是已知历史缺陷"重试任务异常未被观察"的同款路径。</para>
    /// </summary>
    public void FireAndForgetPress(int buttonIndex) =>
        SafeInvokeAsync.FireAndForget(() => PressAsync(buttonIndex), _log, $"弹窗按钮{buttonIndex + 1}按住");

    /// <summary>即发即忘松开（失败必定留痕，见 <see cref="FireAndForgetPress"/>）。</summary>
    public void FireAndForgetRelease(int buttonIndex) =>
        SafeInvokeAsync.FireAndForget(() => ReleaseAsync(buttonIndex), _log, $"弹窗按钮{buttonIndex + 1}松开");

    private void PositioningPopup()
    {
        var Key = _monitorControl.CurrentTriggerKey;

        CurrentPopup = Key is null
            ? null
            : _monitorControl.PopupCollection.FirstOrDefault(Definition => Definition.Key == Key);

        OnPropertyChanged(nameof(HasPopup));

        if (CurrentPopup is null)
        {
            CloseWindow();
            return;
        }

        if (_window is null)
        {
            ShowWindow(CurrentPopup);
            return;
        }

        // 触发从一个弹窗切到另一个：同一个窗口换内容即可
        _window.Update(CurrentPopup);
    }

    private void ShowWindow(PopupDefinition Definition)
    {
        // 主窗尚未创建（极早的触发）时先不显示；下次触发起伏时会重新进入这里
        if (Application.Current?.MainWindow is not { } masterWindow)
        {
            return;
        }

        _window = PopupWindow.Create(this, Definition);
        _window.Owner = masterWindow;

        // 窗口被真正关闭（程序退出 / 主窗关闭）后清引用，避免下次触发复用一个已关闭的窗口
        _window.Closed += (_, _) => _window = null;

        _window.Show();
    }

    private void CloseWindow()
    {
        if (_window is null)
        {
            return;
        }

        // 先清引用再关；走强制关闭，避免 Closing 拦截把窗口留在屏幕上
        var pendingClose = _window;
        _window = null;

        pendingClose.ForceClose();
    }
}
