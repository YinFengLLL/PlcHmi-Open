using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PlcHmi.App.Services;
using PlcHmi.Business.Configuration;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.App.ViewModels;

/// <summary>
/// <see cref="MasterViewModel"/> 的「横幅刷新」部分（partial）。
///
/// <para>三条横幅（配置降级 / 运行期异常 / 活动报警）都是"把后台状态翻译成一句话"的展示逻辑，
/// 与导航、权限、连接这些主职责无关。拆出来是为了守住单文件 400 行门禁。</para>
/// </summary>
public sealed partial class MasterViewModel
{
    /// <summary>
    /// 刷新配置降级横幅。
    ///
    /// <para>配置读不出来时程序照常启动（可用性优先），但<b>不能让操作员以为一切正常</b>：
    /// 报警表损坏而界面显示"无报警"，等于让人对着一台瞎了的设备干活。
    /// 因此只要登记了问题，就在内容区上方常驻一条横幅 —— 不自动消失、不可关闭。</para>
    /// </summary>
    private void RefreshConfigHealth()
    {
        var Issue = _configHealth.IssueCollection;

        if (Issue.Count == 0)
        {
            HasConfigIssue = false;
            ConfigIssueText = string.Empty;
            return;
        }

        ConfigIssueColorKey = Issue.Any(issueItem => issueItem.IsBlocked) ? "DangerBrush" : "WarningStrongBrush";

        ConfigIssueText = "配置异常：" + string.Join("；", Issue.Select(issueItem => issueItem.Summary));
    }

    /// <summary>
    /// 刷新运行期异常横幅。
    ///
    /// <para>异常的可见性是被低估的一环：.NET Core 会静默吞掉"没人 await 的任务异常"，
    /// 于是故障只表现为"某个数字一直不更新"，日志里一片干净。
    /// 这里保证凡是记录过的异常，操作员都能在界面上看见。</para>
    /// </summary>
    private void RefreshRuntimeError()
    {
        var Last = _runtimeErrorList.LastError;

        if (string.IsNullOrEmpty(Last))
        {
            HasRuntimeError = false;
            RuntimeErrorText = string.Empty;
            return;
        }

        // 设备类提示描述的是"此刻的连接状态"，不是历史故障：
        // 连接恢复后自动消失，免得现场把橙条当成长期故障去追。
        if (_runtimeErrorList.LastCategory is RuntimeErrorCategory.DeviceDisconnected)
        {
            if (_connect.State is ConnectionState.Connected)
            {
                HasRuntimeError = false;
                RuntimeErrorText = string.Empty;
                return;
            }

            HasRuntimeError = true;
            RuntimeErrorColorKey = "WarningStrongBrush";
            RuntimeErrorBottomColorKey = "WarningSoftBrush";
            RuntimeErrorText = $"{Last}（连接恢复后自动消失；各页显示的是上次读到的值）";
            return;
        }

        HasRuntimeError = true;
        RuntimeErrorColorKey = "DangerBrush";
        RuntimeErrorBottomColorKey = "DangerSoftBrush";
        RuntimeErrorText = _runtimeErrorList.ShouldRestart
            ? $"程序多次异常，功能可能已不可信，建议尽快重启上位机。最近一次：{Last}"
            : $"程序异常（已记录日志，可继续操作）：{Last}";
    }

    private void RefreshAlarmRow()
    {
        var Active = _alarm.ActiveAlarm;

        if (Active.Count == 0)
        {
            AlarmRowText = "当前无报警";
            HasActiveAlarm = false;
            AlarmRowColorKey = "SuccessBrush";
            return;
        }

        // MaxBy 单遍取最高等级，免去 OrderByDescending 的整表排序
        var top = Active.MaxBy(item => item.Level)!;
        AlarmRowText = $"{top.Code}　{top.Message}　{top.Triggered:HH:mm:ss}　（共 {Active.Count} 条活动报警）";
        HasActiveAlarm = true;
        AlarmRowColorKey = top.Level switch
        {
            AlarmLevel.Critical => "DangerBrush",
            AlarmLevel.Warning => "WarningBrush",
            _ => "PrimaryLightBrush",
        };
    }
}
