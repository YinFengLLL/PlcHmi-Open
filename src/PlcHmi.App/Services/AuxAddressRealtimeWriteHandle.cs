using Microsoft.Extensions.Logging;
using PlcHmi.App.ViewModels;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Models;

namespace PlcHmi.App.Services;

/// <summary>
/// 辅助地址「实时读写」的统一实现（2026-11-12 现场要求，锁附示教 / 点胶示教 / 标定页共用）。
///
/// <para><b>两条规矩：</b></para>
/// <list type="number">
///   <item>页面上看到的值就是 PLC 现值——页面停留期间跟随周期回读（由各页把
///   <see cref="RefreshValueAsync"/> 挂在 2s 回读节拍里），不再需要按「读取」；</item>
///   <item>改完就生效——输入框回车 / 失焦、勾选框变化即写 PLC，不再有「写入」按钮。</item>
/// </list>
///
/// <para><b>写入门槛是手动使能</b>：辅助地址是工艺参数，随手改一个数就可能是撞机 / 撞针。
/// 未使能时行置灰、真要写也会被这里拦下并把界面拉回 PLC 真实值。</para>
/// </summary>
public sealed class AuxAddressRealtimeWriteHandle
{
    private readonly ILogger _log;
    private readonly IDialogService _dialog;
    private readonly Func<bool> _allowWrite;

    public AuxAddressRealtimeWriteHandle(ILogger Log, IDialogService Dialog, Func<bool> AllowWrite)
    {
        _log = Log;
        _dialog = Dialog;
        _allowWrite = AllowWrite;
    }

    /// <summary>按配置创建一行（构造与改地址后重建走同一条路，不会出现"重建出来的行改不了"）。</summary>
    public AuxAddressRowViewModel Create(AuxAddressItem Item, IPageAddressService Service)
    {
        ArgumentNullException.ThrowIfNull(Item);
        ArgumentNullException.ThrowIfNull(Service);

        return new AuxAddressRowViewModel(Item, Service)
        {
            Editable = _allowWrite(),
            SubmitRequest = Submit,
        };
    }

    /// <summary>手动使能变化后同步各行的可编辑状态。</summary>
    public void RefreshEditable(IEnumerable<AuxAddressRowViewModel> RowCollection)
    {
        foreach (var rowItem in RowCollection)
        {
            rowItem.Editable = _allowWrite();
        }
    }

    /// <summary>
    /// 周期回读各行的显示值。
    /// <para><b>跳过正在输入的行</b>：否则操作员输到一半，PLC 的旧值会把输入框覆盖掉。
    /// 单项容错：某行地址配错 / 读失败只跳过它，不打断整页的周期回读。</para>
    /// </summary>
    public Task RefreshValueAsync(IEnumerable<AuxAddressRowViewModel> RowCollection) =>
        // 2026-11-19 性能改造：一行一个 Modbus 请求 → 连续地址合并成 2~4 个请求。
        // 批量实现仍然逐行跳过"正在输入"的行、单项容错，语义与逐行读一致；
        // 上限提到 30 行后这是保住快组 100ms 采集周期的关键。
        AuxAddressRowViewModel.BatchRefreshAsync(RowCollection as IReadOnlyList<AuxAddressRowViewModel> ?? RowCollection.ToList());

    private void Submit(AuxAddressRowViewModel rowItem)
    {
        if (!_allowWrite())
        {
            _dialog.Hint("需要手动使能",
                $"修改「{rowItem.Name}」的当前值前，请先打开手动使能。");

            // 界面上这半截改动不能留着：看着像改成功了，PLC 里其实没变
            SafeInvokeAsync.FireAndForget(rowItem.RefreshShowAsync, _log, $"回读辅助地址「{rowItem.Name}」");
            return;
        }

        SafeInvokeAsync.FireAndForget(
            () => WriteCoreAsync(rowItem),
            _log,
            $"写入辅助地址「{rowItem.Name}」");
    }

    private async Task WriteCoreAsync(AuxAddressRowViewModel rowItem)
    {
        Func<Task> Command = rowItem.IsSwitch ? rowItem.WriteSwitchAsync : rowItem.WriteAsync;

        if (await UiCommand
                .ExecuteAsync(_log, _dialog, $"写入「{rowItem.Name}」", Command)
                .ConfigureAwait(true))
        {
            _log.LogInformation("辅助地址「{Name}」（{Address}）已实时写入 PLC",
                rowItem.Name, rowItem.AddressText);
            return;
        }

        // 失败（数字非法 / 通信异常 / 权限不足）：界面拉回 PLC 的真实值
        SafeInvokeAsync.FireAndForget(rowItem.RefreshShowAsync, _log, $"回读辅助地址「{rowItem.Name}」");
    }
}
