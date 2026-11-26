using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PlcHmi.Core.Models;

namespace PlcHmi.App.ViewModels;

/// <summary>
/// 参数设置页 ViewModel 的「延时表 / 按钮表管理」部分（partial）。
///
/// <para><b>为什么单独一个文件：</b>增删改条目与按钮置位/复位是同一类操作
/// （都对参数表做写操作、都需要权限与忙碌态），放一处便于整体阅读；
/// 主文件已接近项目规模门禁。</para>
/// </summary>
public sealed partial class ParameterViewModel
{
    // ── 延时表管理（仅管理员） ─────────────────────────────────────────

    [RelayCommand]
    private Task AddDelayAsync() => AddEntryAsync(ParameterTableCategory.Delay, "延时");

    [RelayCommand]
    private Task EditDelayAsync(ParameterEntryRowViewModel? rowItem) =>
        EditEntryAsync(ParameterTableCategory.Delay, rowItem, "延时");

    [RelayCommand]
    private Task RemoveDelayAsync(ParameterEntryRowViewModel? rowItem) =>
        RemoveEntryAsync(ParameterTableCategory.Delay, rowItem, "延时");

    // ── 按钮表管理（仅管理员） ─────────────────────────────────────────

    [RelayCommand]
    private Task AddButtonAsync() => AddEntryAsync(ParameterTableCategory.Button, "按钮");

    [RelayCommand]
    private Task EditButtonAsync(ParameterEntryRowViewModel? rowItem) =>
        EditEntryAsync(ParameterTableCategory.Button, rowItem, "按钮");

    [RelayCommand]
    private Task RemoveButtonAsync(ParameterEntryRowViewModel? rowItem) =>
        RemoveEntryAsync(ParameterTableCategory.Button, rowItem, "按钮");

    // ── 按钮置位 / 复位（工程师及以上） ─────────────────────────────────

    /// <summary>把按钮点位置位（开启）。即时写 M 区。</summary>
    [RelayCommand]
    private Task SetButtonAsOpenAsync(ParameterEntryRowViewModel? rowItem) => InnerPartSetButtonAsync(rowItem, true);

    /// <summary>把按钮点位复位（关闭）。即时写 M 区。</summary>
    [RelayCommand]
    private Task SetButtonAsCloseAsync(ParameterEntryRowViewModel? rowItem) => InnerPartSetButtonAsync(rowItem, false);

    private async Task InnerPartSetButtonAsync(ParameterEntryRowViewModel? rowItem, bool on)
    {
        if (rowItem is null)
        {
            return;
        }

        if (!OperableValueList)
        {
            _dialog.Hint("权限不足", "只有工程师及以上可以操作按钮的置位与复位。");
            return;
        }

        if (!rowItem.Definition.Enabled)
        {
            _dialog.Hint("条目已停用", $"「{rowItem.Name}」已停用，请先在编辑里启用后再操作。");
            return;
        }

        try
        {
            IsBusy = true;
            await _dataGrid.SetButtonAsync(rowItem.Definition, on).ConfigureAwait(false);
            rowItem.MarkButtonState(on);

            StateText = $"「{rowItem.Name}」已{(on ? "置位" : "复位")}（{rowItem.Address}）";
            _log.LogInformation("按钮「{Name}」{Action}", rowItem.Name, on ? "置位" : "复位");
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "按钮「{Name}」{Action}失败", rowItem.Name, on ? "置位" : "复位");
            _dialog.ErrorInfo("操作失败", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
