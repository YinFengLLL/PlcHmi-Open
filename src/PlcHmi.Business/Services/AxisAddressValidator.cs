using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// 轴地址校验器。
///
/// <para><b>2026-10-12 按现场要求放宽：取消地址范围限制。</b>
/// 原先的三重区间约束（轴参数区 D1200–D1799、PLC 保留区、与 tags.json 其它点位比对）
/// 全部移除——现场地址分配表随设备型号变化，硬编码区间会把正确地址判成错误。
/// 详见 <c>docs/04</c> 变更记录。</para>
///
/// <para><b>仍然保留的判定</b>（这些不是"范围"，而是录入错误）：
/// 地址能否解析、元素类型是否正确（速度/位置必须 D 区、命令必须 M 区或 D/R 字内位），
/// 以及<b>本次提交内</b>的地址重叠。校验必须落在服务层——界面隐藏入口只是防误触。</para>
/// </summary>
internal sealed class AxisAddressValidator
{
    private readonly IAddressParser _parser;

    public AxisAddressValidator(IAddressParser Parser) => _parser = Parser;

    /// <summary>校验一张候选轴地址表；返回全部错误（空集合表示通过）。</summary>
    public AxisAddressValidate Validate(AxisAddressTable candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var errorList = new List<string>();
        var beOccupied = new List<OccupiedRange>();

        foreach (var EntryItem in candidate.AxisList)
        {
            var axisNumber = EntryItem.Axis;

            AddRegister(errorList, beOccupied, $"{axisNumber} 轴定位速度", EntryItem.PositioningSpeedAddress, SoftElement.D, 2, allowEmpty: false);

            // 点动档位：六轴共用同一个速度档位地址（2026-10-13 现场要求），
            // 因此不进逐轴占用表（六轴相同地址不算重叠），但仍校验其可解析。
            // 一致性在下面的"档位地址必须六轴一致"检查里保证。
            AddRegister(errorList, new List<OccupiedRange>(), $"{axisNumber} 轴点动档位",
                EntryItem.JogSpeedLevelAddress, SoftElement.D, 1, allowEmpty: false);

            // 当前位置允许留空：某些轴（如点胶 Z、ZZ）没有独立位置回读
            AddRegister(errorList, beOccupied, $"{axisNumber} 轴当前位置", EntryItem.PositionAddress, SoftElement.D, 2, allowEmpty: true);

            AddCoil(errorList, beOccupied, $"{axisNumber} 轴回零", EntryItem.HomeAddress);
            AddCoil(errorList, beOccupied, $"{axisNumber} 轴正向点动", EntryItem.JogForwardAddress);
            AddCoil(errorList, beOccupied, $"{axisNumber} 轴反向点动", EntryItem.JogReverseAddress);
        }

        // 点动档位必须六轴共用同一个地址（切档一次，六轴同时生效）
        var gearAddressCollection = candidate.AxisList
            .Select(EntryItem => EntryItem.JogSpeedLevelAddress.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (gearAddressCollection.Count > 1)
        {
            errorList.Add(
                "点动档位地址必须六轴共用同一个"
                + $"（当前 {gearAddressCollection.Count} 个：{string.Join("、", gearAddressCollection)}）。");
        }

        CheckOverlap(errorList, beOccupied);

        return new AxisAddressValidate { IsValid = errorList.Count == 0, ErrorCollection = errorList };
    }

    // ── 校验辅助 ────────────────────────────────────────────────────────

    /// <summary>一条已占用的地址区间（用于重叠检查）。</summary>
    private sealed record OccupiedRange(string Tag, SoftElement ElementItem, int Start, int Count, int BitIndex = -1)
    {
        public int EndBoundary => Start + Count;
    }

    /// <summary>
    /// 判断两个已占用范围是否冲突。
    /// <para>同字的两个字内位（如 D1210.2 与 D1210.3）互不冲突；
    /// 字内位与整字读写仍判冲突——整字写会把位覆盖掉。</para>
    /// </summary>
    private static bool IsConflict(OccupiedRange item, OccupiedRange item2)
    {
        if (item.ElementItem != item2.ElementItem)
        {
            return false;
        }

        if (item.BitIndex >= 0 && item2.BitIndex >= 0)
        {
            return item.Start == item2.Start && item.BitIndex == item2.BitIndex;
        }

        return item.Start < item2.EndBoundary && item2.Start < item.EndBoundary;
    }

    private void AddRegister(
        List<string> errorList,
        List<OccupiedRange> beOccupied,
        string TagName,
        string addressValue,
        SoftElement expected,
        int registerCountItem,
        bool allowEmpty)
    {
        if (string.IsNullOrWhiteSpace(addressValue))
        {
            if (!allowEmpty)
            {
                errorList.Add($"{TagName}：地址不能为空。");
            }

            return;
        }

        var Text = FillAllPrefix(addressValue.Trim(), expected);

        if (!_parser.TryParse(Text, out var PlcAddress) || PlcAddress is null)
        {
            errorList.Add($"{TagName}：「{addressValue}」不是有效的软元件地址。");
            return;
        }

        if (PlcAddress.ElementItem != expected)
        {
            errorList.Add($"{TagName}：「{addressValue}」必须是 {expected} 区地址（当前解析为 {PlcAddress.ElementItem} 区）。");
            return;
        }

        beOccupied.Add(new OccupiedRange(TagName, PlcAddress.ElementItem, PlcAddress.ModbusAddress, registerCountItem));
    }

    /// <summary>
    /// 补全省略的软元件前缀。
    ///
    /// <para><b>为什么需要：</b>只认 D 区的字段（定位速度 / 当前位置 / 点动档位）在界面上显示为
    /// <c>D1202</c>，但现场习惯是直接填寄存器号（把 <c>D1202</c> 全选改成 <c>1252</c>）。
    /// 而服务层的写入路径本来就是宽松解析（<c>TrimStart('D')</c>）——两边口径不一致时，
    /// 会出现"页面报『不是有效的软元件地址』、服务其实完全能写"的分裂，
    /// 现场表现就是"改了轴速度地址保存不了"（2026-10-14 反馈）。</para>
    ///
    /// <para>只对 <b>期望就是 D 区</b> 且输入全是数字的情况补 <c>D</c>：命令类字段（点动 / 回零）
    /// 允许 M 区线圈或 D 区字内位，裸数字无法判断意图，仍要求写明前缀。</para>
    /// </summary>
    private static string FillAllPrefix(string addressValue, SoftElement expected)
    {
        if (expected != SoftElement.D || addressValue.Length == 0)
        {
            return addressValue;
        }

        return addressValue.All(character => char.IsAsciiDigit(character)) ? "D" + addressValue : addressValue;
    }

    private void AddCoil(List<string> errorList, List<OccupiedRange> beOccupied, string TagName, string addressValue)
    {
        if (string.IsNullOrWhiteSpace(addressValue))
        {
            // 回零地址允许留空：留空即表示该轴不提供回零按钮
            return;
        }

        if (!_parser.TryParse(addressValue, out var PlcAddress) || PlcAddress is null)
        {
            errorList.Add($"{TagName}：「{addressValue}」不是有效的软元件地址。");
            return;
        }

        // 点动/回零命令允许两种写法：M 区线圈，或 D/R 字内位
        // （汇川位打包：BOOL 占 D 的一位，如 X 轴正向点动 D1210.2）。
        // 上位机对 D 位走"读-改-写"，不直接驱动 Y 输出，硬约束保持不变。
        if (PlcAddress.IsIntraWordBit && PlcAddress.ElementItem is SoftElement.D or SoftElement.R)
        {
            beOccupied.Add(new OccupiedRange(TagName, PlcAddress.ElementItem, PlcAddress.ModbusAddress, 1, PlcAddress.BitIndex));
            return;
        }

        if (PlcAddress.ElementItem != SoftElement.M)
        {
            errorList.Add(
                $"{TagName}：「{addressValue}」必须是 M 区地址或 D/R 区字内位（如 D1210.2，当前解析为 {PlcAddress.ElementItem} 区）。"
                + "上位机只置位内部继电器/命令位，由 PLC 程序决定何时驱动 Y 输出。");
            return;
        }

        beOccupied.Add(new OccupiedRange(TagName, PlcAddress.ElementItem, PlcAddress.ModbusAddress, 1));
    }

    private static void CheckOverlap(List<string> errorList, List<OccupiedRange> beOccupied)
    {
        // 只比同软元件区：D100 与 M100 的 Modbus 地址都是 100，跨区比较会误报
        for (var Entry = 0; Entry < beOccupied.Count; Entry++)
        {
            for (var innerLayerIndex = Entry + 1; innerLayerIndex < beOccupied.Count; innerLayerIndex++)
            {
                var item = beOccupied[Entry];
                var item2 = beOccupied[innerLayerIndex];

                if (item.ElementItem != item2.ElementItem)
                {
                    continue;
                }

                if (IsConflict(item, item2))
                {
                    errorList.Add(
                        $"{item.Tag}（{item.ElementItem}{item.Start}）与 {item2.Tag}（{item2.ElementItem}{item2.Start}）地址重叠。");
                }
            }
        }
    }
}
