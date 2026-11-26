using System.Globalization;
using Microsoft.Extensions.Logging;
using PlcHmi.Communication.Modbus;
using PlcHmi.Communication.Recipe;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// <see cref="PointTableService"/> 的「按机种快照装载 / 并入」部分（partial）。
///
/// <para><b>为什么不再有 point-tables.json：</b>现场口径是"<b>D2500–D3999 整段都是机种区域</b>"。
/// 点表数据既然整段属于机种，就不该再另存一份全局文件——那会变成两个真相源，
/// 切机种时到底听谁的都说不清。现在：</para>
/// <list type="bullet">
/// <item>PLC 的 D2500–D3999 = 当前机种生效的一份（运行时真相）；</item>
/// <item>机种快照（<c>recipes.json</c>）= 持久化真相，<b>由「下发点表」/「坐标示教」写完后自动并入</b>，
/// 也可在机种页「保存机种」从 PLC 整段读回。</item>
/// </list>
///
/// <para>页面装载是"从当前机种快照解码"，离线也能看到上次保存的数据。
/// <b>2026-10-16 修复：</b>此前下发点表只写 PLC 不动快照，于是"下发 → 切页 → 回来"看到的
/// 还是快照里的旧数据（必须先去机种页点「保存机种」才显形）——现场表现为"下发白做了"。</para>
/// </summary>
public sealed partial class PointTableService
{
    /// <summary>用当前机种的区段快照填充一张点表的内存行。</summary>
    private void SlaveStationKindSnapshotFill(PointTableDefinition TableEntry)
    {
        var Snapshot = _recipe.SectionSnapshot(_recipe.CurrentRecipeNumber);
        var rowList = _bufferStore[TableEntry.Key];
        var mapItem = _map[TableEntry.Key];

        foreach (var rowItem in rowList)
        {
            rowItem.Value.Clear();
        }

        foreach (var columnItem in TableEntry.Column)
        {
            if (columnItem.BaseAddress <= 0)
            {
                continue;
            }

            var wordBuffer = new ushort[columnItem.WordPerPointBit];

            for (var Sequence = 0; Sequence < mapItem.PointCount && Sequence < rowList.Count; Sequence++)
            {
                for (var ordinal = 0; ordinal < columnItem.WordPerPointBit; ordinal++)
                {
                    var Address = columnItem.BaseAddress + Sequence * columnItem.WordPerPointBit + ordinal;

                    // 快照是稀疏的：没记录的字就是 0（与 PLC 里的实际读回一致）
                    wordBuffer[ordinal] = Snapshot.TryGetValue(Address - MachineModelSection.StartAt, out var Value)
                        ? (ushort)Value
                        : (ushort)0;
                }

                var decodeValue = RegisterCodec.Decode(columnItem.DataType, wordBuffer, WordOrder.LittleEndian);

                rowList[Sequence].setup(columnItem.Key, Convert.ToDouble(decodeValue, CultureInfo.InvariantCulture));
            }
        }
    }

    /// <summary>
    /// 把刚写进 PLC 的点表数据（指定序号集合）<b>并入当前机种快照</b>并落盘。
    ///
    /// <para><b>为什么必须并入（2026-10-16 现场问题）：</b>页面重新进入时是从
    /// <b>机种快照</b>装载的，而"下发点表 / 坐标示教"只写 PLC。两者不同步时，
    /// 操作员一下发就切页、再回来，看到的仍是快照里的旧数据（表现为"下发白做了"），
    /// 只能绕道机种页点「保存机种」整段读回才显形。</para>
    ///
    /// <para><b>为什么是并入而不是整段回读：</b>机种区段（D2500–D3999）里除了点表列
    /// 还有机种参数等别的数据。整段回读会把 PLC 里的旧值盖到那些本地未下发的数据上
    /// （操作员没下发的编辑会凭空消失）；只并入<b>本表各列覆盖到的字</b>，
    /// 既让"PLC 里刚写下的 = 快照里存着的"，又不动其它区段。</para>
    ///
    /// <para>值 0 也会并入：<c>异步写入机种快照</c> 按稀疏约定丢弃 0，于是被清空的点
    /// 在快照里也回到"未示教"——与 <see cref="SlaveStationKindSnapshotFill"/> 读不到即 0 对称。</para>
    /// </summary>
    private async Task AndInMachineModelSnapshotAsync(
        PointTableDefinition TableEntry,
        IReadOnlyList<int> indexCollection,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? onlyColumnKey = null)
    {
        var mapItem = _map[TableEntry.Key];
        var Number = _recipe.CurrentRecipeNumber;

        // 取现有快照的副本再改：整段替换由 异步写入机种快照 负责落盘
        var Snapshot = new Dictionary<int, int>(_recipe.SectionSnapshot(Number));
        var andInWordCount = 0;

        foreach (var Sequence in indexCollection)
        {
            if (!mapItem.IndexValid(Sequence))
            {
                continue;
            }

            var rowItem = _bufferStore[TableEntry.Key][Sequence];

            foreach (var columnItem in TableEntry.Column)
            {
                if (columnItem.BaseAddress <= 0)
                {
                    continue;
                }

                // 只并入指定列（指令码下拉走这条）：其余列可能还是"改了没下发"的内存值
                if (onlyColumnKey is not null
                    && !onlyColumnKey.Contains(columnItem.Key, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var registerList = RegisterCodec.Encode(
                    columnItem.DataType, rowItem.Take(columnItem.Key), WordOrder.LittleEndian);

                for (var ordinal = 0; ordinal < columnItem.WordPerPointBit && ordinal < registerList.Length; ordinal++)
                {
                    var Address = columnItem.BaseAddress + Sequence * columnItem.WordPerPointBit + ordinal;

                    // 只并入落在机种区段内的字：区段外的列不属于机种（防御性跳过）
                    if (!MachineModelSection.Contains(Address))
                    {
                        continue;
                    }

                    Snapshot[Address - MachineModelSection.StartAt] = registerList[ordinal];
                    andInWordCount++;
                }
            }
        }

        await _recipe.WriteMachineModelSnapshotAsync(Number, Snapshot, cancellationToken).ConfigureAwait(false);

        _log.LogInformation(
            "点表 {Table} 的 {Count} 个字已并入机种 {No} 的快照（页面重新进入即可见）",
            TableEntry.DisplayName, andInWordCount, Number);
    }
}
