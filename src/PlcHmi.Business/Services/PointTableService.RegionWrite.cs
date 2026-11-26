using Microsoft.Extensions.Logging;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// <see cref="PointTableService"/> 的「参数写入」部分（partial，2026-10-21 现场要求）。
///
/// <para><b>为什么不是"只下发点表"：</b>现场在示教页调的不只是 X/Y/Z，还有指令码、
/// 单点工艺参数（结构体数组）等，它们都落在机种区段 D2500–D3999 里。
/// 一个个按「下发点表」+ 机种页「保存机种」两步走，实际使用中总有人漏掉第二步
/// （切页回来数据没了）。现在一颗「参数写入」= 整段写 PLC + 存机种。</para>
///
/// <para><b>顺序不能颠倒：</b>先把本页点表内存并入快照，再整段下发；
/// 否则"刚在界面上改的坐标"不在快照里，整段写下去反而把旧值推回 PLC。</para>
/// </summary>
public sealed partial class PointTableService
{
    /// <inheritdoc />
    public async Task ParameterWriteAsync(string Key, CancellationToken cancellationToken = default)
    {
        _permission.WantRequire(Permission.EditTeach);

        var TableEntry = Table(Key);
        var mapItem = _map[TableEntry.Key];

        if (!_client.IsConnected)
        {
            throw new InvalidOperationException("PLC 未连接，无法写入参数。");
        }

        var Number = _recipe.CurrentRecipeNumber;

        // ① 本页点表的最新编辑先并入当前机种快照（只并入本表各列，不动别的元件值）
        await AndInMachineModelSnapshotAsync(
                TableEntry, Enumerable.Range(0, mapItem.PointCount).ToList(), cancellationToken)
            .ConfigureAwait(false);

        // ② 整段写入 PLC：先清零、再写快照、并写机种号（机种区段的唯一写入口）
        await _recipe.SendMachineModelAsync(Number, cancellationToken).ConfigureAwait(false);

        // ③ 触发机种保存：把 PLC 里刚写下的整段读回并存成当前机种
        //    （与机种页「保存机种」同一条路径，保证"PLC 里刚写下的 = recipes.json 里存着的"）
        await _recipe.SaveMachineModelAsync(Number, cancellationToken).ConfigureAwait(false);

        _log.LogInformation(
            "参数写入完成：机种 {No} 的 D{From}–D{To} 已整段写入 PLC 并保存（本页 {Table}）",
            Number, MachineModelSection.StartAt, MachineModelSection.end, TableEntry.DisplayName);
    }
}
