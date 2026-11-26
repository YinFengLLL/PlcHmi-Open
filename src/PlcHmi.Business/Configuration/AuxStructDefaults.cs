using PlcHmi.Core.Models;

namespace PlcHmi.Business.Configuration;

/// <summary>
/// 点胶「单点工艺参数」结构体的出厂定义，以及<b>老现场配置的自动补齐</b>。
///
/// <para><b>为什么需要它（2026-10-21 现场事故）：</b>覆盖升级不会覆盖现场的
/// <c>recipe-config.json</c>（那是现场文件），而结构体数组是本次新增的字段。
/// 现场文件里没有 <c>结构体基址/结构体内偏移/结构体长度</c> → 反序列化后全是 0
/// → <see cref="AuxAddressItem.IsStructArray"/> 为 false → 新功能<b>静默失效</b>：
/// 地址不再随点偏移、也不显示"当前为第 N 点"。现场看到的就是"新版装上了但没变化"。</para>
///
/// <para><b>补齐规则（保守，只补零、不覆盖现场值）：</b>仅当三项结构体参数<b>全为 0</b>
/// <b>且</b>该项当前地址正好等于"结构体第一点该字段的地址"时，才按出厂定义填上。
/// 地址对不上的项一律不动——那说明现场有自己的规划，宁可新功能不生效也不能乱改地址。</para>
/// </summary>
public static class AuxAddressStructDefault
{
    /// <summary>结构体基址（点胶单点参数的数组起点，D 区）。</summary>
    public const int BaseAddress = 3500;

    /// <summary>结构体长度（字）：提前关胶 2 + 出胶延时 1 + 关胶后延时 1 + 抬针高度 2 + 点胶速度 2。</summary>
    public const int length = 8;

    /// <summary>
    /// 出厂结构体成员：辅助地址键 → 结构体内字偏移。
    /// <para>顺序与现场 PLC 的 DINT/REAL 排布一致，改这里必须同步改 PLC 程序。</para>
    /// </summary>
    public static IReadOnlyDictionary<string, int> FieldOffset { get; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["点胶-提前关胶"] = 0,      // REAL 占 0–1
            ["点胶-运动速度延时"] = 2,   // INT
            ["点胶-关胶后延时"] = 3,     // INT
            ["点胶-抬针高度"] = 4,       // REAL 占 4–5
            ["点胶-点胶速度"] = 6,       // REAL 占 6–7
        };

    /// <summary>
    /// 给缺少结构体参数的老配置补上出厂定义；返回补齐的条目数（0 = 无需补齐）。
    /// </summary>
    public static int App(RecipeConfig configPairObject)
    {
        ArgumentNullException.ThrowIfNull(configPairObject);

        var fill = 0;

        foreach (var Item in configPairObject.AuxAddress)
        {
            // 老配置有结构体三参数、但没有「结构体成员」标记（2026-10-21 新增的勾选框）：
            // 三者齐全就视为成员。不补这一位，升级后所有单点参数会静默退回固定地址
            if (!Item.StructMember && Item.StructBaseAddress > 0 && Item.StructLength > 0)
            {
                Item.StructMember = true;
                fill++;
                continue;
            }

            if (Item.IsStructArray || !FieldOffset.TryGetValue(Item.Key, out var Offset))
            {
                continue;
            }

            // 只补"地址正好落在结构体第一点该字段上"的项：地址对不上说明现场另有规划
            var shouldAsAddress = $"D{BaseAddress + Offset}";

            if (!string.Equals(Item.Address?.Trim(), shouldAsAddress, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Item.StructMember = true;
            Item.StructBaseAddress = BaseAddress;
            Item.StructInnerOffset = Offset;
            Item.StructLength = length;
            fill++;
        }

        return fill;
    }
}
