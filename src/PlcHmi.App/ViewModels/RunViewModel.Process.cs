using CommunityToolkit.Mvvm.ComponentModel;
using PlcHmi.Business.Services;

namespace PlcHmi.App.ViewModels;

/// <summary>
/// <see cref="RunViewModel"/> 的「工位状态条」部分（partial）：主页面左下角通讯灯之后的三盏
/// 指示灯（锁附 / 点胶 / 加工完成）与「已锁附颗数」。
///
/// <para><b>2026-10-16 现场要求：</b>三盏灯 <c>0</c> = 未完成、<c>1</c> = 完成；
/// 颗数读一个 <b>INT</b> 型地址。地址由管理员在运行页「编辑地址」里改（点位 → tags.json）。</para>
///
/// <para><b>数据来源：</b>数值由 <see cref="DeviceService"/> 统一汇聚并推事件（与节拍、产量同一条链路），
/// 这里只负责转成界面文案与颜色键——界面不做点位解析，也不订阅原始点位流。</para>
///
/// <para><b>为什么拆成 partial：</b>RunViewModel.cs 已顶到规模门禁附近，
/// 工位状态条与"设备状态 / 节拍 / 产量 / 图表"是独立的一块。</para>
/// </summary>
public sealed partial class RunViewModel
{
    [ObservableProperty] private string _screwStateText = "未完成";
    [ObservableProperty] private string _screwStateColorKey = "MutedBrush";
    [ObservableProperty] private string _dispenseStateText = "未完成";
    [ObservableProperty] private string _dispenseStateColorKey = "MutedBrush";
    [ObservableProperty] private string _processStateText = "未完成";
    [ObservableProperty] private string _processStateColorKey = "MutedBrush";
    [ObservableProperty] private string _screwedPieceCountText = "0";

    /// <summary>订阅设备服务的加工状态事件（构造函数调用）。</summary>
    private void SubscribeProcessState()
    {
        _device.screwStateChanged += (_, complete) => AtUiThreadExecute(() => ApplyScrewState(complete));
        _device.dispenseStateChanged += (_, complete) => AtUiThreadExecute(() => ApplyDispenseState(complete));
        _device.processStateChanged += (_, complete) => AtUiThreadExecute(() => ApplyProcessState(complete));
        _device.screwedPieceCountChanged += (_, pieceCount) => AtUiThreadExecute(() => ApplyScrewedPieceCount(pieceCount));
    }

    /// <summary>
    /// 用设备服务当前快照刷新四块显示（初始化时调用）。
    /// <para><b>不能只依赖事件</b>：设备服务只在值变化时推事件，进页面时若状态一直没变
    /// （例如锁附已完成并保持），事件永远不来，指示灯会停在初始的"未完成"。</para>
    /// </summary>
    private void ApplyProcessInitialSnapshot()
    {
        ApplyScrewState(_device.ScrewComplete);
        ApplyDispenseState(_device.DispenseComplete);
        ApplyProcessState(_device.ProcessComplete);
        ApplyScrewedPieceCount(_device.ScrewedPieceCount);
    }

    private void ApplyScrewState(bool complete) => (ScrewStateText, ScrewStateColorKey) = DeviceService.CompleteStateShow(complete);

    private void ApplyDispenseState(bool complete) => (DispenseStateText, DispenseStateColorKey) = DeviceService.CompleteStateShow(complete);

    private void ApplyProcessState(bool complete) => (ProcessStateText, ProcessStateColorKey) = DeviceService.CompleteStateShow(complete);

    private void ApplyScrewedPieceCount(long pieceCount) => ScrewedPieceCountText = pieceCount.ToString("N0");
}
