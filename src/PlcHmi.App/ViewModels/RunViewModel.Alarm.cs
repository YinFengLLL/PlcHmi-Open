using CommunityToolkit.Mvvm.ComponentModel;
using PlcHmi.Core.Enums;

namespace PlcHmi.App.ViewModels;

/// <summary>
/// <see cref="RunViewModel"/> 的「报警展示」部分（partial）。
///
/// <para><b>2026-10-16 现场要求：</b>主界面报警不够显眼——改成 Hero 卡最上方的
/// <b>大号红色横幅</b>（22px 加粗 + 图标，紧急级闪烁），并保留底部报警胶囊；
/// 报警地址在 PLC 侧复位为 0 时横幅自动消失。</para>
///
/// <para><b>为什么拆成 partial：</b>RunViewModel.cs 已顶到 400 行规模门禁，
/// 报警展示与"设备状态 / 节拍 / 产量 / 图表"是两件事。</para>
/// </summary>
public sealed partial class RunViewModel
{
    [ObservableProperty] private string _alarmSummary = "当前无报警";
    [ObservableProperty] private bool _hasActiveAlarm;

    /// <summary>
    /// 报警横幅是否闪烁（仅<b>紧急</b>级）。
    /// <para>闪烁只留给最高等级：警告级常亮即可——全都闪等于都不闪，
    /// 现场会很快对闪烁麻木（UI 铁律 5）。样式层据此 + 系统"减少动效"决定是否启动动画。</para>
    /// </summary>
    [ObservableProperty] private bool _alarmFlickerBlink;

    [ObservableProperty] private string _alarmSummaryColorKey = "SuccessBrush";

    /// <summary>无活动报警（首屏"当前无报警"提示行据此显隐：有警时让位给大号报警横幅，不重复显示）。</summary>
    public bool NoneActiveAlarm => !HasActiveAlarm;

    partial void OnHasActiveAlarmChanged(bool value) => OnPropertyChanged(nameof(NoneActiveAlarm));

    /// <summary>
    /// 刷新主监控页的报警展示（顶部大号横幅 + 报警胶囊）。
    ///
    /// <para><b>自动消失：</b>报警由 <c>报警扫描服务</c> 按 PLC 位扫描——位仍为 1 则保持活动，
    /// 位复位为 0 即产生"已清除"事件，这里随之把横幅收掉。上位机不缓存报警状态，
    /// 界面上看到的就是 PLC 当下的值（现场要求：报警地址复位后横幅自动消失）。</para>
    /// </summary>
    private void RefreshAlarmSummary()
    {
        var Active = _alarm.ActiveAlarm;

        if (Active.Count == 0)
        {
            AlarmSummary = "当前无报警";
            HasActiveAlarm = false;
            AlarmFlickerBlink = false;
            AlarmSummaryColorKey = "SuccessBrush";
            return;
        }

        // MaxBy 单遍取最高等级，免去 OrderByDescending 的整表排序
        var top = Active.MaxBy(item => item.Level)!;
        AlarmSummary = $"{top.Code}　{top.Message}　（共 {Active.Count} 条，最近 {top.Triggered:HH:mm:ss}）";
        HasActiveAlarm = true;
        AlarmFlickerBlink = top.Level == AlarmLevel.Critical;
        AlarmSummaryColorKey = top.Level switch
        {
            AlarmLevel.Critical => "DangerBrush",
            AlarmLevel.Warning => "WarningBrush",
            _ => "PrimaryLightBrush",
        };
    }
}
