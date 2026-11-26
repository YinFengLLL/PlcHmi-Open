using PlcHmi.Core.Models;

namespace PlcHmi.Business.Configuration;

/// <summary>
/// 「当前浮高」显示地址的出厂缺省（2026-10-24 现场要求）。
///
/// <para><b>为什么需要补齐：</b>覆盖升级不会覆盖现场的 <c>recipe-config.json</c>，
/// 老文件里没有 <c>当前浮高地址</c> 这一项。不补的话，装了新版本锁附页也看不到浮高、
/// 「保存坐标点」也不会写浮高列——现场只会觉得"新功能没生效"。</para>
///
/// <para>只补零值，绝不覆盖现场已经改过的地址；同时把「浮高」列纳入保存坐标列
/// （<see cref="PointTableDefinition.SaveCoordinateColumn"/> 为空表示"全部位置列"，无需处理）。</para>
/// </summary>
internal static class CurrentFloatHeightDefault
{
    /// <summary>出厂地址：D240（REAL，占 D240/D241）。</summary>
    public const int defaultAddress = 240;

    /// <summary>缺省对应的点表键。</summary>
    public const string defaultTableKey = "锁附";

    /// <summary>浮高列键。</summary>
    public const string floatHeightColumnKey = "浮高";

    /// <summary>按缺省补齐；返回补齐项数（0 = 无需补齐）。</summary>
    public static int App(RecipeConfig configPairObject)
    {
        ArgumentNullException.ThrowIfNull(configPairObject);

        var Table = configPairObject.PointTable.Find(defaultTableKey);

        if (Table is null)
        {
            return 0;
        }

        var fill = 0;

        if (Table.CurrentFloatHeightAddress <= 0)
        {
            Table.CurrentFloatHeightAddress = defaultAddress;
            fill++;
        }

        // 保存坐标列：空 = "所有带位置标签的列"，已含浮高，不用动；
        // 显式列了 X/Y 的老配置要补上浮高，否则「保存坐标点」不会写它
        if (Table.CurrentFloatHeightAddress > 0
            && Table.FindColumn(floatHeightColumnKey) is not null
            && Table.SaveCoordinateColumn.Count > 0
            && !Table.SaveCoordinateColumn.Any(keyValue => string.Equals(keyValue, floatHeightColumnKey, StringComparison.OrdinalIgnoreCase)))
        {
            Table.SaveCoordinateColumn.Add(floatHeightColumnKey);
            fill++;
        }

        return fill;
    }
}
