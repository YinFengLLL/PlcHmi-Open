using Microsoft.Extensions.Logging;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// 轮询引擎的「读取计划编译」部分（partial）。
///
/// <para><b>为什么拆成 partial 文件：</b>采集计划编排 + 分组调度 + 实际解码发布 + 计划编译
/// 四件事合在一个文件里已超过 400 行规模门禁。计划编译是独立的纯函数式职责
/// （地址解析 → 合并 → 分块），单独成文件后每个文件只讲一件事。</para>
/// </summary>
public sealed partial class PollingEngine
{
    /// <summary>
    /// 把点位列表编译为“读取计划”：合并连续地址，按最大寄存器数分块。
    /// </summary>
    private List<ReadPlan> BuildReadPlanList(IReadOnlyList<TagDefinition> tagList)
    {
        // 单次请求上限与通信层共用同一个配置项（appsettings.json 的 Plc.MaxRegistersPerRequest），
        // 不再各处硬编码：两处各写一个数，改一处漏一处就会超出汇川的读 123 上限。
        // 原先这里是 `FastIntervalMs > 0 ? 100 : 100`——两个分支同值，是死代码。
        var maxRegister = _plcOptions.PerRequestMaxRegisterNumber;
        var planList = new List<ReadPlan>();

        // 位元件与字元件分开处理，且各自按 Modbus 区域分组：
        // 中型 PLC 的 IX 位元件位于离散输入区，必须走 FC02；
        // 此前按"线圈 / 保持寄存器"二分，离散输入区与输入寄存器区的点
        // 会被套上错误的读取区域（异步轮询标签 里的分支沦为死代码）。
        // D/R 字内位（如 D1210.2，布尔但落在保持寄存器区）按 1 字寄存器读取，发布时按位取值。
        var bitTag = new List<(TagDefinition Tag, int Address, ModbusArea Area)>();
        var registerTag = new List<(TagDefinition Tag, int Address, int Count, int BitIndex, ModbusArea Area)>();

        // 机种区段（D2500–D3999）里的数据不自动回读（2026-10-21 现场要求）：
        // 这一段是点表 / 单点工艺参数 / 机种快照的落点，改为"示教页按「读取点表」按需读取"。
        // 周期采集这段既没必要（每秒几百字），也会让人误以为界面在实时跟随 PLC。
        var skipMachineModelSection = 0;

        foreach (var tagItem in tagList)
        {
            if (!tagItem.Readable || !_parser.TryParse(tagItem.Address, out var addressValue) || addressValue is null)
            {
                if (tagItem.Readable)
                {
                    _log.LogWarning("点位 {Name} 地址 \"{Address}\" 无法解析，已跳过", tagItem.Name, tagItem.Address);
                }

                continue;
            }

            if (addressValue.Area is ModbusArea.HoldingRegister && MachineModelSection.Contains(addressValue.ModbusAddress))
            {
                skipMachineModelSection++;
                continue;
            }

            var registerCountItem = _parser.GetRegisterCount(tagItem.DataType);

            if (addressValue.Area is ModbusArea.Coil or ModbusArea.DiscreteInput
                && tagItem.DataType == TagDataType.Boolean)
            {
                bitTag.Add((tagItem, addressValue.ModbusAddress, addressValue.Area));
            }
            else
            {
                // 布尔字内位恒占 1 字（整字读），不能用类型的寄存器数量
                var isBitTag = addressValue.IsIntraWordBit && tagItem.DataType == TagDataType.Boolean;
                registerTag.Add((tagItem, addressValue.ModbusAddress,
                    isBitTag ? 1 : registerCountItem,
                    isBitTag ? addressValue.BitIndex : -1,
                    addressValue.Area));
            }
        }

        // 位元件：按区域分组，连续地址合并为一次读位请求（线圈 / 离散输入）
        // 合并上限用 Modbus 位上限（FC01/FC02 = 2000 位），不是字上限（100）——
        // 曾误用寄存器上限，1000 个 M 区布尔点被拆成 10 次请求，白白多出 9 次往返
        foreach (var areaGroup in bitTag.GroupBy(bitItem => bitItem.Area))
        {
            foreach (var dataBlock in MergeAddress(areaGroup.Select(bitItem => bitItem.Address), perRequestMaxCoilNumber))
            {
                var memberList = areaGroup
                    .Where(bitItem => bitItem.Address >= dataBlock.Start && bitItem.Address < dataBlock.Start + dataBlock.Count)
                    .Select(bitItem => new PlanMember(bitItem.Tag, bitItem.Address - dataBlock.Start, 1))
                    .ToList();

                planList.Add(new ReadPlan(areaGroup.Key, dataBlock.Start, dataBlock.Count, memberList));
            }
        }

        if (skipMachineModelSection > 0)
        {
            _log.LogInformation(
                "机种区段 D{Start}–D{End} 不做周期采集（{Count} 个点位已跳过），点表由示教页「读取点表」按需读取",
                MachineModelSection.StartAt, MachineModelSection.end, skipMachineModelSection);
        }

        // 字元件：按区域分组，各自按起始与结束地址合并（保持 / 输入寄存器）
        foreach (var areaGroup in registerTag.GroupBy(registerItem => registerItem.Area))
        {
            var hasIndex = areaGroup.OrderBy(registerItem => registerItem.Address).ToList();
            var Sequence = 0;

            while (Sequence < hasIndex.Count)
            {
                var Start = hasIndex[Sequence].Address;
                var end = hasIndex[Sequence].Address + hasIndex[Sequence].Count;
                var memberList = new List<PlanMember> { new(hasIndex[Sequence].Tag, 0, hasIndex[Sequence].Count, hasIndex[Sequence].BitIndex) };
                Sequence++;

                while (Sequence < hasIndex.Count)
                {
                    var next = hasIndex[Sequence];
                    var projectionEnd = Math.Max(end, next.Address + next.Count);

                    // 超过单次上限就断开
                    if (projectionEnd - Start > maxRegister)
                    {
                        break;
                    }

                    // 允许与当前区间重叠或紧邻（无空洞）
                    if (next.Address > end)
                    {
                        break;
                    }

                    memberList.Add(new PlanMember(next.Tag, next.Address - Start, next.Count, next.BitIndex));
                    end = projectionEnd;
                    Sequence++;
                }

                planList.Add(new ReadPlan(areaGroup.Key, Start, end - Start, memberList));
            }
        }

        return planList;
    }

    /// <summary>把一组地址合并为连续区间（按最大值切块）。</summary>
    private static IEnumerable<(int Start, int Count)> MergeAddress(IEnumerable<int> addressList, int maxLength)
    {
        // SortedSet 一趟完成排序 + 去重（原 Distinct().OrderBy().ToList() 三趟遍历）
        var hasIndex = new SortedSet<int>(addressList).ToList();
        var Sequence = 0;

        while (Sequence < hasIndex.Count)
        {
            var Start = hasIndex[Sequence];
            var end = Start + 1;
            Sequence++;

            while (Sequence < hasIndex.Count && hasIndex[Sequence] <= end && hasIndex[Sequence] - Start < maxLength)
            {
                end = Math.Max(end, hasIndex[Sequence] + 1);
                Sequence++;
            }

            yield return (Start, Math.Min(end - Start, maxLength));
        }
    }

    // ── 内部类型 ────────────────────────────────────────────────────────

    /// <summary>
    /// 读取方案里的一个点位成员。
    /// <para><paramref name="BitIndex"/> ≥ 0 表示 D/R 区字内位（布尔）：
    /// 读取仍是整字，发布时按 <c>(字值 &gt;&gt; 位) &amp; 1</c> 取布尔值。</para>
    /// </summary>
    private sealed record PlanMember(TagDefinition Tag, int Offset, int RegisterCount, int BitIndex = -1);

    private sealed record ReadPlan(ModbusArea Area, int StartAddress, int Count, List<PlanMember> memberCollection);

    private sealed record PollingGroup(TagGroup Group, TimeSpan interval, List<ReadPlan> planCollection, bool hasIo);
}
