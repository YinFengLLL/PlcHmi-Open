using System.Globalization;
using Microsoft.Extensions.Logging;
using 上位机.通信.Modbus;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// 历史记录服务的采集部分：把配置里的数据项地址合并成尽量少的请求，读回并按配置的字序解码。
///
/// <para>拆出来的原因与 <c>PollingEngine.Publish.cs</c> 相同：读取计划的合并规则是独立的一小块逻辑，
/// 混在生命周期与校验里会让文件越滚越长，改一处要读一整屏。</para>
/// </summary>
public sealed partial class 历史记录服务
{
    /// <summary>合并读取时允许的最大跨区间隔（字）。</summary>
    private const int 最大合并间隔 = 16;

    /// <summary>单次请求最多读多少个寄存器。</summary>
    private const int 每次请求最大寄存器数 = 100;

    /// <summary>读取全部数据项。任一失败返回 <c>null</c>（本轮不记录）。</summary>
    private async Task<List<double>?> 异步读取数据项(历史记录配置 配置, CancellationToken 取消令牌)
    {
        var 已解析 = new List<(int 序号, 历史数据项 项, PLC地址 地址)>();

        for (var 序号 = 0; 序号 < 配置.数据项集合.Count; 序号++)
        {
            var 项 = 配置.数据项集合[序号];

            if (!_解析器.尝试解析(项.地址, out var 地址) || 地址 is null)
            {
                _日志.LogWarning("数据项「{Name}」的地址「{Address}」无法解析，本次不记录",
                    项.名称, 项.地址);
                return null;
            }

            已解析.Add((序号, 项, 地址));
        }

        var 结果 = new double[配置.数据项集合.Count];

        foreach (var 方案 in 构建读取方案列表(已解析))
        {
            var 寄存器列表 = await _客户端
                .异步读取保持寄存器(方案.启动地址, 方案.数量, 取消令牌)
                .ConfigureAwait(false);

            if (寄存器列表.Length < 方案.数量)
            {
                _日志.LogWarning("读取 {Start}（{Count} 字）只返回 {Actual} 个字，本次不记录",
                    方案.启动地址, 方案.数量, 寄存器列表.Length);
                return null;
            }

            foreach (var 条目 in 方案.条目集合)
            {
                var 偏移量 = 条目.地址.Modbus地址 - 方案.启动地址;
                var 字数 = _解析器.获取寄存器数量(条目.项.数据类型);

                if (偏移量 < 0 || 偏移量 + 字数 > 寄存器列表.Length)
                {
                    _日志.LogWarning("数据项「{Name}」在合并读取中越界（{Offset}+{Size}），本次不记录",
                        条目.项.名称, 偏移量, 字数);
                    return null;
                }

                // 解码统一走寄存器编解码器（字序由配置决定），界面层不参与拆字（审查红线 R1）
                结果[条目.序号] = Convert.ToDouble(
                    寄存器编解码器.解码(条目.项.数据类型, 寄存器列表.AsSpan(偏移量, 字数), 配置.字顺序),
                    CultureInfo.InvariantCulture);
            }
        }

        return [.. 结果];
    }

    /// <summary>把数据项按「同软元件区 + 地址接近」合并成读取计划。</summary>
    private List<读取方案> 构建读取方案列表(List<(int 序号, 历史数据项 项, PLC地址 地址)> 已解析)
    {
        var 方案列表 = new List<读取方案>();

        // 先按区、再按地址排序：M0 与 D0 的 Modbus 地址都是 0，不分区会把两处数据混进一次请求
        foreach (var 条目 in 已解析
                     .OrderBy(项 => 项.地址.元素)
                     .ThenBy(项 => 项.地址.Modbus地址))
        {
            var 最后 = 方案列表.Count > 0 ? 方案列表[^1] : null;
            var 字数 = _解析器.获取寄存器数量(条目.项.数据类型);

            if (最后 is not null &&
                最后.元素 == 条目.地址.元素 &&
                条目.地址.Modbus地址 >= 最后.启动地址 &&
                条目.地址.Modbus地址 - 最后.启动地址 + 字数 <= 每次请求最大寄存器数 &&
                条目.地址.Modbus地址 - (最后.启动地址 + 最后.数量) <= 最大合并间隔)
            {
                最后.条目集合.Add(条目);
                最后.数量 = Math.Max(最后.数量, 条目.地址.Modbus地址 - 最后.启动地址 + 字数);
                continue;
            }

            var 方案 = new 读取方案 { 启动地址 = 条目.地址.Modbus地址, 元素 = 条目.地址.元素 };
            方案.条目集合.Add(条目);
            方案.数量 = 字数;
            方案列表.Add(方案);
        }

        return 方案列表;
    }

    /// <summary>配置错误只提示一次：每 500ms 刷一行日志会把日志冲垮，反而看不见真正的问题。</summary>
    private void 提示配置错误(string 消息)
    {
        if (_已提示配置错误)
        {
            return;
        }

        _已提示配置错误 = true;
        _日志.LogError("历史记录配置错误：{Message}", 消息);
    }

    /// <summary>一次合并读取。</summary>
    private sealed class 读取方案
    {
        /// <summary>起始 Modbus 地址。</summary>
        public int 启动地址 { get; init; }

        /// <summary>读取长度（随合并增长）。</summary>
        public int 数量 { get; set; }

        /// <summary>所属软元件区。</summary>
        public 软元素 元素 { get; init; }

        /// <summary>本方案覆盖的数据项。</summary>
        public List<(int 序号, 历史数据项 项, PLC地址 地址)> 条目集合 { get; } = new();
    }
}
