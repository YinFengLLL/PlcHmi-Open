using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PlcHmi.App.Services;
using PlcHmi.Core.Models;

namespace PlcHmi.App.ViewModels;

/// <summary>
/// <see cref="PointTableTeachViewModel"/> 的示教 / 下发 / 动作 / 地址编辑命令（partial）。
///
/// <para>从主文件拆出：主文件逼近 400 行规模门禁；这里全是"用户主动点击、
/// 必须给出反馈"的命令，统一走 <see cref="UiCommand.ExecuteAsync"/> 收口失败语义。</para>
/// </summary>
public abstract partial class PointTableTeachViewModel
{
    /// <summary>
    /// 保存坐标点：读取当前各轴位置，写入点列表中选中的点。
    /// <para>参与写入的列由点表配置的 <c>保存坐标列</c> 决定——锁附页只写 X/Y（浮高保留人工填写值），
    /// 点胶页写 X/Y/Z（指令码本就无位置标签，不受影响）。</para>
    /// </summary>
    [RelayCommand]
    private async Task SaveCoordinatePointAsync()
    {
        IsBusy = true;

        try
        {
            if (await UiCommand
                    .ExecuteAsync(_log, _dialog, "保存坐标点", () => PointTableService.TeachCurrentPositionAsync(TableKey, SelectedIndex))
                    .ConfigureAwait(true))
            {
                AtUiThreadExecute(RefreshRow);
                _dialog.Hint("已保存坐标点",
                    $"已将当前各轴位置写入第 {SelectedIndex + 1} 点，并保存到当前机种。");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// <b>参数写入</b>（原「下发点表」，2026-10-21 现场要求）：把上位机当前的机种区段
    /// D2500–D3999 整段写入 PLC，并同时触发机种保存。
    ///
    /// <para><b>为什么不只写点表：</b>示教页调的不只是 X/Y/Z——指令码、单点工艺参数等
    /// 都落在机种区段里。此前"下发点表 + 回机种页保存机种"两步走，现场经常漏第二步。</para>
    /// </summary>
    [RelayCommand]
    private async Task ParameterWriteAsync()
    {
        if (!_dialog.Confirm("参数写入",
                $"确认把上位机当前的 D{MachineModelSection.StartAt}–D{MachineModelSection.end} 参数整段写入 PLC，"
                + "并保存为当前机种？\n\n"
                + "整段写入会先清零再写入：点表坐标、指令码、单点工艺参数、标定等"
                + "全部以当前机种快照（含本页尚未下发的坐标修改）为准。",
                Views.ConfirmLevel.Warning, "写入",
                "PLC 将立即按新参数运行，确认现场无人员处于设备动作范围。"))
        {
            return;
        }

        IsBusy = true;

        try
        {
            if (await UiCommand
                    .ExecuteAsync(_log, _dialog, "参数写入", () => PointTableService.ParameterWriteAsync(TableKey))
                    .ConfigureAwait(true))
            {
                RefreshRow();

                _dialog.Hint("参数写入完成",
                    $"① 上位机当前机种（含本页刚改的点位）已整段写入 PLC 的 D{MachineModelSection.StartAt}–D{MachineModelSection.end}；\n"
                    + "② 已把 PLC 里刚写下的整段读回来，保存为当前机种——"
                    + "切换页面、切换机种或重启后仍是这份数据。");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 读取点表：从 PLC 读回本表全部点的当前值（2026-10-21 现场要求）。
    ///
    /// <para><b>为什么必须是显式动作：</b>点表落在机种区段（D2500–D3999），该区段不再参与周期采集，
    /// 页面默认显示"上次装载 / 下发"的值。要看 PLC 现状就按这颗按钮——界面不跟着 PLC 实时跳。</para>
    ///
    /// <para>读取只刷新内存与界面，<b>不写机种快照</b>（机种数据以「保存机种」为准），
    /// 所以提示里明确告诉操作员"还没保存到机种"。</para>
    /// </summary>
    [RelayCommand]
    private async Task ReadPointTableAsync()
    {
        IsBusy = true;

        try
        {
            if (await UiCommand
                    .ExecuteAsync(_log, _dialog, "读取点表", () => PointTableService.ReadPointTableAsync(TableKey))
                    .ConfigureAwait(true))
            {
                RefreshRow();

                _dialog.Hint("已读取 PLC 当前值",
                    $"已读取「{TableDefinition.DisplayName}」的 {TableDefinition.PointCount} 个点。\n\n"
                    + "界面现在显示的是 PLC 里的实时值，尚未写入当前机种快照；"
                    + "如需永久保存，请到「机种管理」页点「保存机种」。");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>清空当前点。</summary>
    [RelayCommand]
    private void ClearEmptyPoint()
    {
        if (!_dialog.Confirm("清空当前点", $"确认清空第 {SelectedIndex + 1} 点的坐标？"))
        {
            return;
        }

        PointTableService.ClearEmptyPoint(TableKey, SelectedIndex);
        RefreshRow();
    }

    // ── 动作按钮 ────────────────────────────────────────────────────────

    /// <summary>
    /// 动作按钮写入版本：按钮每成功写一次自增。
    /// <para>周期回读在读之前取一份，读完发现被改过就丢弃这批快照——
    /// 否则"读在按之前、写在按之后"的时序会把刚点亮的按钮又翻回未激活。</para>
    /// </summary>
    private int _actionWriteVersion;

    /// <summary>
    /// 切换一个动作按钮：首次按下<b>置位</b>（激活），再次按下<b>复位</b>（取消激活）。
    ///
    /// <para><b>为什么改成切换型、且不再二次确认（现场口径 2026-10-13）：</b>
    /// 这类按钮（电机复位、电批启动、出胶阀、吸螺丝真空…）在现场就是"点一次开、点一次关"的开通关。
    /// 每次按下都弹确认框既拖慢示教节奏，也会让人对弹窗麻木——真出问题时反而看不见。
    /// 激活状态改为直接显示在按钮本身（高亮 + 悬停提示），比弹窗更直观也更难漏看。</para>
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

    /// <summary>
    /// 从 PLC 回读各动作按钮的线圈状态，让切换型按钮显示真实状态。
    ///
    /// <para>切换型按钮的状态必须来自 PLC：断电重启、或在别处（PLC 程序 / 手动页）改过线圈后，
    /// 按钮若还显示"未激活"就是误导。单项容错——某个地址读不到只跳过它，
    /// 不影响其它按钮，也不该拦住整页初始化（通信抖动在示教现场是常态）。</para>
    ///
    /// <para>进入页面调一次（<see cref="InitializeAsync"/>），页面停留期间由
    /// <see cref="MasterViewModel"/> 周期调一次（见 <see cref="IActionButtonPage"/>）。</para>
    /// </summary>
    private async Task SyncActionButtonStateAsync()
    {
        var version = _actionWriteVersion;
        var Snapshot = new List<(ActionButtonViewModel Button, bool Activate)>();

        // 2026-11-19 性能改造：一页十几个按钮原逐个读线圈（连发独占串口闸锁），
        // 改为一次批量读（连续 M 区地址合并），读不到的按钮只跳过，语义不变。
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

    /// <summary>
    /// 辅助地址的周期回读（<see cref="IActionButtonPage"/>，主视图模型每 3s 调一次）。
    /// <para>与动作按钮分开跑：辅助地址一页十几行，混进按钮回读会把"PLC 复位后按钮两秒内跟上"
    /// 的时间预算吃掉（2026-11-18 现场自检就卡在这条上）。</para>
    /// </summary>
    public Task RefreshAuxAddressStateAsync() => RefreshAuxAddressValueAsync();

    /// <summary>
    /// 本页点码写入的寄存器（锁附示教 <c>D651</c> / 点胶示教 <c>D652</c>，
    /// 见 <see cref="PageContextRegister"/>）。
    /// <para>两张点表互相独立，由具体页在构造时传入各自的寄存器——
    /// 不在基类里按表键猜，少一处静默写错字的可能。</para>
    /// </summary>
    protected int PointCodeRegister { get; }

    /// <summary>
    /// 把当前点码同步到 PLC（本页自己的寄存器，0 基：第一个点 = 0）。
    /// <para>进入点表页时调用一次——选中序号没变化不会触发 <c>On选中序号Changed</c>，
    /// 但 PLC 侧需要知道"操作员现在回到点表了"。</para>
    /// </summary>
    private Task SyncCurrentPointNumberAsync() => _pageContext.WritePointNumberAsync(PointCodeRegister, SelectedIndex);

    // ── 辅助地址 ────────────────────────────────────────────────────────

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

    // 辅助地址的写入不再有按钮：填完回车 / 失焦即写（见 提交辅助地址编辑 与 输入提交手势）。

    // ── 地址编辑（仅管理员） ────────────────────────────────────────────

    /// <summary>
    /// 编辑本页所有与 PLC 交互的元件地址（点表列 / 辅助地址 / 动作按钮 / 点动 / 回零）。
    /// </summary>
    [RelayCommand]
    private void EditAddressAsync()
    {
        if (!EditableAddress)
        {
            _dialog.Hint("权限不足", "仅管理员可编辑地址，请联系设备管理员。");
            return;
        }

        SafeInvokeAsync.FireAndForget(EditAddressCoreAsync, _log, "编辑页面地址");
    }

    private async Task EditAddressCoreAsync()
    {
        var Request = new AddressEditRequest
        {
            Title = TableDefinition.DisplayName,
            DefaultGroup = Group,
            ItemCollection = PageAddressService.CollectSetPointTablePage(TableKey).ToList(),
        };

        if (!_dialog.EditPageAddress(Request))
        {
            return;
        }

        IsBusy = true;

        try
        {
            if (await UiCommand
                    .ExecuteAsync(_log, _dialog, "保存", () => PageAddressService.SavePointTablePageAsync(TableKey, Request))
                    .ConfigureAwait(true))
            {
                AtUiThreadExecute(() =>
                {
                    RebuildAuxAddress();
                    RebuildActionButton();
                    SyncInstructionCodeDownPull();   // 指令码列基址可能刚被改过：地址提示跟着更新
                    _dialog.Hint("已保存", "页面元件地址已保存并立即生效。");
                });

                // 状态灯地址可能刚被改过：立刻按新地址回读一次，改完即见效果（不必离开页面或重启）
                SafeInvokeAsync.FireAndForget(RefreshStatusLampAsync, _log, "按新地址刷新回原状态灯");

                // 动作按钮（含固定「参数模式」）刚被重建、状态都复位成未激活：
                // 立即回读一次，避免最长 2 秒内按钮显示"默认参数"而 PLC 其实是"独立参数"
                SafeInvokeAsync.FireAndForget(RefreshActionButtonStateAsync, _log, "按新地址回读动作按钮状态");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }
}
