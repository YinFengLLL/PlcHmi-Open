using Microsoft.Extensions.Logging;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// 轮询引擎的「IO 点位采集」部分。
///
/// <para><b>为什么拆成 partial 文件</b>：采集计划编排 + 分组调度已占满
/// <c>PollingEngine.cs</c>（项目规模门禁 400 行）。IO 点位与标签是两套独立语义——
/// IO 是"区域分组 + 稀疏点合并读取"，标签是"读取计划 + 解码发布"，
/// 拆开后每个文件只讲一件事。</para>
/// </summary>
public sealed partial class PollingEngine
{
    /// <summary>Modbus 规范 FC01 单次读线圈上限。协议固定值，不是现场可调项。</summary>
    private const int perRequestMaxCoilNumber = 2000;

    /// <summary>
    /// IO 点合并读取的最大地址间隔：间隔不超过它的相邻点合并为一次请求，
    /// 更大的空洞就拆开（与报警扫描的最大合并间隔同款策略）——
    /// 避免稀疏点（如 X0 与 X1777）按整跨度一次白读上千个位。
    /// </summary>
    private const int ioMaxMergeInterval = 16;

    private readonly List<IoPoint> _enabledInput = new();
    private readonly List<IoPoint> _enabledOutput = new();

    /// <summary>
    /// IO 点读取方案（启动 / 重建时编译一次）。
    /// 此前每个快组周期（默认 100ms）都对全部 IO 点重新做地址解析
    /// 并按 min–max 整跨度读取——地址在配置不变时是常量，纯属浪费。
    /// </summary>
    private readonly List<IoPointBlock> _ioReadPlan = new();

    /// <summary>
    /// 把启用的 IO 点编译为按区域分组的读取方案：
    /// 相邻点间隔不超过 <see cref="ioMaxMergeInterval"/> 就合并为一次读位请求，
    /// 单次请求不超过 <see cref="perRequestMaxCoilNumber"/>，超限自动拆分。
    /// </summary>
    /// <param name="xAreaOverwrite">io-points.json 元的 X区域（"线圈"/"离散输入"，缺省不覆盖）。</param>
    /// <param name="yAreaOverwrite">io-points.json 元的 Y区域。</param>
    private void BuildIoReadPlan(string? xAreaOverwrite = null, string? yAreaOverwrite = null)
    {
        _ioReadPlan.Clear();

        var xArea = ParseAreaOverwrite(xAreaOverwrite);
        var yArea = ParseAreaOverwrite(yAreaOverwrite);

        if (xArea is not null || yArea is not null)
        {
            _log.LogInformation("IO 区位区覆盖：X={XRegion}，Y={YRegion}（来自 io-points.json 元）",
                xArea?.ToString() ?? "（缺省）", yArea?.ToString() ?? "（缺省）");
        }

        var resolved = new List<(IoPoint Point, int Address, ModbusArea Area)>();

        foreach (var PointItem in _enabledInput.Concat(_enabledOutput))
        {
            if (_parser.TryParse(PointItem.Code, out var addressValue) && addressValue is not null)
            {
                // 配置覆盖优先于解析器：X/Y 分区指定，点名前缀决定用哪一个
                var overwrite = PointItem.Code.StartsWith('X') || PointItem.Code.StartsWith('x') ? xArea : yArea;
                resolved.Add((PointItem, addressValue.ModbusAddress, overwrite ?? addressValue.Area));
            }
            else
            {
                _log.LogWarning("IO 点 {Code} 地址无法解析，已跳过", PointItem.Code);
            }
        }

        foreach (var areaGroup in resolved.GroupBy(Item => Item.Area))
        {
            var hasIndex = areaGroup.OrderBy(Item => Item.Address).ToList();
            var Sequence = 0;

            while (Sequence < hasIndex.Count)
            {
                var Start = hasIndex[Sequence].Address;
                var end = Start;
                Sequence++;

                while (Sequence < hasIndex.Count
                       && hasIndex[Sequence].Address - end <= ioMaxMergeInterval
                       && hasIndex[Sequence].Address - Start < perRequestMaxCoilNumber)
                {
                    end = hasIndex[Sequence].Address;
                    Sequence++;
                }

                var countValue = end - Start + 1;
                var memberCollection = hasIndex
                    .Where(Item => Item.Address >= Start && Item.Address < Start + countValue)
                    .Select(Item => (Item.Point, Item.Address - Start))
                    .ToList();

                _ioReadPlan.Add(new IoPointBlock(areaGroup.Key, Start, countValue, memberCollection));
            }
        }
    }

    /// <summary>
    /// 读取 IO 点状态。按启动期编译的读取方案分块批量读，
    /// 不再每周期重新解析地址，也不再为稀疏点整跨度买单。
    /// </summary>
    private async Task PollingIoPointAsync(CancellationToken cancellationToken)
    {
        foreach (var block in _ioReadPlan)
        {
            var ValueList = await TryReadIoBlock(block, cancellationToken).ConfigureAwait(false);

            if (ValueList is null)
            {
                // 本块本周期读不到：只跳过它自己。异常绝不允许逃逸到分组循环——
                // 那会连带丢掉同周期其余 IO 块与全部标签采集（见 尝试读取IO块 的注释）。
                continue;
            }

            foreach (var (PointItem, OffsetValue) in block.memberCollection)
            {
                if (OffsetValue >= 0 && OffsetValue < ValueList.Length)
                {
                    _ioPointTable.UpdateState(PointItem.Code, ValueList[OffsetValue]);
                }
            }
        }
    }

    /// <summary>
    /// 读取一个 IO 块；当前位区读不通或恒读 0 时切换到另一个位区，并把结果<b>记在块上</b>。
    ///
    /// <para><b>为什么位区要按块记忆：</b>现场实证（EASY522）D/M 走 FC01/FC03 正常，
    /// 而 X/Y 的物理状态不在地址解析给出的线圈区（0xF800 / 0xFC00）——同一台 PLC 上
    /// 出现过两种失效形态：线圈区<b>无应答</b>（读请求抛异常），以及线圈区<b>读取成功但恒为 0</b>、
    /// 真实物理状态在离散输入区（FC02）同地址。前者靠异常回退，后者不抛任何异常，
    /// 只能靠「另一位区读到了非 0 信号」来识别（见 <see cref="AllZeroWhenProbeSpareArea"/>）。</para>
    ///
    /// <para>若不记忆、每周期都重新试错，代价有两层：一是每周期白付一次失败往返，
    /// 二是失败异常会从 IO 采集里逃逸、<b>中断本周期其余 IO 块与全部标签采集</b>——
    /// 现场表现正是「X / Y 停在默认值不动，偶尔整个快组都不刷新」。</para>
    ///
    /// <para><b>2026-10-15 修复（现场 X 区错乱）：</b>「全 0 就探测备用区」曾把<b>物理断开</b>
    /// 误判成<b>位区失效</b>——机器空闲时输入点全部断开，线圈区读全 0，探测动作读到备用区里的
    /// 无关位（另一块内存/别的数据），整块被永久切到错误位区，X 显示与物理对不上，
    /// 而始终有信号的块（Y、部分 X）从未探测所以正常。现在以
    /// <see cref="IoPointBlock.CurrentAreaEverHasSignal"/> 作为位区「活性证明」：读到过 1 的位区就是活的，
    /// 之后的全 0 是物理断开，不再探测、不切换。</para>
    ///
    /// <para>读取失败一律以 <c>null</c> 返回（本周期跳过该块），单块故障不牵连同周期其它采集；
    /// 位区选择只在 <see cref="PreparePlanList"/> 重建读取方案时随新块对象归零。</para>
    /// </summary>
    private async Task<bool[]?> TryReadIoBlock(IoPointBlock block, CancellationToken cancellationToken)
    {
        try
        {
            var ValueList = await ReadBitArea(block.GenerateEffectArea, block.Start, block.Count, cancellationToken).ConfigureAwait(false);

            if (Array.IndexOf(ValueList, true) >= 0)
            {
                // 当前位区读到过信号 → 它是"活"的。记住这一点：之后读到全 0 是物理断开，
                // 不是位区失效，绝不能因此去猜备用区（现场 X 区错乱的直接成因）
                block.CurrentAreaEverHasSignal = true;
                return ValueList;
            }

            return await AllZeroWhenProbeSpareArea(block, ValueList, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // 当前位区读不通（无应答 / 非法地址）：回退另一个位区；读成功就记住
            var spare = SpareBitArea(block.GenerateEffectArea);

            try
            {
                var ValueList = await ReadBitArea(spare, block.Start, block.Count, cancellationToken).ConfigureAwait(false);

                _log.LogWarning(
                    "IO 块 {Region} D{Start}x{Count} {Current} 位区读取失败，已回退 {Backup} 位区并记住（后续周期直接使用）",
                    block.Area, block.Start, block.Count, block.GenerateEffectArea, spare);
                block.GenerateEffectArea = spare;
                block.CurrentAreaEverHasSignal = Array.IndexOf(ValueList, true) >= 0;
                block.SpareAreaConnectContinueFailedCount = 0;

                return ValueList;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                _log.LogWarning(exception,
                    "IO 块 {Region} D{Start}x{Count} 两个位区均读取失败，本周期跳过",
                    block.Area, block.Start, block.Count);
                return null;
            }
        }
    }

    /// <summary>备用位区探测的连续失败达到此次数后停止探测（机器空闲时不再每秒空试）。</summary>
    private const int spareAreaProbeMaxFailedCount = 3;

    /// <summary>
    /// 当前位区读成<b>全 0</b> 时的兜底探测：读一次另一位区，读到非 0 信号即切换并记住。
    ///
    /// <para><b>为什么需要：</b>线圈区「读取成功但恒为 0」这种失效不抛异常，异常回退永远不会触发，
    /// 监控页就停在默认值——识别方法只能是"另一个位区有信号"。物理点全部断开时两个位区都是 0，
    /// 保持现状、下个周期继续探，任何一点闭合即完成切换。</para>
    ///
    /// <para><b>2026-10-15 修复（现场 X 区错乱）：</b>探测前提从"本次读成全 0"收紧为
    /// "当前位区<b>从未</b>读到过信号"（<see cref="IoPointBlock.CurrentAreaEverHasSignal"/>）。
    /// 旧逻辑在每次全 0 时都探——机器空闲（输入物理断开）时探到备用区里的无关位，
    /// 就把整块永久切到错误位区；而真正有信号的块（Y、部分 X）从未探测所以看着正常。
    /// 现在：位区只要证明过自己是活的，全 0 就按物理断开处理，绝不猜备用区。</para>
    ///
    /// <para><b>成本控制：</b>当前位区有信号时不探测（零开销）；备用位区连续多次读不通
    /// （典型：标准汇川固件没有这块离散输入区）后停止探测。探测失败静默计数——
    /// 这是预期内的机型差异，不该每个周期刷一条告警。</para>
    /// </summary>
    private async Task<bool[]> AllZeroWhenProbeSpareArea(IoPointBlock block, bool[] currentValue, CancellationToken cancellationToken)
    {
        // Array.IndexOf 免 LINQ 枚举器分配（100ms 周期 × 块数的热路径）
        if (block.SpareAreaConnectContinueFailedCount >= spareAreaProbeMaxFailedCount
            || block.CurrentAreaEverHasSignal
            || Array.IndexOf(currentValue, true) >= 0)
        {
            return currentValue;
        }

        var spare = SpareBitArea(block.GenerateEffectArea);

        bool[] spareValue;
        try
        {
            spareValue = await ReadBitArea(spare, block.Start, block.Count, cancellationToken).ConfigureAwait(false);
            block.SpareAreaConnectContinueFailedCount = 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            block.SpareAreaConnectContinueFailedCount++;

            if (block.SpareAreaConnectContinueFailedCount >= spareAreaProbeMaxFailedCount)
            {
                // 两个位区都读不到信号：给现场一条可操作的提示——自动探测已到极限，
                // 需要用 io-points.json 元的 X区域/Y区域 显式指定，或用 IO 页的
                // 「IO 通信诊断」看一眼两个功能码各自读到了什么
                _log.LogWarning(
                    "IO 块 {Region} D{Start}x{Count} 两个位区均读不到信号，自动探测已停止。"
                    + "若现场这些点实际有输入/输出：① 在 config/io-points.json 的「元」中把对应区"
                    + "（X区域 / Y区域）配为 \"线圈\" 或 \"离散输入\"，保存后重启；"
                    + "② 或到 IO 页点「IO 通信诊断」查看两个功能码各自的原始读数",
                    block.Area, block.Start, block.Count);
            }

            return currentValue;
        }

        if (Array.IndexOf(spareValue, true) < 0)
        {
            return currentValue;
        }

        _log.LogWarning(
            "IO 块 {Region} D{Start}x{Count} {Current} 位区从未读到信号，{Backup} 位区读到物理信号，已切换并记住",
            block.Area, block.Start, block.Count, block.GenerateEffectArea, spare);
        block.GenerateEffectArea = spare;
        block.CurrentAreaEverHasSignal = true;
        block.SpareAreaConnectContinueFailedCount = 0;

        return spareValue;
    }

    /// <summary>按位区走对应的位读取功能码（线圈 FC01 / 离散输入 FC02）。</summary>
    private Task<bool[]> ReadBitArea(ModbusArea areaValue, int Start, int Count, CancellationToken cancellationToken) =>
        areaValue is ModbusArea.DiscreteInput
            ? _client.ReadDiscreteInputAsync(Start, Count, cancellationToken)
            : _client.ReadCoilAsync(Start, Count, cancellationToken);

    /// <summary>另一个位区：线圈 ↔ 离散输入。</summary>
    private static ModbusArea SpareBitArea(ModbusArea areaValue) =>
        areaValue is ModbusArea.Coil ? ModbusArea.DiscreteInput : ModbusArea.Coil;

    /// <summary>
    /// 把 io-points.json 元的 X区域/Y区域 文本解析为 Modbus 位区。
    /// 无法识别（含缺省 null）时返回 null——按解析器的首选位区，与旧行为一致。
    /// </summary>
    private static ModbusArea? ParseAreaOverwrite(string? textValue) => textValue?.Trim() switch
    {
        "线圈" => ModbusArea.Coil,
        "离散输入" => ModbusArea.DiscreteInput,
        null or "" => null,
        _ => null,
    };

    /// <summary>IO 点读取方案：一个区域里一段连续（或近连续）地址的批量读。</summary>
    private sealed record IoPointBlock(
        ModbusArea Area, int Start, int Count, List<(IoPoint Point, int Offset)> memberCollection)
    {
        private ModbusArea? _generateEffectArea;

        /// <summary>
        /// 当前实际用于读取的位区（线圈 FC01 ↔ 离散输入 FC02），初始为地址解析出的首选区域。
        /// <para>两种情况下切换并<b>记住</b>：当前位区读取抛异常（回退），或当前位区从未读到过
        /// 信号而另一位区有物理信号。仅由快组采集线程读写；重建读取方案时随新块对象归零，
        /// 重新从首选位区开始探测。</para>
        /// </summary>
        public ModbusArea GenerateEffectArea
        {
            get => _generateEffectArea ?? Area;
            set => _generateEffectArea = value;
        }

        /// <summary>
        /// 当前位区是否<b>曾经读到过 1</b>（活性证明）。
        /// <para>读到过即证明该位区承载真实 IO：之后读到全 0 是物理点全部断开，
        /// 不是位区失效——不能因此切换到备用位区（现场 X 区错乱的成因：
        /// 机器空闲时探测读到备用区的无关位，整块被永久切错）。</para>
        /// </summary>
        public bool CurrentAreaEverHasSignal { get; set; }

        /// <summary>备用位区探测的连续失败次数（探测成功即归零）。</summary>
        public int SpareAreaConnectContinueFailedCount { get; set; }
    }
}
