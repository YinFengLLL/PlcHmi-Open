using Microsoft.Extensions.Logging;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// <see cref="手动控件服务"/> 的「批量读线圈」部分（2026-11-19 性能改造，partial）。
///
/// <para>动作按钮状态回读原先是"一个按钮一次 <c>异步读取线圈</c>"，一页十几个按钮连发，
/// 每次都要抢同一条串行化连接的闸锁，会把快组 100ms 采集周期整个挤掉。
/// 这里把连续/邻近的 M 区地址合并成一次读位请求，再按偏移分发。</para>
///
/// <para><b>语义与逐点读一致：</b>地址无效、单项解析失败、读请求失败都只让该项返回
/// <c>null</c>（调用方跳过），不影响同批其它项；仍要求全部地址都是 M 区
/// （沿用 <see cref="解析设备中继"/> 的"上位机不越权"约束）。</para>
/// </summary>
public sealed partial class 手动控件服务
{
    /// <summary>合并读取的最大位数：与 Modbus FC01/FC02 单次上限保持一致（2000）。</summary>
    private const int 批量读取最大位数 = 2000;

    /// <inheritdoc />
    public async Task<IReadOnlyList<bool?>> 异步批量读取线圈(
        IReadOnlyList<string> 地址列表, CancellationToken 取消令牌 = default)
    {
        ArgumentNullException.ThrowIfNull(地址列表);

        var 结果 = new bool?[地址列表.Count];

        if (地址列表.Count == 0)
        {
            return 结果;
        }

        // 逐项解析（复用同一套 M 区硬约束），只把解析成功的项纳入合并计划
        var 可合并 = new List<(int 序号, int 地址)>();

        for (var 序号 = 0; 序号 < 地址列表.Count; 序号++)
        {
            try
            {
                可合并.Add((序号, 解析设备中继(地址列表[序号])));
            }
            catch (InvalidOperationException)
            {
                // 单项地址配错 / 未连接：跳过它，不让一批请求全废
                结果[序号] = null;
            }
            catch (ArgumentException)
            {
                结果[序号] = null;
            }
        }

        if (可合并.Count == 0)
        {
            return 结果;
        }

        // 按 Modbus 地址排序，连续（或跨度不超过上限）的块合成一次读位请求
        var 有序 = 可合并.OrderBy(项 => 项.地址).ToList();
        var 起点 = 0;

        while (起点 < 有序.Count)
        {
            var 启动 = 有序[起点].地址;
            var 结束 = 启动;
            var 终点 = 起点;

            while (终点 + 1 < 有序.Count)
            {
                var 下一地址 = 有序[终点 + 1].地址;

                // 跨度超过单次上限就断开；否则把中间空洞一起读回来（多读几个位几乎零成本）
                if (下一地址 - 启动 + 1 > 批量读取最大位数)
                {
                    break;
                }

                结束 = 下一地址;
                终点++;
            }

            var 数量 = 结束 - 启动 + 1;

            // 本块失败只影响本块：其余块照常读，调用方对 null 项跳过
            void 记录块失败()
            {
                for (var 甲 = 起点; 甲 <= 终点; 甲++)
                {
                    结果[有序[甲].序号] = null;
                }
            }

            try
            {
                var 线圈列表 = await _客户端
                    .异步读取线圈(启动, 数量, 取消令牌)
                    .ConfigureAwait(false);

                for (var 甲 = 起点; 甲 <= 终点; 甲++)
                {
                    var 偏移 = 有序[甲].地址 - 启动;
                    结果[有序[甲].序号] = 偏移 < 线圈列表.Length ? 线圈列表[偏移] : null;
                }
            }
            catch (IOException 异常)
            {
                记录块失败();
                _日志.LogWarning(异常, "批量回读动作按钮 M{Start}–M{End} 失败，本块已跳过", 启动, 结束);
            }
            catch (InvalidOperationException 异常)
            {
                记录块失败();
                _日志.LogWarning(异常, "批量回读动作按钮 M{Start}–M{End} 失败，本块已跳过", 启动, 结束);
            }
            catch (OperationCanceledException)
            {
                记录块失败();
            }

            起点 = 终点 + 1;
        }

        return 结果;
    }
}
