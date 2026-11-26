using Microsoft.Extensions.Logging;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// <see cref="配方服务"/> 的「机种区段快照」部分（partial）。
///
/// <para>机种 = D2500–D3999 的寄存器快照：读 PLC 存成机种、把机种写回 PLC、
/// 以及稀疏快照与 Modbus 分块读写之间的转换。
/// 拆出来是因为主文件已越过 400 行规模门禁，而"机种区段 IO"本身是一条独立职责线。</para>
/// </summary>
public sealed partial class 配方服务
{
    // ── 机种区段快照 ────────────────────────────────────────────────────

    /// <inheritdoc />
    public IReadOnlyDictionary<int, int> 区段快照(int 配方号) =>
        _存储.查找(配方号)?.区段快照 ?? new Dictionary<int, int>();

    /// <inheritdoc />
    public async Task 异步保存机种(int 配方号, CancellationToken 取消令牌 = default)
    {
        // 覆盖整个机种的现场数据，属工程师及以上
        _权限.要求(权限.切换配方);
        校验配方号(配方号);

        if (!_客户端.是否已连接)
        {
            throw new InvalidOperationException("PLC 未连接，无法读取机种数据。");
        }

        var 快照 = await 异步读取区段(取消令牌).ConfigureAwait(false);

        var 数据 = _存储.获取或创建(配方号);
        数据.区段快照 = 快照;

        await _存储.异步保存(取消令牌).ConfigureAwait(false);

        _日志.LogInformation(
            "机种 {No} 已保存：D{From}–D{To} 共 {Count} 项非 0 数据",
            配方号, 机种区段.起始, 机种区段.结束, 快照.Count);

        配方已变更?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public async Task 异步下发机种(int 配方号, CancellationToken 取消令牌 = default)
    {
        _权限.要求(权限.切换配方);
        校验配方号(配方号);

        await 异步下发机种核心(配方号, 取消令牌).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task 异步写入机种快照(
        int 配方号, IReadOnlyDictionary<int, int> 快照, CancellationToken 取消令牌 = default)
    {
        ArgumentNullException.ThrowIfNull(快照);

        // CSV 导入会覆盖整机现场数据
        _权限.要求(权限.切换配方);
        校验配方号(配方号);

        var 数据 = _存储.获取或创建(配方号);

        数据.区段快照 = 快照
            .Where(项 => 项.Key >= 0 && 项.Key < 机种区段.长度 && 项.Value != 0)
            .ToDictionary(项 => 项.Key, 项 => 项.Value);

        await _存储.异步保存(取消令牌).ConfigureAwait(false);

        _日志.LogInformation("机种 {No} 的快照已写入（{Count} 项，来自导入）", 配方号, 数据.区段快照.Count);
        配方已变更?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>把某机种的快照写入 PLC 的机种区段。</summary>
    /// <param name="配方号">机种号。</param>
    /// <param name="取消令牌">取消令牌。</param>
    /// <param name="允许空机种">
    /// 目标机种没有任何数据时是否照常下发（= 机种区段整段清零）。
    /// <para><b>切换机种必须允许</b>（2026-10-21 现场报障：新机种本来就是空的，原来的守卫
    /// 让空机种"切不过去"，界面上只是一句"还没有数据"）；<b>手动「下发机种」仍不允许</b>——
    /// 那等于一声不响把机种区段清空，必须走「切换」这条会弹确认框的路径。</para>
    /// </param>
    private async Task 异步下发机种核心(
        int 配方号, CancellationToken 取消令牌, bool 允许空机种 = false)
    {
        if (!_客户端.是否已连接)
        {
            throw new InvalidOperationException("PLC 未连接，无法下发机种数据。");
        }

        var 数据 = _存储.查找(配方号);
        var 是空机种 = 数据 is null || 数据.是否空;

        if (是空机种 && !允许空机种)
        {
            throw new InvalidOperationException(
                $"机种 {配方号} 还没有数据。请先在示教页调好点位，回到本页点「保存机种」。");
        }

        if (是空机种)
        {
            _日志.LogWarning(
                "机种 {No} 没有数据，按空机种下发（D{From}–D{To} 整段清零）",
                配方号, 机种区段.起始, 机种区段.结束);
        }

        var 快照 = 数据?.区段快照 ?? new Dictionary<int, int>();

        // 先把整段清零：快照是稀疏的，不清零会把上一个机种的残留点位留在 PLC 里
        await 异步填零区段(取消令牌).ConfigureAwait(false);
        await 异步写入区段(快照, 取消令牌).ConfigureAwait(false);

        // 机种号一并写到 PLC（有些现场从 HMI 侧切完就断电，机种号必须落进 PLC）
        await 异步写入寄存器(_配置.控件.配方号地址, 配方号, 取消令牌).ConfigureAwait(false);

        _日志.LogInformation("机种 {No} 的区段快照已下发（{Count} 项）", 配方号, 快照.Count);
    }

    /// <summary>读 PLC 的整个机种区段（分块读，返回稀疏快照）。</summary>
    private async Task<Dictionary<int, int>> 异步读取区段(CancellationToken 取消令牌)
    {
        var 快照 = new Dictionary<int, int>();

        for (var 偏移 = 0; 偏移 < 机种区段.长度; 偏移 += 区段块大小)
        {
            var 本块 = Math.Min(区段块大小, 机种区段.长度 - 偏移);

            var 寄存器列表 = await _客户端
                .异步读取保持寄存器(机种区段.起始 + 偏移, 本块, 取消令牌)
                .ConfigureAwait(false);

            if (寄存器列表.Length < 本块)
            {
                throw new InvalidOperationException(
                    $"从 D{机种区段.起始 + 偏移} 读取 {本块} 个字时 PLC 只返回 {寄存器列表.Length} 个，已中止。");
            }

            for (var 序 = 0; 序 < 本块; 序++)
            {
                if (寄存器列表[序] != 0)
                {
                    快照[偏移 + 序] = 寄存器列表[序];
                }
            }
        }

        return 快照;
    }

    /// <summary>把稀疏快照写进 PLC（只写有值的字，按相邻段合并成块写）。</summary>
    private async Task 异步写入区段(IReadOnlyDictionary<int, int> 快照, CancellationToken 取消令牌)
    {
        foreach (var 块 in 分块(快照))
        {
            await _客户端
                .异步写入多个寄存器(
                    机种区段.起始 + 块.起始偏移,
                    Enumerable.Range(0, 块.值.Length).Select(序 => (ushort)块.值[序]).ToArray(),
                    取消令牌)
                .ConfigureAwait(false);

            await Task.Delay(写入间隔, 取消令牌).ConfigureAwait(false);
        }
    }

    /// <summary>把整个机种区段清零（下发前先清残留）。</summary>
    private async Task 异步填零区段(CancellationToken 取消令牌)
    {
        var 零 = new ushort[区段块大小];

        for (var 偏移 = 0; 偏移 < 机种区段.长度; 偏移 += 区段块大小)
        {
            var 本块 = Math.Min(区段块大小, 机种区段.长度 - 偏移);
            var 值列表 = 本块 == 区段块大小 ? 零 : new ushort[本块];

            await _客户端
                .异步写入多个寄存器(机种区段.起始 + 偏移, 值列表, 取消令牌)
                .ConfigureAwait(false);

            await Task.Delay(写入间隔, 取消令牌).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 把稀疏快照切成"连续的、长度 ≤ 区段块大小"的块。
    /// <para>逐字写 1001 次会慢到不可接受；按空洞切块既快又不会写到不该写的字。</para>
    /// </summary>
    private static IEnumerable<(int 起始偏移, int[] 值)> 分块(IReadOnlyDictionary<int, int> 快照)
    {
        var 有序 = 快照.OrderBy(项 => 项.Key).ToList();
        var 序 = 0;

        while (序 < 有序.Count)
        {
            var 起始偏移 = 有序[序].Key;
            var 长度 = 1;

            while (序 + 长度 < 有序.Count
                   && 长度 < 区段块大小
                   && 有序[序 + 长度].Key == 起始偏移 + 长度)
            {
                长度++;
            }

            var 值列表 = new int[长度];

            for (var 甲 = 0; 甲 < 长度; 甲++)
            {
                值列表[甲] = 有序[序 + 甲].Value;
            }

            yield return (起始偏移, 值列表);
            序 += 长度;
        }
    }

}
