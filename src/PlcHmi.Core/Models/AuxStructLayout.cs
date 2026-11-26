namespace PlcHmi.Core.Models;

/// <summary>
/// 点胶「单点工艺参数」结构体的<b>成员排布规则</b>（2026-10-21 现场口径）。
///
/// <para><b>为什么必须统一算：</b>结构体成员的地址 = 基址 + 点序号 × 长度 + 偏移，
/// 而偏移取决于<b>前面所有成员占了多少字</b>。现场把"抬针高度"从 REAL(2 字) 改成 INT(1 字) 后，
/// 后面的"点胶速度"必须从偏移 6 前移到 5（第 1 点 D3506 → D3505）——
/// 若各成员仍按旧的固定偏移显示，上位机就会指着一段 PLC 根本没有的地址。</para>
///
/// <para><b>排布规则：</b>按成员在配置里的先后顺序<b>紧凑累加</b>（无空洞）：
/// 偏移[n] = 偏移[n-1] + 成员[n-1] 的寄存器数；结构体长度 = 全部成员寄存器数之和。
/// 例：REAL(0–1) / INT(2) / INT(3) / INT(4) / REAL(5–6) → 长度 7，点胶速度 D3505。</para>
///
/// <para><b>为什么不插入对齐填充：</b>现场口径就是"改一个成员的类型，后面的成员依次前移"
/// （点胶速度必须落在 D3505）。填充会让地址与 PLC 程序对不上，而且现场难以核对。</para>
/// </summary>
public static class AuxAddressStructureLayout
{
    /// <summary>结构体基址的出厂缺省（点胶单点参数数组起点）。</summary>
    public const int defaultBaseAddress = 3500;

    /// <summary>取某页分组下的结构体成员（按配置顺序，即结构体内顺序）。</summary>
    public static IReadOnlyList<AuxAddressItem> Member(IReadOnlyList<AuxAddressItem> allItem, string Group) =>
        allItem
            .Where(Item => Item.StructMember && GroupMatch(Item.Group, Group))
            .ToList();

    /// <summary>
    /// 重排某页分组下全部结构体成员的偏移与长度，并回写每人的"第 1 点地址"（<see cref="AuxAddressItem.Address"/>）。
    /// <para>基址取成员里第一个大于 0 的 <see cref="AuxAddressItem.StructBaseAddress"/>；都没有则用
    /// <paramref name="defaultBaseAddressValue"/>。基址统一到所有成员（否则同组成员会算出不同数组）。</para>
    /// </summary>
    /// <returns>重排后的结构体长度（字）；没有成员时返回 0。</returns>
    public static int ReLayout(
        IReadOnlyList<AuxAddressItem> allItem, string Group, int defaultBaseAddressValue = defaultBaseAddress)
    {
        ArgumentNullException.ThrowIfNull(allItem);

        var memberList = Member(allItem, Group);

        if (memberList.Count == 0)
        {
            return 0;
        }

        var BaseAddress = memberList.Select(Item => Item.StructBaseAddress).FirstOrDefault(Value => Value > 0);

        if (BaseAddress <= 0)
        {
            BaseAddress = defaultBaseAddressValue > 0 ? defaultBaseAddressValue : defaultBaseAddress;
        }

        var Offset = 0;

        foreach (var Item in memberList)
        {
            Item.StructBaseAddress = BaseAddress;
            Item.StructInnerOffset = Offset;
            Offset += Item.RegisterCount;
        }

        foreach (var Item in memberList)
        {
            Item.StructLength = Offset;

            // 地址字段对成员只是"第 1 点的实际地址"（界面与核对用），寻址始终按 基址+偏移 实时算
            Item.Address = $"D{BaseAddress + Item.StructInnerOffset}";
        }

        return Offset;
    }

    /// <summary>分组匹配（空分组视为全局元件，与页面地址服务同口径）。</summary>
    private static bool GroupMatch(string componentGroup, string PageGroup) =>
        string.IsNullOrWhiteSpace(componentGroup)
        || string.IsNullOrWhiteSpace(PageGroup)
        || string.Equals(componentGroup, PageGroup, StringComparison.OrdinalIgnoreCase);
}
