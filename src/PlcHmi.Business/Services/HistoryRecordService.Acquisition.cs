using System.Globalization;
using Microsoft.Extensions.Logging;
using PlcHmi.Communication.Modbus;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// 历史记录服务的采集部分：把配置里的数据项地址合并成尽量少的请求，读回并按配置的字序解码。
///
/// <para>拆出来的原因与 <c>PollingEngine.Publish.cs</c> 相同：读取计划的合并规则是独立的一小块逻辑，
/// 混在生命周期与校验里会让文件越滚越长，改一处要读一整屏。</para>
/// </summary>
public sealed partial class HistoryRecordService
{
    /// <summary>合并读取时允许的最大跨区间隔（字）。</summary>
    private const int maxMergeInterval = 16;

    /// <summary>单次请求最多读多少个寄存器。</summary>
    private const int PerRequestMaxRegisterNumber = 100;

    /// <summary>读取全部数据项。任一失败返回 <c>null</c>（本轮不记录）。</summary>
    private async Task<List<double>?> ReadDataItemAsync(HistoryRecordConfig Config, CancellationToken cancellationToken)
    {
        var resolved = new List<(int Sequence, HistoryDataItem Item, PlcAddress Address)>();

        for (var Sequence = 0; Sequence < Config.DataItemCollection.Count; Sequence++)
        {
            var Item = Config.DataItemCollection[Sequence];

            if (!_parser.TryParse(Item.Address, out var Address) || Address is null)
            {
                _log.LogWarning("数据项「{Name}」的地址「{Address}」无法解析，本次不记录",
                    Item.Name, Item.Address);
                return null;
            }

            resolved.Add((Sequence, Item, Address));
        }

        var result = new double[Config.DataItemCollection.Count];

        foreach (var plan in BuildReadPlanList(resolved))
        {
            var registerList = await _client
                .ReadHoldingRegisterAsync(plan.StartAddress, plan.Count, cancellationToken)
                .ConfigureAwait(false);

            if (registerList.Length < plan.Count)
            {
                _log.LogWarning("读取 {Start}（{Count} 字）只返回 {Actual} 个字，本次不记录",
                    plan.StartAddress, plan.Count, registerList.Length);
                return null;
            }

            foreach (var Entry in plan.EntryCollection)
            {
                var OffsetValue = Entry.Address.ModbusAddress - plan.StartAddress;
                var wordCount = _parser.GetRegisterCount(Entry.Item.DataType);

                if (OffsetValue < 0 || OffsetValue + wordCount > registerList.Length)
                {
                    _log.LogWarning("数据项「{Name}」在合并读取中越界（{Offset}+{Size}），本次不记录",
                        Entry.Item.Name, OffsetValue, wordCount);
                    return null;
                }

                // 解码统一走寄存器编解码器（字序由配置决定），界面层不参与拆字（审查红线 R1）
                result[Entry.Sequence] = Convert.ToDouble(
                    RegisterCodec.Decode(Entry.Item.DataType, registerList.AsSpan(OffsetValue, wordCount), Config.WordOrder),
                    CultureInfo.InvariantCulture);
            }
        }

        return [.. result];
    }

    /// <summary>把数据项按「同软元件区 + 地址接近」合并成读取计划。</summary>
    private List<ReadPlan> BuildReadPlanList(List<(int Sequence, HistoryDataItem Item, PlcAddress Address)> resolved)
    {
        var planList = new List<ReadPlan>();

        // 先按区、再按地址排序：M0 与 D0 的 Modbus 地址都是 0，不分区会把两处数据混进一次请求
        foreach (var Entry in resolved
                     .OrderBy(Item => Item.Address.ElementItem)
                     .ThenBy(Item => Item.Address.ModbusAddress))
        {
            var Last = planList.Count > 0 ? planList[^1] : null;
            var wordCount = _parser.GetRegisterCount(Entry.Item.DataType);

            if (Last is not null &&
                Last.ElementItem == Entry.Address.ElementItem &&
                Entry.Address.ModbusAddress >= Last.StartAddress &&
                Entry.Address.ModbusAddress - Last.StartAddress + wordCount <= PerRequestMaxRegisterNumber &&
                Entry.Address.ModbusAddress - (Last.StartAddress + Last.Count) <= maxMergeInterval)
            {
                Last.EntryCollection.Add(Entry);
                Last.Count = Math.Max(Last.Count, Entry.Address.ModbusAddress - Last.StartAddress + wordCount);
                continue;
            }

            var plan = new ReadPlan { StartAddress = Entry.Address.ModbusAddress, ElementItem = Entry.Address.ElementItem };
            plan.EntryCollection.Add(Entry);
            plan.Count = wordCount;
            planList.Add(plan);
        }

        return planList;
    }

    /// <summary>配置错误只提示一次：每 500ms 刷一行日志会把日志冲垮，反而看不见真正的问题。</summary>
    private void HintConfigError(string Message)
    {
        if (_hintedConfigError)
        {
            return;
        }

        _hintedConfigError = true;
        _log.LogError("历史记录配置错误：{Message}", Message);
    }

    /// <summary>一次合并读取。</summary>
    private sealed class ReadPlan
    {
        /// <summary>起始 Modbus 地址。</summary>
        public int StartAddress { get; init; }

        /// <summary>读取长度（随合并增长）。</summary>
        public int Count { get; set; }

        /// <summary>所属软元件区。</summary>
        public SoftElement ElementItem { get; init; }

        /// <summary>本方案覆盖的数据项。</summary>
        public List<(int Sequence, HistoryDataItem Item, PlcAddress Address)> EntryCollection { get; } = new();
    }
}
