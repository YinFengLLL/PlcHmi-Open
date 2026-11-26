using Microsoft.Extensions.Logging;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// <see cref="ManualWidgetService"/> 的「批量读线圈」部分（2026-11-19 性能改造，partial）。
///
/// <para>动作按钮状态回读原先是"一个按钮一次 <c>异步读取线圈</c>"，一页十几个按钮连发，
/// 每次都要抢同一条串行化连接的闸锁，会把快组 100ms 采集周期整个挤掉。
/// 这里把连续/邻近的 M 区地址合并成一次读位请求，再按偏移分发。</para>
///
/// <para><b>语义与逐点读一致：</b>地址无效、单项解析失败、读请求失败都只让该项返回
/// <c>null</c>（调用方跳过），不影响同批其它项；仍要求全部地址都是 M 区
/// （沿用 <see cref="ParseDeviceContinue"/> 的"上位机不越权"约束）。</para>
/// </summary>
public sealed partial class ManualWidgetService
{
    /// <summary>合并读取的最大位数：与 Modbus FC01/FC02 单次上限保持一致（2000）。</summary>
    private const int batchReadMaxBitCount = 2000;

    /// <inheritdoc />
    public async Task<IReadOnlyList<bool?>> BatchReadCoilAsync(
        IReadOnlyList<string> addressList, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(addressList);

        var result = new bool?[addressList.Count];

        if (addressList.Count == 0)
        {
            return result;
        }

        // 逐项解析（复用同一套 M 区硬约束），只把解析成功的项纳入合并计划
        var mergeable = new List<(int Sequence, int Address)>();

        for (var Sequence = 0; Sequence < addressList.Count; Sequence++)
        {
            try
            {
                mergeable.Add((Sequence, ParseDeviceContinue(addressList[Sequence])));
            }
            catch (InvalidOperationException)
            {
                // 单项地址配错 / 未连接：跳过它，不让一批请求全废
                result[Sequence] = null;
            }
            catch (ArgumentException)
            {
                result[Sequence] = null;
            }
        }

        if (mergeable.Count == 0)
        {
            return result;
        }

        // 按 Modbus 地址排序，连续（或跨度不超过上限）的块合成一次读位请求
        var hasIndex = mergeable.OrderBy(Item => Item.Address).ToList();
        var startPoint = 0;

        while (startPoint < hasIndex.Count)
        {
            var Start = hasIndex[startPoint].Address;
            var end = Start;
            var endPoint = startPoint;

            while (endPoint + 1 < hasIndex.Count)
            {
                var nextAddress = hasIndex[endPoint + 1].Address;

                // 跨度超过单次上限就断开；否则把中间空洞一起读回来（多读几个位几乎零成本）
                if (nextAddress - Start + 1 > batchReadMaxBitCount)
                {
                    break;
                }

                end = nextAddress;
                endPoint++;
            }

            var Count = end - Start + 1;

            // 本块失败只影响本块：其余块照常读，调用方对 null 项跳过
            void RecordBlockFailed()
            {
                for (var item = startPoint; item <= endPoint; item++)
                {
                    result[hasIndex[item].Sequence] = null;
                }
            }

            try
            {
                var coilList = await _client
                    .ReadCoilAsync(Start, Count, cancellationToken)
                    .ConfigureAwait(false);

                for (var item = startPoint; item <= endPoint; item++)
                {
                    var Offset = hasIndex[item].Address - Start;
                    result[hasIndex[item].Sequence] = Offset < coilList.Length ? coilList[Offset] : null;
                }
            }
            catch (IOException exception)
            {
                RecordBlockFailed();
                _log.LogWarning(exception, "批量回读动作按钮 M{Start}–M{End} 失败，本块已跳过", Start, end);
            }
            catch (InvalidOperationException exception)
            {
                RecordBlockFailed();
                _log.LogWarning(exception, "批量回读动作按钮 M{Start}–M{End} 失败，本块已跳过", Start, end);
            }
            catch (OperationCanceledException)
            {
                RecordBlockFailed();
            }

            startPoint = endPoint + 1;
        }

        return result;
    }
}
