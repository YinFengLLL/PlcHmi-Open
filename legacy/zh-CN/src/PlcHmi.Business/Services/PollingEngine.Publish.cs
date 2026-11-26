using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using 上位机.通信.Modbus;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// 轮询引擎的「值发布与范围诊断」部分。
///
/// <para><b>为什么拆成 partial 文件</b>：本类承担了采集计划编排、分组调度与实际解码发布三件事，
/// 合在一个文件里已超过 400 行审查阈值 —— 审查者很难在心里维持完整上下文，回归缺陷极易漏过。
/// 这里只放"把读回来的原始字变成一个已发布的点位值"，以及伴随的物理范围诊断；
/// 采集计划与调度仍在 <c>PollingEngine.cs</c>。</para>
/// </summary>
public sealed partial class 轮询引擎
{
    // ── 解码值物理范围诊断（2026-09-12 现场乱值排查后加）────────────────
    // 现场 HMI 曾显示 3.7e26：复核后解码实现无缺陷，是 PLC D 区当时的残留数据。
    // 为让下次"界面显示离谱值"能一步定位，解码值超出点位声明的物理范围时记 Warning
    // 并附原始字十六进制——与 PLC 监控同帧对照即可分清"数据源垃圾/被覆盖"还是"解码错"。

    // 已告警点位集合。三个采集分组线程并发读写，用 ConcurrentDictionary 走无锁快路径：
    // "值在范围内"是每点每周期的常态，此前每次都要进 lock 与另外两个分组争用
    // （100ms 周期 × 数百点 = 每秒数千次加锁）。
    private readonly ConcurrentDictionary<string, byte> _范围已告警 = new();

    /// <summary>
    /// 解码值超出点位声明的 [Min, Max] 时记一条带原始字十六进制的 Warning。
    /// <para>同一点位持续越界只记一次，恢复正常后重新武装，避免 100ms 轮询刷爆日志；
    /// 越界值照常发布，只诊断不拦截——日志是线索，不是过滤器。</para>
    /// </summary>
    private void 告警范围(标签定义 标签项, ushort[]? 寄存器列表, int 偏移量, int 数量值, double 缩放后)
    {
        // 线圈位路径没有寄存器可对照，且 Bool 点位也不会配物理范围——直接跳过
        if (标签项.最小 is null || 标签项.最大 is null || 寄存器列表 is null)
        {
            return;
        }

        if (缩放后 >= 标签项.最小.Value && 缩放后 <= 标签项.最大.Value)
        {
            // 值在范围内是常态：仅当该点位此前告警过（需要解除武装）时才动集合
            _范围已告警.TryRemove(标签项.名称, out _);
            return;
        }

        // 越界是低频路径：TryAdd 失败 = 已告警过，直接返回
        if (!_范围已告警.TryAdd(标签项.名称, 0))
        {
            return;
        }

        var 十六进制 = 寄存器列表 is null
            ? "—"
            : string.Join(
                " ",
                寄存器列表.Skip(偏移量).Take(数量值).Select(值 => $"0x{值:X4}"));

        _日志.LogWarning(
            "点位 {Name} 解码值 {Value} 超出物理范围 [{Min}, {Max}]，原始字：{Hex}。"
            + "请与 PLC 监控同帧比对：若 PLC 该地址的值在此范围内，说明读取时刻该地址无有效数据"
            + "（如程序尚未写入的残留值）或被其它设备（如旧触摸屏）覆盖，而非解码错误",
            标签项.名称, 缩放后, 标签项.最小.Value, 标签项.最大.Value, 十六进制);
    }

    private void 发布方案值列表(读取方案 方案, bool[]? 位, ushort[]? 寄存器列表)
    {
        foreach (var 成员项 in 方案.成员集合)
        {
            try
            {
                object 原始值;

                if (位 is not null)
                {
                    if (成员项.偏移 < 0 || 成员项.偏移 >= 位.Length)
                    {
                        // 短读越界：发布"无效"而不是"确定的断开"——界面必须能看出数据不可信，
                        // 否则现场会看到一个"明明断开、查无实处"的假状态
                        _值列表.发布(标签新值.无效(成员项.标签.名称));
                        _日志.LogDebug("点位 {Name} 位数据越界（偏移 {Offset}，读回 {Length} 位），已发布无效",
                            成员项.标签.名称, 成员项.偏移, 位.Length);
                        continue;
                    }

                    原始值 = 位[成员项.偏移];
                }
                else if (寄存器列表 is not null && 成员项.位序号 >= 0)
                {
                    // D/R 字内位：读取的是整字，按位取值发布布尔
                    if (成员项.偏移 < 0 || 成员项.偏移 >= 寄存器列表.Length)
                    {
                        _值列表.发布(标签新值.无效(成员项.标签.名称));
                        _日志.LogDebug("点位 {Name} 字内位数据越界（偏移 {Offset}，读回 {Length} 字），已发布无效",
                            成员项.标签.名称, 成员项.偏移, 寄存器列表.Length);
                        continue;
                    }

                    原始值 = ((寄存器列表[成员项.偏移] >> 成员项.位序号) & 1) != 0;
                }
                else if (寄存器列表 is not null)
                {
                    if (成员项.偏移 < 0 || 成员项.偏移 + 成员项.寄存器数量 > 寄存器列表.Length)
                    {
                        // 短读（PLC 返回字数不足）：发布"无效"并留痕。
                        // 曾经的 Math.Min 切片在 偏移 > Length 时算出负长度，
                        // AsSpan 抛 ArgumentOutOfRangeException 被外层 catch 吞掉——
                        // 现场表现是"某个点周期性变灰且无线索"
                        _值列表.发布(标签新值.无效(成员项.标签.名称));
                        _日志.LogDebug("点位 {Name} 寄存器数据不足（需要 {Need} 字，读回 {Actual} 字），已发布无效",
                            成员项.标签.名称, 成员项.寄存器数量, 寄存器列表.Length);
                        continue;
                    }

                    var 切片 = 寄存器列表.AsSpan(成员项.偏移, 成员项.寄存器数量);
                    原始值 = 寄存器编解码器.解码(成员项.标签.数据类型, 切片, 成员项.标签.字顺序);
                }
                else
                {
                    continue;
                }

                var 数值 = Convert.ToDouble(原始值, System.Globalization.CultureInfo.InvariantCulture);
                var 缩放后 = 数值 * 成员项.标签.缩放 + 成员项.标签.偏移;

                告警范围(成员项.标签, 寄存器列表, 成员项.偏移, 成员项.寄存器数量, 缩放后);

                _值列表.发布(new 标签新值(成员项.标签.名称, 原始值, 缩放后, DateTime.Now, true));
            }
            catch (Exception 异常)
            {
                _日志.LogDebug(异常, "解码点位 {Name} 失败", 成员项.标签.名称);
                _值列表.发布(标签新值.无效(成员项.标签.名称));
            }
        }
    }
}
