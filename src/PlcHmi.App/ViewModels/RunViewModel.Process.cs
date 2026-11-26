using CommunityToolkit.Mvvm.ComponentModel;
using 上位机.业务.服务;

namespace 上位机.应用.视图模型;

/// <summary>
/// <see cref="运行视图模型"/> 的「工位状态条」部分（partial）：主页面左下角通讯灯之后的三盏
/// 指示灯（锁附 / 点胶 / 加工完成）与「已锁附颗数」。
///
/// <para><b>2026-10-16 现场要求：</b>三盏灯 <c>0</c> = 未完成、<c>1</c> = 完成；
/// 颗数读一个 <b>INT</b> 型地址。地址由管理员在运行页「编辑地址」里改（点位 → tags.json）。</para>
///
/// <para><b>数据来源：</b>数值由 <see cref="设备服务"/> 统一汇聚并推事件（与节拍、产量同一条链路），
/// 这里只负责转成界面文案与颜色键——界面不做点位解析，也不订阅原始点位流。</para>
///
/// <para><b>为什么拆成 partial：</b>RunViewModel.cs 已顶到规模门禁附近，
/// 工位状态条与"设备状态 / 节拍 / 产量 / 图表"是独立的一块。</para>
/// </summary>
public sealed partial class 运行视图模型
{
    [ObservableProperty] private string _锁附状态文本 = "未完成";
    [ObservableProperty] private string _锁附状态颜色键 = "MutedBrush";
    [ObservableProperty] private string _点胶状态文本 = "未完成";
    [ObservableProperty] private string _点胶状态颜色键 = "MutedBrush";
    [ObservableProperty] private string _加工状态文本 = "未完成";
    [ObservableProperty] private string _加工状态颜色键 = "MutedBrush";
    [ObservableProperty] private string _已锁附颗数文本 = "0";

    /// <summary>订阅设备服务的加工状态事件（构造函数调用）。</summary>
    private void 订阅加工状态()
    {
        _设备.锁附状态已变更 += (_, 完成) => 在界面线程执行(() => 应用锁附状态(完成));
        _设备.点胶状态已变更 += (_, 完成) => 在界面线程执行(() => 应用点胶状态(完成));
        _设备.加工状态已变更 += (_, 完成) => 在界面线程执行(() => 应用加工状态(完成));
        _设备.已锁附颗数已变更 += (_, 颗数) => 在界面线程执行(() => 应用已锁附颗数(颗数));
    }

    /// <summary>
    /// 用设备服务当前快照刷新四块显示（初始化时调用）。
    /// <para><b>不能只依赖事件</b>：设备服务只在值变化时推事件，进页面时若状态一直没变
    /// （例如锁附已完成并保持），事件永远不来，指示灯会停在初始的"未完成"。</para>
    /// </summary>
    private void 应用加工初始快照()
    {
        应用锁附状态(_设备.锁附完成);
        应用点胶状态(_设备.点胶完成);
        应用加工状态(_设备.加工完成);
        应用已锁附颗数(_设备.已锁附颗数);
    }

    private void 应用锁附状态(bool 完成) => (锁附状态文本, 锁附状态颜色键) = 设备服务.完成状态显示(完成);

    private void 应用点胶状态(bool 完成) => (点胶状态文本, 点胶状态颜色键) = 设备服务.完成状态显示(完成);

    private void 应用加工状态(bool 完成) => (加工状态文本, 加工状态颜色键) = 设备服务.完成状态显示(完成);

    private void 应用已锁附颗数(long 颗数) => 已锁附颗数文本 = 颗数.ToString("N0");
}
