using CommunityToolkit.Mvvm.ComponentModel;
using PlcHmi.App.Services;
using PlcHmi.Core.Models;

namespace PlcHmi.App.ViewModels;

/// <summary>
/// <see cref="PointTableTeachViewModel"/> 的「指令码」部分（partial）。
///
/// <para><b>2026-10-21 现场要求：</b>点胶示教页的指令码不再手填数字，改为<b>辅助地址区首行的下拉选单</b>：
/// 选中一个点后，在 00 结束 … 09 对针 之间选择，选中即把码值写进该点的指令码字
/// （第 2 点选「直线」→ <c>D3401 = 2</c>），点表的指令码列显示对应文字。</para>
///
/// <para><b>锁附页没有指令码列</b>（<see cref="PointTableDefinition.HasInstructionCode"/> 为 false），下拉框整块隐藏，
/// 不影响锁附示教的现有布局。</para>
/// </summary>
public abstract partial class PointTableTeachViewModel
{
    /// <summary>正在把内存值同步到下拉框（用于避免"同步"被当成"用户选择"而反过来写 PLC）。</summary>
    private bool _instructionCodeSync;

    /// <summary>本页点表是否含指令码列（决定下拉框是否出现）。</summary>
    public bool HasInstructionCode => TableDefinition.HasInstructionCode;

    /// <summary>指令码下拉选项（00 结束 … 09 对针）。</summary>
    public IReadOnlyList<InstructionCodeItem> InstructionCodeOptions => InstructionCodeDefinition.All;

    /// <summary>当前点选中的指令码。</summary>
    [ObservableProperty] private InstructionCodeItem? _selectedInstructionCodeItem;

    /// <summary>当前点的指令码要写到哪个寄存器（如「第 2 点 · 写入 D3401」）。</summary>
    [ObservableProperty] private string _instructionCodeAddressText = string.Empty;

    partial void OnSelectedInstructionCodeItemChanged(InstructionCodeItem? value)
    {
        if (_instructionCodeSync || value is null)
        {
            return;
        }

        SafeInvokeAsync.FireAndForget(() => WriteInstructionCodeCoreAsync(value), _log, $"写入指令码 {value.Show}");
    }

    /// <summary>
    /// 把「当前点 + 下拉框」对齐到点表里的真实值（切点、清空、下发后都走这里）。
    /// <para>同步期间置 <see cref="_instructionCodeSync"/>：否则设置下拉选中项会被当成用户操作，
    /// 反过来再写一次 PLC（切点时表现为"每切一点就无谓写一次"）。</para>
    /// </summary>
    protected void SyncInstructionCodeDownPull()
    {
        if (!HasInstructionCode)
        {
            return;
        }

        var columnItem = TableDefinition.FindColumn(PointTableDefinition.instructionCodeColumnKey);
        var rowList = PointTableService.CurrentPointTable(TableKey);

        var Value = SelectedIndex >= 0 && SelectedIndex < rowList.Count
            ? rowList[SelectedIndex].Take(PointTableDefinition.instructionCodeColumnKey)
            : 0;

        _instructionCodeSync = true;

        try
        {
            SelectedInstructionCodeItem = InstructionCodeDefinition.Find((int)Math.Round(Value));

            InstructionCodeAddressText = columnItem is null
                ? string.Empty
                : $"第 {SelectedIndex + 1} 点 · 写入 D{columnItem.BaseAddress + SelectedIndex * columnItem.WordPerPointBit}";
        }
        finally
        {
            _instructionCodeSync = false;
        }
    }

    /// <summary>把选中的指令码写给当前点（失败则回滚下拉框显示，不假装写成功）。</summary>
    private async Task WriteInstructionCodeCoreAsync(InstructionCodeItem Item)
    {
        // 先取当前点：写入是异步的，期间操作员可能已经切到别的点
        var Sequence = SelectedIndex;

        IsBusy = true;

        try
        {
            var Success = await UiCommand
                .ExecuteAsync(
                    _log,
                    _dialog,
                    $"写入指令码「{Item.Name}」",
                    () => PointTableService.WriteInstructionCodeAsync(TableKey, Sequence, Item.code),
                    $"第 {Sequence + 1} 点")
                .ConfigureAwait(true);

            if (!Success)
            {
                SyncInstructionCodeDownPull();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }
}
