using CommunityToolkit.Mvvm.ComponentModel;
using PlcHmi.App.Services;
using PlcHmi.Core.Models;

namespace PlcHmi.App.ViewModels;

/// <summary>
/// <see cref="PointTableTeachViewModel"/> 的「轴位置 / 当前浮高显示」部分（partial）。
///
/// <para>从主文件拆出：主文件已接近规模门禁，而这段与点表读写、点动命令都无关，
/// 只管"把轴位置标签的当前值显示到轴按钮上"。</para>
///
/// <para><b>2026-10-24 现场要求（锁附页）：</b>手动区在 X/Y/Z/ZZ 当前位之后增加「当前浮高」，
/// 读一个独立 D 地址（REAL 占 2 字，缺省 D240，管理员可在「编辑地址」改），
/// 显示风格与轴当前位置一致。</para>
/// </summary>
public abstract partial class PointTableTeachViewModel
{
    /// <summary>当前浮高文本（读不到时显示 <c>--.--</c>，与轴位置的"读不到"同一口径）。</summary>
    [ObservableProperty] private string _currentFloatHeightText = "--.--";

    /// <summary>本页是否显示当前浮高（只有配了地址的表才显示，目前是锁附页）。</summary>
    public bool ShowCurrentFloatHeight => TableDefinition.CurrentFloatHeightAddress > 0;

    /// <summary>
    /// 进入页面时拉一次轴位置快照。
    ///
    /// <para><b>为什么不能只靠事件：</b>标签值服务的「新值已变更」是"变化才推送"——
    /// 轴停着不动时，页面上会一直显示 <c>--.--</c>，操作员以为位置读不出来
    /// （审查标准 R2：订阅事件流的页面必须在初始拉一次快照）。
    /// 首次采集发生在页面 ViewModel 构造之前，所以这里必须主动取一次当前值。</para>
    /// </summary>
    protected void RefreshAxisPositionSnapshot()
    {
        foreach (var axisItem in AxisList)
        {
            if (axisItem.PositionTag.Length > 0 && _valueList.TryGetDouble(axisItem.PositionTag, out var positionValue))
            {
                axisItem.PositionText = CoordinateAux.Format(positionValue);
            }
        }

        // 浮高不在标签表里（独立地址、不参与轮询）：进页面主动读一次
        SafeInvokeAsync.FireAndForget(RefreshCurrentFloatHeightAsync, _log, "读取当前浮高");
    }

    /// <summary>
    /// 从 PLC 读一次当前浮高并刷新显示（由主界面的 1s 节拍调用）。
    /// <para><b>读不到时显示 <c>--.--</c>：</b>服务层对"未配置地址 / 未连接 / 只读到一半"
    /// 返回 <c>null</c>。偶发的通讯异常由调用方（<see cref="SafeInvokeAsync"/>）统一记录，
    /// 下一拍（1s）自动重试——这条是辅助显示，不能因为它中断整页的周期回读。</para>
    /// </summary>
    public async Task RefreshCurrentFloatHeightAsync()
    {
        if (!ShowCurrentFloatHeight)
        {
            return;
        }

        var Value = await PointTableService.ReadCurrentFloatHeightAsync(TableKey).ConfigureAwait(false);
        var Text = Value is { } floatHeight ? CoordinateAux.Format(floatHeight) : "--.--";

        AtUiThreadExecute(() => CurrentFloatHeightText = Text);
    }
}
