using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace 上位机.应用.视图模型;

/// <summary>
/// <see cref="点表示教视图模型"/> 的「顶部固定状态灯」部分（partial）。
///
/// <para><b>2026-10-16 现场要求：</b>锁附示教与点胶示教页顶部各放一盏固定状态灯，
/// 读一个 <b>M 区位</b>：<c>0</c> → 灯闪烁 + 提示「各轴未回原」，
/// <c>1</c> → 灯常亮 + 提示「各轴回原完成」。地址由管理员在「编辑地址」里改（按分组各页一份），
/// 保存后立即生效。</para>
///
/// <para><b>为什么地址每次都从配置取：</b>这样管理员改完地址无需重启、也无需先离开页面——
/// 下一次回读（1s）就用了新地址。代价是一次列表查找，与一条 Modbus 请求相比可以忽略。</para>
///
/// <para><b>为什么与动作按钮分开刷新：</b>回原状态要跟得紧（1s），按钮组一趟要读好几路
/// 线圈（2s 够用、也更不挤占轮询）。见 <see cref="动作按钮页接口"/>。</para>
/// </summary>
public abstract partial class 点表示教视图模型
{
    /// <summary>状态灯是否可见（未配置地址时隐藏）。</summary>
    [ObservableProperty] private bool _回原灯可见;

    /// <summary>是否闪烁（未回原时闪；尊重系统"减少动效"设置由样式层处理）。</summary>
    [ObservableProperty] private bool _回原闪烁;

    /// <summary>状态文本：各轴回原完成 / 各轴未回原 / 读取失败。</summary>
    [ObservableProperty] private string _回原状态文本 = "各轴未回原";

    /// <summary>状态灯颜色键（成功 / 警告 / 次要）。</summary>
    [ObservableProperty] private string _回原状态颜色键 = "WarningBrush";

    /// <summary>悬停提示（含当前地址与取值含义，便于现场核对）。</summary>
    [ObservableProperty] private string _回原地址提示 = string.Empty;

    /// <summary>
    /// 进入页面时把本页需要周期回读的状态拉一遍（状态灯 + 动作按钮）。
    /// <para>初始化与「地址保存后立即生效」都走这里，避免两处各写一份调用顺序。</para>
    /// </summary>
    protected async Task 异步刷新本页状态()
    {
        await 异步刷新状态灯().ConfigureAwait(false);
        await 异步同步动作按钮状态().ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 单项容错：地址没配 / 读不到只把灯标成"未读到"，绝不抛给调用方——
    /// 这盏灯是辅助信息，不能因为一个地址写错就把整页初始化（或周期回读）打断。
    /// </remarks>
    public async Task 异步刷新状态灯()
    {
        var 灯项 = 页面地址服务.状态灯(分组).FirstOrDefault();

        if (灯项 is null || string.IsNullOrWhiteSpace(灯项.地址))
        {
            在界面线程执行(() => 回原灯可见 = false);
            return;
        }

        try
        {
            var 到位 = await _手动.异步读取线圈(灯项.地址).ConfigureAwait(false);
            在界面线程执行(() => 应用回原状态(灯项.地址, 到位));
        }
        catch (Exception 异常)
        {
            // 读不到（未连接 / 地址无效）：灯转为"未读到"（灰色常亮），不假装还是上次的值
            _日志.LogWarning(异常, "读取回原状态灯 {Address} 失败", 灯项.地址);
            在界面线程执行(() => 应用回原状态(灯项.地址, null));
        }
    }

    /// <summary>把一次读取结果落到灯上（<paramref name="到位"/> 为 <c>null</c> 表示这一轮没读到）。</summary>
    private void 应用回原状态(string 地址值, bool? 到位)
    {
        回原灯可见 = true;
        回原地址提示 = $"{地址值} = {(到位 is null ? "未读到" : 到位.Value ? "1" : "0")}"
                       + "　（0 = 各轴未回原，1 = 各轴回原完成；地址可在「编辑地址」中修改）";

        switch (到位)
        {
            case true:
                回原状态文本 = "各轴回原完成";
                回原状态颜色键 = "SuccessBrush";
                回原闪烁 = false;
                break;

            case false:
                回原状态文本 = "各轴未回原";
                回原状态颜色键 = "WarningBrush";
                回原闪烁 = true;
                break;

            default:
                回原状态文本 = "回原状态未读到";
                回原状态颜色键 = "MutedBrush";
                回原闪烁 = false;
                break;
        }
    }
}
