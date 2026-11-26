using 上位机.核心.模型;

namespace 上位机.业务.配置;

/// <summary>
/// 「当前浮高」显示地址的出厂缺省（2026-10-24 现场要求）。
///
/// <para><b>为什么需要补齐：</b>覆盖升级不会覆盖现场的 <c>recipe-config.json</c>，
/// 老文件里没有 <c>当前浮高地址</c> 这一项。不补的话，装了新版本锁附页也看不到浮高、
/// 「保存坐标点」也不会写浮高列——现场只会觉得"新功能没生效"。</para>
///
/// <para>只补零值，绝不覆盖现场已经改过的地址；同时把「浮高」列纳入保存坐标列
/// （<see cref="点表定义.保存坐标列"/> 为空表示"全部位置列"，无需处理）。</para>
/// </summary>
internal static class 当前浮高缺省
{
    /// <summary>出厂地址：D240（REAL，占 D240/D241）。</summary>
    public const int 缺省地址 = 240;

    /// <summary>缺省对应的点表键。</summary>
    public const string 缺省表键 = "锁附";

    /// <summary>浮高列键。</summary>
    public const string 浮高列键 = "浮高";

    /// <summary>按缺省补齐；返回补齐项数（0 = 无需补齐）。</summary>
    public static int 应用(配方配置 配置对象)
    {
        ArgumentNullException.ThrowIfNull(配置对象);

        var 表 = 配置对象.点表.查找(缺省表键);

        if (表 is null)
        {
            return 0;
        }

        var 补齐 = 0;

        if (表.当前浮高地址 <= 0)
        {
            表.当前浮高地址 = 缺省地址;
            补齐++;
        }

        // 保存坐标列：空 = "所有带位置标签的列"，已含浮高，不用动；
        // 显式列了 X/Y 的老配置要补上浮高，否则「保存坐标点」不会写它
        if (表.当前浮高地址 > 0
            && 表.查找列(浮高列键) is not null
            && 表.保存坐标列.Count > 0
            && !表.保存坐标列.Any(键值 => string.Equals(键值, 浮高列键, StringComparison.OrdinalIgnoreCase)))
        {
            表.保存坐标列.Add(浮高列键);
            补齐++;
        }

        return 补齐;
    }
}
