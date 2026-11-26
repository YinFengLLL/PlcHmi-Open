using CommunityToolkit.Mvvm.ComponentModel;
using 上位机.核心.抽象;

namespace 上位机.应用.视图模型;

/// <summary>
/// <see cref="运行视图模型"/> 的「当前工单号」部分（partial，2026-11-12）。
///
/// <para><b>现场要求：</b>主页面要能一眼看到当前工单码——和「当前机种」一样，
/// 属于"跑错件"这类事故的第一道人工防线。位置放在运行页标题栏右侧、
/// 紧挨机种徽标（那里本来就是操作员巡检时视线停留的位置）。</para>
///
/// <para><b>数据来源：</b><see cref="扫码枪服务接口.工单号"/> 的变更事件（扫码枪接收线程触发），
/// 与系统设置页读的是同一份内存真相。工单号不落盘，关软件即清空。</para>
/// </summary>
public sealed partial class 运行视图模型
{
    private readonly 扫码枪服务接口 _扫码枪;

    /// <summary>当前工单号；未扫过显示"未扫码"。</summary>
    [ObservableProperty] private string _工单号文本 = "未扫码";

    /// <summary>订阅扫码结果并拉一次当前快照（构造函数调用）。</summary>
    private void 订阅扫码工单号()
    {
        _扫码枪.工单号已变更 += (_, 工单号) => 在界面线程执行(() => 应用工单号(工单号));
        应用工单号(_扫码枪.工单号);
    }

    /// <summary>
    /// 刷新工单号显示。
    /// <para>不能只依赖事件：若页面在扫码之后才创建 / 激活，事件已经过去了，
    /// 徽标会一直停在"未扫码"（与节拍、工位灯同一套"初始拉一次快照"的规矩）。</para>
    /// </summary>
    private void 应用工单号(string 工单号) =>
        工单号文本 = string.IsNullOrWhiteSpace(工单号) ? "未扫码" : 工单号.Trim();
}
