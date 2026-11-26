using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PlcHmi.Communication.Modbus;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// 轮询引擎的「值发布与范围诊断」部分。
///
/// <para><b>为什么拆成 partial 文件</b>：本类承担了采集计划编排、分组调度与实际解码发布三件事，
/// 合在一个文件里已超过 400 行审查阈值 —— 审查者很难在心里维持完整上下文，回归缺陷极易漏过。
/// 这里只放"把读回来的原始字变成一个已发布的点位值"，以及伴随的物理范围诊断；
/// 采集计划与调度仍在 <c>PollingEngine.cs</c>。</para>
/// </summary>
public sealed partial class PollingEngine
{
    // ── 解码值物理范围诊断（2026-09-12 现场乱值排查后加）────────────────
    // 现场 HMI 曾显示 3.7e26：复核后解码实现无缺陷，是 PLC D 区当时的残留数据。
    // 为让下次"界面显示离谱值"能一步定位，解码值超出点位声明的物理范围时记 Warning
    // 并附原始字十六进制——与 PLC 监控同帧对照即可分清"数据源垃圾/被覆盖"还是"解码错"。

    // 已告警点位集合。三个采集分组线程并发读写，用 ConcurrentDictionary 走无锁快路径：
    // "值在范围内"是每点每周期的常态，此前每次都要进 lock 与另外两个分组争用
    // （100ms 周期 × 数百点 = 每秒数千次加锁）。
    private readonly ConcurrentDictionary<string, byte> _rangeWarned = new();

    /// <summary>
    /// 解码值超出点位声明的 [Min, Max] 时记一条带原始字十六进制的 Warning。
    /// <para>同一点位持续越界只记一次，恢复正常后重新武装，避免 100ms 轮询刷爆日志；
    /// 越界值照常发布，只诊断不拦截——日志是线索，不是过滤器。</para>
    /// </summary>
    private void AlarmRange(TagDefinition tagItem, ushort[]? registerList, int OffsetValue, int countValue, double scaleAfter)
    {
        // 线圈位路径没有寄存器可对照，且 Bool 点位也不会配物理范围——直接跳过
        if (tagItem.Min is null || tagItem.Max is null || registerList is null)
        {
            return;
        }

        if (scaleAfter >= tagItem.Min.Value && scaleAfter <= tagItem.Max.Value)
        {
            // 值在范围内是常态：仅当该点位此前告警过（需要解除武装）时才动集合
            _rangeWarned.TryRemove(tagItem.Name, out _);
            return;
        }

        // 越界是低频路径：TryAdd 失败 = 已告警过，直接返回
        if (!_rangeWarned.TryAdd(tagItem.Name, 0))
        {
            return;
        }

        var hex = registerList is null
            ? "—"
            : string.Join(
                " ",
                registerList.Skip(OffsetValue).Take(countValue).Select(Value => $"0x{Value:X4}"));

        _log.LogWarning(
            "点位 {Name} 解码值 {Value} 超出物理范围 [{Min}, {Max}]，原始字：{Hex}。"
            + "请与 PLC 监控同帧比对：若 PLC 该地址的值在此范围内，说明读取时刻该地址无有效数据"
            + "（如程序尚未写入的残留值）或被其它设备（如旧触摸屏）覆盖，而非解码错误",
            tagItem.Name, scaleAfter, tagItem.Min.Value, tagItem.Max.Value, hex);
    }

    private void RaiseLayoutPlanValueList(ReadPlan plan, bool[]? Bit, ushort[]? registerList)
    {
        foreach (var memberItem in plan.memberCollection)
        {
            try
            {
                object SourceValue;

                if (Bit is not null)
                {
                    if (memberItem.Offset < 0 || memberItem.Offset >= Bit.Length)
                    {
                        // 短读越界：发布"无效"而不是"确定的断开"——界面必须能看出数据不可信，
                        // 否则现场会看到一个"明明断开、查无实处"的假状态
                        _valueList.RaiseLayout(TagNewValue.Invalid(memberItem.Tag.Name));
                        _log.LogDebug("点位 {Name} 位数据越界（偏移 {Offset}，读回 {Length} 位），已发布无效",
                            memberItem.Tag.Name, memberItem.Offset, Bit.Length);
                        continue;
                    }

                    SourceValue = Bit[memberItem.Offset];
                }
                else if (registerList is not null && memberItem.BitIndex >= 0)
                {
                    // D/R 字内位：读取的是整字，按位取值发布布尔
                    if (memberItem.Offset < 0 || memberItem.Offset >= registerList.Length)
                    {
                        _valueList.RaiseLayout(TagNewValue.Invalid(memberItem.Tag.Name));
                        _log.LogDebug("点位 {Name} 字内位数据越界（偏移 {Offset}，读回 {Length} 字），已发布无效",
                            memberItem.Tag.Name, memberItem.Offset, registerList.Length);
                        continue;
                    }

                    SourceValue = ((registerList[memberItem.Offset] >> memberItem.BitIndex) & 1) != 0;
                }
                else if (registerList is not null)
                {
                    if (memberItem.Offset < 0 || memberItem.Offset + memberItem.RegisterCount > registerList.Length)
                    {
                        // 短读（PLC 返回字数不足）：发布"无效"并留痕。
                        // 曾经的 Math.Min 切片在 偏移 > Length 时算出负长度，
                        // AsSpan 抛 ArgumentOutOfRangeException 被外层 catch 吞掉——
                        // 现场表现是"某个点周期性变灰且无线索"
                        _valueList.RaiseLayout(TagNewValue.Invalid(memberItem.Tag.Name));
                        _log.LogDebug("点位 {Name} 寄存器数据不足（需要 {Need} 字，读回 {Actual} 字），已发布无效",
                            memberItem.Tag.Name, memberItem.RegisterCount, registerList.Length);
                        continue;
                    }

                    var Segment = registerList.AsSpan(memberItem.Offset, memberItem.RegisterCount);
                    SourceValue = RegisterCodec.Decode(memberItem.Tag.DataType, Segment, memberItem.Tag.WordOrder);
                }
                else
                {
                    continue;
                }

                var NumericValue = Convert.ToDouble(SourceValue, System.Globalization.CultureInfo.InvariantCulture);
                var scaleAfter = NumericValue * memberItem.Tag.Scale + memberItem.Tag.Offset;

                AlarmRange(memberItem.Tag, registerList, memberItem.Offset, memberItem.RegisterCount, scaleAfter);

                _valueList.RaiseLayout(new TagNewValue(memberItem.Tag.Name, SourceValue, scaleAfter, DateTime.Now, true));
            }
            catch (Exception exception)
            {
                _log.LogDebug(exception, "解码点位 {Name} 失败", memberItem.Tag.Name);
                _valueList.RaiseLayout(TagNewValue.Invalid(memberItem.Tag.Name));
            }
        }
    }
}
