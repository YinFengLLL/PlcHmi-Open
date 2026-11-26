using Microsoft.Extensions.Logging;
using PlcHmi.Communication.Modbus;
using PlcHmi.Communication.Recipe;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// <see cref="PointTableService"/> 的「指令码」部分（partial）。
///
/// <para><b>2026-10-21 现场要求：</b>点胶示教页的指令码改成下拉选单（00 结束 … 09 对针），
/// 选中即把码值写进该点的指令码字（第 n 点 = <c>D3400 + n - 1</c>，例：第 2 点选「直线」→ D3401 = 2）。
/// 这条路径与「下发点表」的区别是<b>只动指令码这一个字</b>：
/// 不会把尚未示教的坐标（0）带下 PLC，也不会校验坐标范围。</para>
///
/// <para><b>写入顺序（先 PLC、后内存）：</b>PLC 写失败时内存与下拉框都不改，
/// 界面显示与 PLC 实际值保持一致——工控界面上"看起来选好了、其实没写进去"是最坏的假象。</para>
/// </summary>
public sealed partial class PointTableService
{
    /// <inheritdoc />
    public async Task WriteInstructionCodeAsync(string Key, int Sequence, int code, CancellationToken cancellationToken = default)
    {
        _permission.WantRequire(Permission.EditTeach);

        var TableEntry = Table(Key);
        var mapItem = _map[TableEntry.Key];

        if (!mapItem.IndexValid(Sequence))
        {
            throw new ArgumentOutOfRangeException(nameof(Sequence), Sequence, "点序号超出范围。");
        }

        var columnItem = TableEntry.FindColumn(PointTableDefinition.instructionCodeColumnKey)
            ?? throw new InvalidOperationException(
                $"点表「{TableEntry.DisplayName}」没有指令码列，无法写入指令码。");

        if (!_client.IsConnected)
        {
            throw new InvalidOperationException("PLC 未连接，无法写入指令码。");
        }

        var addressValue = mapItem.Address(Sequence, columnItem.Key);
        var registerList = RegisterCodec.Encode(columnItem.DataType, code, WordOrder.LittleEndian);

        await _client
            .WriteMultiRegisterAsync(addressValue, registerList, cancellationToken)
            .ConfigureAwait(false);

        SetPointValue(Key, Sequence, columnItem.Key, code);

        // 只并入指令码这一列：操作员可能刚在界面上改了坐标还没下发，
        // 顺手并入会把"没下发的编辑"存进机种快照，下次进页面看起来像是已下发（误导现场）
        await AndInMachineModelSnapshotAsync(TableEntry, [Sequence], cancellationToken, [columnItem.Key]).ConfigureAwait(false);

        _log.LogInformation(
            "{Table} 第 {Point} 点指令码写入 {Code}（D{Address}）",
            TableEntry.DisplayName, Sequence + 1, code, addressValue);
    }
}
