using Microsoft.Extensions.Logging;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// <see cref="RecipeService"/> 的「机种区段快照」部分（partial）。
///
/// <para>机种 = D2500–D3999 的寄存器快照：读 PLC 存成机种、把机种写回 PLC、
/// 以及稀疏快照与 Modbus 分块读写之间的转换。
/// 拆出来是因为主文件已越过 400 行规模门禁，而"机种区段 IO"本身是一条独立职责线。</para>
/// </summary>
public sealed partial class RecipeService
{
    // ── 机种区段快照 ────────────────────────────────────────────────────

    /// <inheritdoc />
    public IReadOnlyDictionary<int, int> SectionSnapshot(int RecipeNumber) =>
        _store.Find(RecipeNumber)?.SectionSnapshot ?? new Dictionary<int, int>();

    /// <inheritdoc />
    public async Task SaveMachineModelAsync(int RecipeNumber, CancellationToken cancellationToken = default)
    {
        // 覆盖整个机种的现场数据，属工程师及以上
        _permission.WantRequire(Permission.SwitchRecipe);
        ValidateRecipeNumber(RecipeNumber);

        if (!_client.IsConnected)
        {
            throw new InvalidOperationException("PLC 未连接，无法读取机种数据。");
        }

        var Snapshot = await ReadSectionAsync(cancellationToken).ConfigureAwait(false);

        var data = _store.GetOrCreate(RecipeNumber);
        data.SectionSnapshot = Snapshot;

        await _store.SaveAsync(cancellationToken).ConfigureAwait(false);

        _log.LogInformation(
            "机种 {No} 已保存：D{From}–D{To} 共 {Count} 项非 0 数据",
            RecipeNumber, MachineModelSection.StartAt, MachineModelSection.end, Snapshot.Count);

        recipeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public async Task SendMachineModelAsync(int RecipeNumber, CancellationToken cancellationToken = default)
    {
        _permission.WantRequire(Permission.SwitchRecipe);
        ValidateRecipeNumber(RecipeNumber);

        await SendMachineModelCoreAsync(RecipeNumber, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task WriteMachineModelSnapshotAsync(
        int RecipeNumber, IReadOnlyDictionary<int, int> Snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(Snapshot);

        // CSV 导入会覆盖整机现场数据
        _permission.WantRequire(Permission.SwitchRecipe);
        ValidateRecipeNumber(RecipeNumber);

        var data = _store.GetOrCreate(RecipeNumber);

        data.SectionSnapshot = Snapshot
            .Where(Item => Item.Key >= 0 && Item.Key < MachineModelSection.length && Item.Value != 0)
            .ToDictionary(Item => Item.Key, Item => Item.Value);

        await _store.SaveAsync(cancellationToken).ConfigureAwait(false);

        _log.LogInformation("机种 {No} 的快照已写入（{Count} 项，来自导入）", RecipeNumber, data.SectionSnapshot.Count);
        recipeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>把某机种的快照写入 PLC 的机种区段。</summary>
    /// <param name="RecipeNumber">机种号。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="allowEmptyMachineModel">
    /// 目标机种没有任何数据时是否照常下发（= 机种区段整段清零）。
    /// <para><b>切换机种必须允许</b>（2026-10-21 现场报障：新机种本来就是空的，原来的守卫
    /// 让空机种"切不过去"，界面上只是一句"还没有数据"）；<b>手动「下发机种」仍不允许</b>——
    /// 那等于一声不响把机种区段清空，必须走「切换」这条会弹确认框的路径。</para>
    /// </param>
    private async Task SendMachineModelCoreAsync(
        int RecipeNumber, CancellationToken cancellationToken, bool allowEmptyMachineModel = false)
    {
        if (!_client.IsConnected)
        {
            throw new InvalidOperationException("PLC 未连接，无法下发机种数据。");
        }

        var data = _store.Find(RecipeNumber);
        var isEmptyMachineModel = data is null || data.IsEmpty;

        if (isEmptyMachineModel && !allowEmptyMachineModel)
        {
            throw new InvalidOperationException(
                $"机种 {RecipeNumber} 还没有数据。请先在示教页调好点位，回到本页点「保存机种」。");
        }

        if (isEmptyMachineModel)
        {
            _log.LogWarning(
                "机种 {No} 没有数据，按空机种下发（D{From}–D{To} 整段清零）",
                RecipeNumber, MachineModelSection.StartAt, MachineModelSection.end);
        }

        var Snapshot = data?.SectionSnapshot ?? new Dictionary<int, int>();

        // 先把整段清零：快照是稀疏的，不清零会把上一个机种的残留点位留在 PLC 里
        await FillZeroSectionAsync(cancellationToken).ConfigureAwait(false);
        await WriteSectionAsync(Snapshot, cancellationToken).ConfigureAwait(false);

        // 机种号一并写到 PLC（有些现场从 HMI 侧切完就断电，机种号必须落进 PLC）
        await WriteRegisterAsync(_config.Widget.RecipeNumberAddress, RecipeNumber, cancellationToken).ConfigureAwait(false);

        _log.LogInformation("机种 {No} 的区段快照已下发（{Count} 项）", RecipeNumber, Snapshot.Count);
    }

    /// <summary>读 PLC 的整个机种区段（分块读，返回稀疏快照）。</summary>
    private async Task<Dictionary<int, int>> ReadSectionAsync(CancellationToken cancellationToken)
    {
        var Snapshot = new Dictionary<int, int>();

        for (var Offset = 0; Offset < MachineModelSection.length; Offset += sectionBlockSize)
        {
            var thisBlock = Math.Min(sectionBlockSize, MachineModelSection.length - Offset);

            var registerList = await _client
                .ReadHoldingRegisterAsync(MachineModelSection.StartAt + Offset, thisBlock, cancellationToken)
                .ConfigureAwait(false);

            if (registerList.Length < thisBlock)
            {
                throw new InvalidOperationException(
                    $"从 D{MachineModelSection.StartAt + Offset} 读取 {thisBlock} 个字时 PLC 只返回 {registerList.Length} 个，已中止。");
            }

            for (var ordinal = 0; ordinal < thisBlock; ordinal++)
            {
                if (registerList[ordinal] != 0)
                {
                    Snapshot[Offset + ordinal] = registerList[ordinal];
                }
            }
        }

        return Snapshot;
    }

    /// <summary>把稀疏快照写进 PLC（只写有值的字，按相邻段合并成块写）。</summary>
    private async Task WriteSectionAsync(IReadOnlyDictionary<int, int> Snapshot, CancellationToken cancellationToken)
    {
        foreach (var block in SplitBlock(Snapshot))
        {
            await _client
                .WriteMultiRegisterAsync(
                    MachineModelSection.StartAt + block.startOffset,
                    Enumerable.Range(0, block.Value.Length).Select(ordinal => (ushort)block.Value[ordinal]).ToArray(),
                    cancellationToken)
                .ConfigureAwait(false);

            await Task.Delay(writeInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>把整个机种区段清零（下发前先清残留）。</summary>
    private async Task FillZeroSectionAsync(CancellationToken cancellationToken)
    {
        var zero = new ushort[sectionBlockSize];

        for (var Offset = 0; Offset < MachineModelSection.length; Offset += sectionBlockSize)
        {
            var thisBlock = Math.Min(sectionBlockSize, MachineModelSection.length - Offset);
            var ValueList = thisBlock == sectionBlockSize ? zero : new ushort[thisBlock];

            await _client
                .WriteMultiRegisterAsync(MachineModelSection.StartAt + Offset, ValueList, cancellationToken)
                .ConfigureAwait(false);

            await Task.Delay(writeInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 把稀疏快照切成"连续的、长度 ≤ 区段块大小"的块。
    /// <para>逐字写 1001 次会慢到不可接受；按空洞切块既快又不会写到不该写的字。</para>
    /// </summary>
    private static IEnumerable<(int startOffset, int[] Value)> SplitBlock(IReadOnlyDictionary<int, int> Snapshot)
    {
        var hasIndex = Snapshot.OrderBy(Item => Item.Key).ToList();
        var ordinal = 0;

        while (ordinal < hasIndex.Count)
        {
            var startOffset = hasIndex[ordinal].Key;
            var length = 1;

            while (ordinal + length < hasIndex.Count
                   && length < sectionBlockSize
                   && hasIndex[ordinal + length].Key == startOffset + length)
            {
                length++;
            }

            var ValueList = new int[length];

            for (var item = 0; item < length; item++)
            {
                ValueList[item] = hasIndex[ordinal + item].Value;
            }

            yield return (startOffset, ValueList);
            ordinal += length;
        }
    }

}
