using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using 上位机.应用.服务;
using 上位机.业务.配置;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.应用.视图模型;

/// <summary>
/// <see cref="主视图模型"/> 的「横幅刷新」部分（partial）。
///
/// <para>三条横幅（配置降级 / 运行期异常 / 活动报警）都是"把后台状态翻译成一句话"的展示逻辑，
/// 与导航、权限、连接这些主职责无关。拆出来是为了守住单文件 400 行门禁。</para>
/// </summary>
public sealed partial class 主视图模型
{
    /// <summary>
    /// 刷新配置降级横幅。
    ///
    /// <para>配置读不出来时程序照常启动（可用性优先），但<b>不能让操作员以为一切正常</b>：
    /// 报警表损坏而界面显示"无报警"，等于让人对着一台瞎了的设备干活。
    /// 因此只要登记了问题，就在内容区上方常驻一条横幅 —— 不自动消失、不可关闭。</para>
    /// </summary>
    private void 刷新配置健康()
    {
        var 问题 = _配置健康.问题集合;

        if (问题.Count == 0)
        {
            有配置问题 = false;
            配置问题文本 = string.Empty;
            return;
        }

        配置问题颜色键 = 问题.Any(问题项 => 问题项.是否阻塞) ? "DangerBrush" : "WarningStrongBrush";

        配置问题文本 = "配置异常：" + string.Join("；", 问题.Select(问题项 => 问题项.摘要));
    }

    /// <summary>
    /// 刷新运行期异常横幅。
    ///
    /// <para>异常的可见性是被低估的一环：.NET Core 会静默吞掉"没人 await 的任务异常"，
    /// 于是故障只表现为"某个数字一直不更新"，日志里一片干净。
    /// 这里保证凡是记录过的异常，操作员都能在界面上看见。</para>
    /// </summary>
    private void 刷新运行期错误()
    {
        var 最后 = _运行期错误列表.最后错误;

        if (string.IsNullOrEmpty(最后))
        {
            有运行期错误 = false;
            运行期错误文本 = string.Empty;
            return;
        }

        // 设备类提示描述的是"此刻的连接状态"，不是历史故障：
        // 连接恢复后自动消失，免得现场把橙条当成长期故障去追。
        if (_运行期错误列表.最后类别 is 运行期错误类别.设备未连接)
        {
            if (_连接.状态 is 连接状态.已连接)
            {
                有运行期错误 = false;
                运行期错误文本 = string.Empty;
                return;
            }

            有运行期错误 = true;
            运行期错误颜色键 = "WarningStrongBrush";
            运行期错误底色键 = "WarningSoftBrush";
            运行期错误文本 = $"{最后}（连接恢复后自动消失；各页显示的是上次读到的值）";
            return;
        }

        有运行期错误 = true;
        运行期错误颜色键 = "DangerBrush";
        运行期错误底色键 = "DangerSoftBrush";
        运行期错误文本 = _运行期错误列表.应重启
            ? $"程序多次异常，功能可能已不可信，建议尽快重启上位机。最近一次：{最后}"
            : $"程序异常（已记录日志，可继续操作）：{最后}";
    }

    private void 刷新报警栏()
    {
        var 活动 = _报警.活动报警;

        if (活动.Count == 0)
        {
            报警栏文本 = "当前无报警";
            有活动报警 = false;
            报警栏颜色键 = "SuccessBrush";
            return;
        }

        // MaxBy 单遍取最高等级，免去 OrderByDescending 的整表排序
        var 顶部 = 活动.MaxBy(甲 => 甲.等级)!;
        报警栏文本 = $"{顶部.代码}　{顶部.消息}　{顶部.已触发:HH:mm:ss}　（共 {活动.Count} 条活动报警）";
        有活动报警 = true;
        报警栏颜色键 = 顶部.等级 switch
        {
            报警等级.紧急 => "DangerBrush",
            报警等级.警告 => "WarningBrush",
            _ => "PrimaryLightBrush",
        };
    }
}
