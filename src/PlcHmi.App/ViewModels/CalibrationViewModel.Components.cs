using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PlcHmi.App.Services;
using PlcHmi.Core.Models;

namespace PlcHmi.App.ViewModels;

/// <summary>
/// <see cref="CalibrationViewModel"/> 的「动作按钮 / 辅助地址」部分（partial）。
///
/// <para>现场要求标定页采用与锁附示教页相同的按钮与地址配置方式：动作按钮与辅助地址
/// 同样按「标定」分组纳入统一地址编辑入口，管理员可新增、改名、改址与删除。</para>
/// </summary>
public sealed partial class CalibrationViewModel
{
    /// <summary>标定页的辅助地址 / 动作按钮分组名（与 recipe-config.json 保持一致）。</summary>
    private const string calibrationGroup = "标定";

    /// <summary>
    /// 轴位置取自「锁附」分组的点动配置（标定页动的是锁附 X/Y/Z 三轴，
    /// 与锁附示教页同一套地址与位置点位）。
    /// </summary>
    private const string screwGroup = "锁附";

    /// <summary>
    /// 本页要显示的轴位置：锁附 X / Y / Z。与标定页的点动按钮同一组轴——
    /// 标定只动 X/Y/Z（<c>recipe-config.json</c> 里 ZZ 是锁附专有的第二 Z，标定不涉及）。
    /// </summary>
    private static readonly string[] showAxis = ["X", "Y", "Z"];

    /// <summary>
    /// 动作按钮写入版本：按钮每成功写一次自增（见 <see cref="TriggerActionAsync"/>）。
    /// <para>周期回读在读之前取一份，读完发现被改过就丢弃这批快照——
    /// 否则"读在按之前、写在按之后"的时序会把刚点亮的按钮又翻回未激活。</para>
    /// </summary>
    private int _actionWriteVersion;

    /// <summary>动作按钮（按「标定」分组过滤；默认配置为空，管理员可新增）。</summary>
    public ObservableCollection<ActionButtonViewModel> ActionButtonCollection { get; } = new();

    /// <summary>
    /// 锁附 X / Y / Z 轴当前位置（与锁附示教页同一套配置、同一套显示风格）。
    /// <para>标定时要一边看中心点数值一边看轴走到哪，原先这一页看不到实时位置。</para>
    /// </summary>
    public ObservableCollection<TeachAxisViewModel> AxisList { get; } = new();

    /// <summary>
    /// 按配置重建轴列表（与锁附示教页取同一份 <c>recipe-config.json</c> 点动配置）。
    /// <para><b>必须同时按分组过滤</b>：配置里点胶 X 轴的键也是 <c>X</c>，
    /// 只按轴键筛会得到两个 X（一个锁附、一个点胶），位置标签还各不相同。</para>
    /// </summary>
    private void RebuildAxisList()
    {
        AxisList.Clear();

        foreach (var Item in _config.Jog.Where(Item =>
                     PageGroup.Match(Item.Group, screwGroup)
                     && showAxis.Contains(Item.Axis, StringComparer.OrdinalIgnoreCase)))
        {
            AxisList.Add(new TeachAxisViewModel(Item));
        }
    }

    /// <summary>轮询推送轴位置变化时刷新对应轴的显示（与锁附示教页同一套匹配方式）。</summary>
    private void TagChanged(TagNewValue Value)
    {
        if (!Value.IsValid)
        {
            return;
        }

        var Text = CoordinateAux.Format(Value.scaleAfterNewValue);

        foreach (var axisItem in AxisList.Where(item => item.PositionTag == Value.Name))
        {
            axisItem.PositionText = Text;
        }
    }

    /// <summary>
    /// 进入页面时拉一次轴位置快照。
    /// <para>只订阅 <c>新值已变更</c> 是不够的：那是"变化才推送"，轴停着不动时页面会一直显示
    /// <c>--.--</c>（审查标准 R2：订阅事件流的页面必须在初始拉一次快照）。</para>
    /// </summary>
    private void RefreshAxisPositionSnapshot()
    {
        foreach (var axisItem in AxisList)
        {
            if (axisItem.PositionTag.Length > 0 && _valueList.TryGetDouble(axisItem.PositionTag, out var positionValue))
            {
                axisItem.PositionText = CoordinateAux.Format(positionValue);
            }
        }
    }

    /// <summary>
    /// 从 PLC 回读各动作按钮的线圈状态，让切换型按钮显示真实状态。
    /// <para>与两个示教页同一套做法：单项容错，读不到的按钮只跳过，不拦住整页初始化。
    /// 进入页面调一次，页面停留期间由 <see cref="MasterViewModel"/> 周期调一次
    /// （见 <see cref="IActionButtonPage"/>）——PLC 侧复位后不必离开页面再进来。</para>
    /// </summary>
    private async Task SyncActionButtonStateAsync()
    {
        var version = _actionWriteVersion;
        var Snapshot = new List<(ActionButtonViewModel Button, bool Activate)>();

        // 2026-11-19 性能改造：连续 M 区线圈合并成一次读，避免一页按钮连发独占串口闸锁。
        IReadOnlyList<bool?> stateList;

        try
        {
            stateList = await _manual
                .BatchReadCoilAsync(ActionButtonCollection.Select(Button => Button.Definition.Address).ToList())
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            _log.LogWarning(exception, "批量回读动作按钮状态失败，本轮跳过");
            return;
        }
        catch (System.IO.IOException exception)
        {
            _log.LogWarning(exception, "批量回读动作按钮状态失败，本轮跳过");
            return;
        }
        catch (System.Net.Sockets.SocketException exception)
        {
            _log.LogWarning(exception, "批量回读动作按钮状态失败，本轮跳过");
            return;
        }

        for (var Sequence = 0; Sequence < ActionButtonCollection.Count && Sequence < stateList.Count; Sequence++)
        {
            if (stateList[Sequence] is { } Activate)
            {
                Snapshot.Add((ActionButtonCollection[Sequence], Activate));
            }
        }

        if (Snapshot.Count == 0)
        {
            return;
        }

        // 读的途中操作员按过按钮：这批快照是"按之前"的旧值，应用它会把刚点亮的按钮又翻回去
        if (version != _actionWriteVersion)
        {
            return;
        }

        AtUiThreadExecute(() =>
        {
            foreach (var (Button, Activate) in Snapshot)
            {
                Button.Activated = Activate;
            }
        });
    }

    /// <inheritdoc />
    public Task RefreshActionButtonStateAsync() => SyncActionButtonStateAsync();

    /// <summary>辅助地址的周期回读（与动作按钮分开跑，见 <see cref="IActionButtonPage"/>）。</summary>
    public Task RefreshAuxAddressStateAsync() => _auxAddressWrite.RefreshValueAsync(AuxAddressCollection);

    /// <summary>
    /// 手动使能共享 + 写 M666（见 <see cref="ManualEnableRegister"/>）。
    ///
    /// <para><b>2026-10-15 现场反馈：</b>原先标定页只有 <c>是否手动启用</c> 这个本页属性，
    /// 没接共享服务——在标定页打开使能，锁附示教页看不到（反之亦然），PLC 侧的 M666 也不置位。
    /// 现在与两个示教页同一套做法：改服务状态（三页靠事件一起变）+ 立即写一次 PLC，
    /// 之后由 <c>手动使能保持服务</c> 持续维持。</para>
    /// </summary>
    partial void OnIsManualEnabledChanged(bool value)
    {
        // 与服务状态一致时直接返回：这通常是"由事件回写"的场景，继续往下会绕圈
        if (_manual.IsEnabled == value)
        {
            return;
        }

        _manual.SetEnabled(value);
        SafeInvokeAsync.FireAndForget(() => _manual.SyncEnabledAsync(), _log, "同步手动使能状态到 PLC");

        // 辅助地址的当前值也归手动使能管（2026-11-12）
        _auxAddressWrite.RefreshEditable(AuxAddressCollection);
    }

    /// <summary>辅助地址（按「标定」分组过滤；默认配置为空，管理员可新增）。</summary>
    public ObservableCollection<AuxAddressRowViewModel> AuxAddressCollection { get; } = new();

    /// <summary>按当前配置重建动作按钮与辅助地址行（构造、初始化与改地址后调用）。</summary>
    private void RebuildComponentArea()
    {
        ActionButtonCollection.Clear();

        foreach (var Item in _pageAddress.ActionButton(calibrationGroup))
        {
            ActionButtonCollection.Add(new ActionButtonViewModel(Item));
        }

        AuxAddressCollection.Clear();

        foreach (var Item in _pageAddress.AuxAddress(calibrationGroup))
        {
            AuxAddressCollection.Add(_auxAddressWrite.Create(Item, _pageAddress));
        }
    }

    /// <summary>读取全部辅助地址的当前值。</summary>
    private Task ReadAuxAddressAsync() =>
        // 2026-11-19 性能改造：与周期回读共用同一套合并读取
        AuxAddressRowViewModel.BatchRefreshAsync(AuxAddressCollection);

    // ── 命令 ────────────────────────────────────────────────────────────

    /// <summary>读取全部辅助地址。</summary>
    [RelayCommand]
    private async Task ReadAuxAddressValueAsync()
    {
        IsBusy = true;

        try
        {
            await UiCommand
                .ExecuteAsync(_log, _dialog, "读取辅助地址", ReadAuxAddressAsync)
                .ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // 辅助地址的写入不再有按钮：填完回车 / 失焦即写（见 辅助地址实时写入手柄 与 输入提交手势）。

    /// <summary>
    /// 切换一个动作按钮：首次按下<b>置位</b>（激活），再次按下<b>复位</b>（取消激活）。
    /// <para>与两个示教页同一套交互、同一套按钮外观（切换型、无二次确认）。</para>
    /// </summary>
    [RelayCommand]
    private async Task TriggerActionAsync(ActionButtonViewModel? ButtonItem)
    {
        if (ButtonItem is null)
        {
            return;
        }

        // 目标状态 = 取反：按一次切换一次
        var targetActive = !ButtonItem.Activated;

        IsBusy = true;

        try
        {
            if (await UiCommand
                    .ExecuteAsync(
                        _log,
                        _dialog,
                        targetActive ? $"激活「{ButtonItem.Name}」" : $"取消激活「{ButtonItem.Name}」",
                        () => _manual.WriteCoilAsync(ButtonItem.Definition.Address, targetActive))
                    .ConfigureAwait(true))
            {
                // 写入成功才改按钮状态：失败时按钮不该假装已激活
                ButtonItem.Activated = targetActive;
                _actionWriteVersion++;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }
}
