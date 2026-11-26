using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PlcHmi.Core.Models;

namespace PlcHmi.App.ViewModels;

/// <summary>
/// 参数设置页 ViewModel 的「轴地址」部分（partial）。
///
/// <para><b>为什么单独一个文件：</b>地址的合成、校验、保存与"有没有未保存改动"是同一件事，
/// 放一处便于一次看全；主文件本身也接近规模门禁。</para>
///
/// <para><b>唯一提交出口：</b>地址栏是"编辑中即改内存"的草稿，服务层才是真相。
/// 按「保存」或「保存轴地址」提交，按「读取」会恢复成配置文件里的值
/// （有未保存改动时会先确认，不静默回退）。</para>
/// </summary>
public sealed partial class ParameterViewModel
{
    /// <summary>为指定轴的定位速度选择变量：选中后地址与变量名一并回填行模型。</summary>
    [RelayCommand]
    private void SelectPositioningSpeedVariable(object? rowParameter)
    {
        if (rowParameter is not AxisSpeedRowViewModel rowItem)
        {
            return;
        }

        if (_dialog.SelectVariable($"为 {rowItem.Name} 定位速度选择变量") is not { } Entry)
        {
            return;
        }

        rowItem.VariableText = Entry.VariableName;
        rowItem.AddressText = Entry.SoftElementAddress;
        StateText = $"「{rowItem.Name}」定位速度已绑定变量 {Entry.VariableName}，保存轴地址后生效";
    }

    /// <summary>
    /// 保存轴地址。把「轴速度」表的地址列与「轴地址」表的五个字段合成一份候选表，
    /// 交给服务层统一校验——页面自己写配置会绕过区间与重叠检查。
    /// <para>保存成功后服务会重建采集计划，新地址立即生效，无需重启程序。</para>
    /// </summary>
    [RelayCommand]
    private async Task SaveAxisAddressAsync()
    {
        if (!EditableTable)
        {
            _dialog.Hint("权限不足", "只有管理员可以修改轴地址。");
            return;
        }

        var candidate = BuildAxisAddressCandidate();
        var validateItem = _axisAddress.Validate(candidate);

        if (!validateItem.IsValid)
        {
            _log.LogWarning("轴地址校验未通过，保存被拒绝：{Errors}", validateItem.Summary);
            _dialog.ErrorInfo("轴地址校验未通过", string.Join(Environment.NewLine, validateItem.ErrorCollection));
            return;
        }

        if (!_dialog.Confirm("保存轴地址",
                "确认保存这些地址？\n\n"
                + "保存后会立即重建采集计划。请先确认 PLC 侧的读取地址已同步改好，"
                + "否则采集到的会是错位的数据。"))
        {
            return;
        }

        try
        {
            IsBusy = true;

            await _axisAddress.SaveAsync(candidate).ConfigureAwait(false);

            AtUiThreadExecute(() =>
            {
                StateText = "轴地址已保存并生效（采集计划已重建，无需重启）";
                RefreshAll();
            });
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "保存轴地址失败");
            AtUiThreadExecute(() => _dialog.ErrorInfo("保存失败", exception.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>把「轴速度」表的地址列与「轴地址」表的五个字段合成一份候选表。</summary>
    private AxisAddressTable BuildAxisAddressCandidate()
    {
        var candidate = _axisAddress.DataGrid.Clone();

        foreach (var rowItem in AxisAddressCollection)
        {
            if (candidate.Find(rowItem.Axis) is { } EntryItem)
            {
                rowItem.App(EntryItem);
            }
        }

        // 定位速度地址的唯一编辑入口在上方「轴速度」表
        foreach (var rowItem in AxisSpeed)
        {
            if (candidate.Find(rowItem.AxisKey) is { } EntryItem)
            {
                EntryItem.PositioningSpeedAddress = rowItem.AddressText.Trim();
                EntryItem.PositioningSpeedVariable = rowItem.VariableText.Trim();
            }
        }

        return candidate;
    }

    /// <summary>
    /// 界面上的地址是否有未保存的改动（与服务层的当前快照逐项比对）。
    ///
    /// <para>比较前先规范化：<c>1252</c> 与 <c>D1252</c> 是同一个地址，不该因为写法差异
    /// 被判成"有改动"，否则每次点「读取」都会弹一次无谓的确认。</para>
    /// </summary>
    private bool AddressHasChangeMove()
    {
        var Snapshot = _axisAddress.DataGrid;
        var candidate = BuildAxisAddressCandidate();

        return candidate.AxisList.Any(Item =>
        {
            var origin = Snapshot.Find(Item.Axis);
            return origin is null
                   || AddressDifferent(Item.PositioningSpeedAddress, origin.PositioningSpeedAddress)
                   || AddressDifferent(Item.PositionAddress, origin.PositionAddress)
                   || AddressDifferent(Item.JogForwardAddress, origin.JogForwardAddress)
                   || AddressDifferent(Item.JogReverseAddress, origin.JogReverseAddress)
                   || AddressDifferent(Item.JogSpeedLevelAddress, origin.JogSpeedLevelAddress)
                   || AddressDifferent(Item.HomeAddress, origin.HomeAddress);
        });
    }

    /// <summary>两个地址是否不同（忽略大小写、首尾空白与省略的 D 前缀）。</summary>
    private static bool AddressDifferent(string currentValue, string OriginValue) =>
        !string.Equals(RuleRangeConvert(currentValue), RuleRangeConvert(OriginValue), StringComparison.OrdinalIgnoreCase);

    /// <summary>把 <c>1252</c> 这类省前缀写法补成 <c>D1252</c>，便于比较（不改动真实地址）。</summary>
    private static string RuleRangeConvert(string addressValue)
    {
        var Text = addressValue.Trim();
        return Text.Length > 0 && Text.All(char.IsAsciiDigit) ? "D" + Text : Text;
    }
}
