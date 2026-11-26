using System.Globalization;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using PlcHmi.Communication.Modbus;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// <see cref="PageAddressService"/> 的「辅助地址批量读取」部分（2026-11-19 性能改造，partial）。
///
/// <para><b>为什么要有这一层：</b>辅助地址区上限提到 30 行后，周期回读若仍"一行一次
/// <c>异步读取保持寄存器</c>"，3s 节拍里会连发 30 个请求；而所有 Modbus 收发共用
/// <c>FluentModbusTcpClient</c> 的一把 <c>SemaphoreSlim(1,1)</c> 闸锁，一串连发会
/// 独占闸锁上百毫秒，把快组 100ms 采集周期整个挤掉（现场表现为按钮跟随变慢、IO 丢拍）。</para>
///
/// <para><b>做法：</b>把连续/邻近的 D 区地址合并成一次保持寄存器读请求（跨度上限 100 字，
/// 与 <c>appsettings.json</c> 的 每次请求最大寄存器数 一致），再按偏移切回各行解码。
/// 实测锁附页 D3342–D3370、点胶页 D3500–D3506 都是高度连续的，30 行可压到 2~4 个请求。</para>
///
/// <para><b>语义与逐行读一致：</b>开关类（M 区布尔）、地址无效、单项/单块读取失败都只让该项
/// 返回 <c>null</c>（调用方跳过该行），不影响其它项；解码仍走 <see cref="RegisterCodec.Decode"/>
/// 与 <see cref="WordOrder.LittleEndian"/>，与 <c>异步读取辅助地址</c> 同源（红线 R1）。</para>
/// </summary>
public sealed partial class PageAddressService
{
    /// <summary>合并读取的跨度上限（字）：与 appsettings.json 每次请求最大寄存器数 对齐。</summary>
    private const int batchReadMaxWordCount = 100;

    /// <inheritdoc />
    public async Task<IReadOnlyList<double?>> BatchReadAuxAddressAsync(
        IReadOnlyList<AuxAddressItem> itemList, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(itemList);

        var result = new double?[itemList.Count];

        if (itemList.Count == 0)
        {
            return result;
        }

        if (!_client.IsConnected)
        {
            return result;   // 未连接：全部跳过，与 异步读取辅助地址 返回 0 的容错口径一致
        }

        // 逐项解析，只把"可批量读取"的 D 区数值项纳入合并计划
        var mergeable = new List<(int Sequence, int Address, int wordCount, TagDataType Kind)>();

        for (var Sequence = 0; Sequence < itemList.Count; Sequence++)
        {
            var Item = itemList[Sequence];

            if (Item is null || Item.IsSwitch || !TryParse(Item.Address, out var addressValue) || addressValue <= 0)
            {
                result[Sequence] = null;   // 开关走线圈、地址无效：调用方跳过
                continue;
            }

            mergeable.Add((Sequence, addressValue, Math.Max(1, Item.RegisterCount), Item.DataType));
        }

        if (mergeable.Count == 0)
        {
            return result;
        }

        var hasIndex = mergeable.OrderBy(Item => Item.Address).ToList();
        var startPoint = 0;

        while (startPoint < hasIndex.Count)
        {
            var Start = hasIndex[startPoint].Address;
            var end = hasIndex[startPoint].Address + hasIndex[startPoint].wordCount;
            var endPoint = startPoint;

            while (endPoint + 1 < hasIndex.Count)
            {
                var projectionEnd = Math.Max(end, hasIndex[endPoint + 1].Address + hasIndex[endPoint + 1].wordCount);

                if (projectionEnd - Start > batchReadMaxWordCount)
                {
                    break;
                }

                end = projectionEnd;
                endPoint++;
            }

            var Count = end - Start;
            ushort[]? registerList = null;

            try
            {
                registerList = await _client
                    .ReadHoldingRegisterAsync(Start, Count, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (IOException exception)
            {
                _log.LogWarning(exception, "批量回读辅助地址 D{Start}–D{End} 失败，本块已跳过", Start, end - 1);
            }
            catch (SocketException exception)
            {
                _log.LogWarning(exception, "批量回读辅助地址 D{Start}–D{End} 失败，本块已跳过", Start, end - 1);
            }
            catch (InvalidOperationException exception)
            {
                _log.LogWarning(exception, "批量回读辅助地址 D{Start}–D{End} 失败，本块已跳过", Start, end - 1);
            }
            catch (OperationCanceledException)
            {
                // 取消：本块按 null 跳过，不记警告
            }

            for (var item = startPoint; item <= endPoint; item++)
            {
                var Member = hasIndex[item];
                var Offset = Member.Address - Start;

                if (registerList is null || Offset < 0 || Offset + Member.wordCount > registerList.Length)
                {
                    result[Member.Sequence] = null;
                    continue;
                }

                try
                {
                    var sliceSection = new ushort[Member.wordCount];
                    Array.Copy(registerList, Offset, sliceSection, 0, Member.wordCount);

                    var decoded = RegisterCodec.Decode(Member.Kind, sliceSection, WordOrder.LittleEndian);
                    result[Member.Sequence] = Convert.ToDouble(decoded, CultureInfo.InvariantCulture);
                }
                catch (NotSupportedException)
                {
                    result[Member.Sequence] = null;
                }
                catch (ArgumentException)
                {
                    result[Member.Sequence] = null;
                }
                catch (FormatException)
                {
                    result[Member.Sequence] = null;
                }
            }

            startPoint = endPoint + 1;
        }

        return result;
    }
}
