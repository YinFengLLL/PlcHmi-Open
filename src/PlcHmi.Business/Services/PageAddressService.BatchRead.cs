using System.Globalization;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using 上位机.通信.Modbus;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// <see cref="页面地址服务"/> 的「辅助地址批量读取」部分（2026-11-19 性能改造，partial）。
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
/// 返回 <c>null</c>（调用方跳过该行），不影响其它项；解码仍走 <see cref="寄存器编解码器.解码"/>
/// 与 <see cref="字顺序.小字节序"/>，与 <c>异步读取辅助地址</c> 同源（红线 R1）。</para>
/// </summary>
public sealed partial class 页面地址服务
{
    /// <summary>合并读取的跨度上限（字）：与 appsettings.json 每次请求最大寄存器数 对齐。</summary>
    private const int 批量读取最大字数 = 100;

    /// <inheritdoc />
    public async Task<IReadOnlyList<double?>> 异步批量读取辅助地址(
        IReadOnlyList<辅助地址项> 项列表, CancellationToken 取消令牌 = default)
    {
        ArgumentNullException.ThrowIfNull(项列表);

        var 结果 = new double?[项列表.Count];

        if (项列表.Count == 0)
        {
            return 结果;
        }

        if (!_客户端.是否已连接)
        {
            return 结果;   // 未连接：全部跳过，与 异步读取辅助地址 返回 0 的容错口径一致
        }

        // 逐项解析，只把"可批量读取"的 D 区数值项纳入合并计划
        var 可合并 = new List<(int 序号, int 地址, int 字数, 标签数据类型 类型)>();

        for (var 序号 = 0; 序号 < 项列表.Count; 序号++)
        {
            var 项 = 项列表[序号];

            if (项 is null || 项.是开关 || !尝试解析(项.地址, out var 地址值) || 地址值 <= 0)
            {
                结果[序号] = null;   // 开关走线圈、地址无效：调用方跳过
                continue;
            }

            可合并.Add((序号, 地址值, Math.Max(1, 项.寄存器数量), 项.数据类型));
        }

        if (可合并.Count == 0)
        {
            return 结果;
        }

        var 有序 = 可合并.OrderBy(项 => 项.地址).ToList();
        var 起点 = 0;

        while (起点 < 有序.Count)
        {
            var 启动 = 有序[起点].地址;
            var 结束 = 有序[起点].地址 + 有序[起点].字数;
            var 终点 = 起点;

            while (终点 + 1 < 有序.Count)
            {
                var 投影结束 = Math.Max(结束, 有序[终点 + 1].地址 + 有序[终点 + 1].字数);

                if (投影结束 - 启动 > 批量读取最大字数)
                {
                    break;
                }

                结束 = 投影结束;
                终点++;
            }

            var 数量 = 结束 - 启动;
            ushort[]? 寄存器列表 = null;

            try
            {
                寄存器列表 = await _客户端
                    .异步读取保持寄存器(启动, 数量, 取消令牌)
                    .ConfigureAwait(false);
            }
            catch (IOException 异常)
            {
                _日志.LogWarning(异常, "批量回读辅助地址 D{Start}–D{End} 失败，本块已跳过", 启动, 结束 - 1);
            }
            catch (SocketException 异常)
            {
                _日志.LogWarning(异常, "批量回读辅助地址 D{Start}–D{End} 失败，本块已跳过", 启动, 结束 - 1);
            }
            catch (InvalidOperationException 异常)
            {
                _日志.LogWarning(异常, "批量回读辅助地址 D{Start}–D{End} 失败，本块已跳过", 启动, 结束 - 1);
            }
            catch (OperationCanceledException)
            {
                // 取消：本块按 null 跳过，不记警告
            }

            for (var 甲 = 起点; 甲 <= 终点; 甲++)
            {
                var 成员 = 有序[甲];
                var 偏移 = 成员.地址 - 启动;

                if (寄存器列表 is null || 偏移 < 0 || 偏移 + 成员.字数 > 寄存器列表.Length)
                {
                    结果[成员.序号] = null;
                    continue;
                }

                try
                {
                    var 片段 = new ushort[成员.字数];
                    Array.Copy(寄存器列表, 偏移, 片段, 0, 成员.字数);

                    var 已解码 = 寄存器编解码器.解码(成员.类型, 片段, 字顺序.小字节序);
                    结果[成员.序号] = Convert.ToDouble(已解码, CultureInfo.InvariantCulture);
                }
                catch (NotSupportedException)
                {
                    结果[成员.序号] = null;
                }
                catch (ArgumentException)
                {
                    结果[成员.序号] = null;
                }
                catch (FormatException)
                {
                    结果[成员.序号] = null;
                }
            }

            起点 = 终点 + 1;
        }

        return 结果;
    }
}
