using System.Text.Json;
using Microsoft.Extensions.Logging;
using 上位机.核心.模型;

namespace 上位机.业务.配置;

/// <summary>
/// 现场老配置的「结构升级」：把点胶点表从 <b>30 点升到 50 点</b>（2026-11）。
///
/// <para><b>为什么必须有这一步：</b>发布包只带 <c>出厂配置/</c> 模板，覆盖升级<b>不会</b>动现场的
/// <c>config/</c>。老现场的 <c>recipe-config.json</c> 里点胶仍是 30 点、Y/Z 基址仍是 2760/2820 ——
/// 新程序照旧按配置跑，现场就"装了新版本却还是 30 个点"。要人工去「编辑地址」改三处，
/// 十台机台就会漏掉三台。</para>
///
/// <para><b>只做文本级最小改动：</b>不重新序列化整个 JSON（那会重排字段、丢掉模型里没有的键），
/// 只在点胶表的对象块内替换三处值：<c>点数 30→50</c>、<c>Y 基址 2760→2800</c>、
/// <c>Z 基址 2820→2900</c>。其余内容一个字节都不动，并沿用 <see cref="原子文件"/> 留 <c>.bak</c>。</para>
///
/// <para><b>三条保守规则（宁可不动，也不要把地址改错）：</b></para>
/// <list type="number">
///   <item>已经是 50 点或更多 → 什么都不做（幂等）；</item>
///   <item>点数不是 30 → 不动（现场自己算过账，交给人工）；</item>
///   <item>X/Y/Z 基址不是出厂旧值 2700/2760/2820 → 不动并记警告
///   （此时照搬 2800/2900 可能与现场别的元件撞车，属于"必须有人看一眼"的情况）。</item>
/// </list>
/// </summary>
public static class 配方配置迁移
{
    /// <summary>目标点数（与出厂配置一致）。</summary>
    public const int 点胶目标点数 = 50;

    /// <summary>出厂旧布局（30 点时代）。</summary>
    private const int 旧基址X = 2700;
    private const int 旧基址Y = 2760;
    private const int 旧基址Z = 2820;

    /// <summary>新布局（50 点：每列 100 字）。</summary>
    private const int 新基址Y = 2800;
    private const int 新基址Z = 2900;

    /// <summary>
    /// 就地升级 <paramref name="文件路径"/> 指向的 <c>recipe-config.json</c>。
    /// </summary>
    /// <returns>发生了迁移时返回给操作员/日志看的一句话；不需要动或条件不满足时返回 <c>null</c>。</returns>
    public static string? 应用(string 文件路径, ILogger? 日志 = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(文件路径);

        if (!File.Exists(文件路径))
        {
            return null;
        }

        string 文本;

        try
        {
            文本 = File.ReadAllText(文件路径);
        }
        catch (IOException 异常)
        {
            日志?.LogWarning(异常, "读取 {Path} 失败，跳过点胶点数迁移", 文件路径);
            return null;
        }
        catch (UnauthorizedAccessException 异常)
        {
            日志?.LogWarning(异常, "读取 {Path} 无权限，跳过点胶点数迁移", 文件路径);
            return null;
        }

        配方配置? 配置;

        try
        {
            配置 = JsonSerializer.Deserialize<配方配置>(文本, JSON配置.读取);
        }
        catch (JsonException 异常)
        {
            // 配置本身坏了：更上层的加载器负责"从 .bak 恢复 / 登记健康告警"，这里不抢它的活
            日志?.LogWarning(异常, "{Path} 解析失败，跳过点胶点数迁移", 文件路径);
            return null;
        }

        if (配置?.点表.查找("点胶") is not { } 点胶)
        {
            return null;
        }

        if (点胶.点数 >= 点胶目标点数)
        {
            return null;   // 已是新布局（或现场自己扩过）：幂等
        }

        if (点胶.点数 != 30)
        {
            日志?.LogWarning(
                "点胶点表当前 {Current} 点（既不是 30 也不是 ≥{Target}），不自动迁移，请人工核对地址",
                点胶.点数, 点胶目标点数);
            return null;
        }

        var 基址X = 点胶.查找列("X")?.基址 ?? 0;
        var 基址Y = 点胶.查找列("Y")?.基址 ?? 0;
        var 基址Z = 点胶.查找列("Z")?.基址 ?? 0;

        if (基址X != 旧基址X || 基址Y != 旧基址Y || 基址Z != 旧基址Z)
        {
            日志?.LogWarning(
                "点胶点表仍是 30 点，但列基址不是出厂旧值（X{DX}/Y{DY}/Z{DZ}），"
                + "未自动迁移到 50 点——请人工确认 50 点后的列地址（出厂布局为 X2700/Y2800/Z2900）",
                基址X, 基址Y, 基址Z);
            return null;
        }

        var 改后 = 替换点胶块(文本, out var 点数处数, out var y处数, out var z处数);

        // 期望形态：点数 1 处；Y 与 Z 各 1 处（新版只写"列"）或各 2 处（老版还带派生属性"坐标列"）。
        // 对不上说明文件结构超出预期（例如手工改坏），宁可不写也不要把地址改出半截。
        if (改后 is null || 点数处数 != 1 || y处数 < 1 || y处数 != z处数)
        {
            日志?.LogWarning(
                "点胶点表的升级未执行：点数处 {Point}、Y 处 {Y}、Z 处 {Z}（请人工核对 {Path}）",
                点数处数, y处数, z处数, 文件路径);
            return null;
        }

        try
        {
            // 原子写 + 旧文件留 .bak：改坏了现场还能回滚
            原子文件.异步写入全部文本(文件路径, 改后).GetAwaiter().GetResult();
        }
        catch (IOException 异常)
        {
            日志?.LogWarning(异常, "写回 {Path} 失败，点胶点数未迁移", 文件路径);
            return null;
        }
        catch (UnauthorizedAccessException 异常)
        {
            日志?.LogWarning(异常, "写回 {Path} 无权限，点胶点数未迁移", 文件路径);
            return null;
        }

        var 说明 = $"点胶示教已由 30 点升级为 {点胶目标点数} 点："
                   + $"Y 基址 D{旧基址Y}→D{新基址Y}、Z 基址 D{旧基址Z}→D{新基址Z}"
                   + "（原文件已备份为 .bak）";

        日志?.LogInformation("配置升级：{Summary}（{Path}）", 说明, 文件路径);
        return 说明;
    }

    /// <summary>
    /// 在点胶表的对象块内做三处文本替换（保持文件其余部分逐字节不变）。
    /// </summary>
    /// <returns>替换后的全文；找不到点胶块时返回 <c>null</c>。</returns>
    private static string? 替换点胶块(
        string 文本, out int 点数处数, out int y处数, out int z处数)
    {
        y处数 = 0;
        z处数 = 0;

        if (!尝试定位点胶块(文本, out var 起, out var 止))
        {
            点数处数 = 0;
            return null;
        }

        var 块 = 文本[(起 + 1)..止];

        点数处数 = 替换次数(块, "\"点数\": 30", "\"点数\": 50", out var 块甲);
        y处数 = 替换次数(块甲, $"\"基址\": {旧基址Y}", $"\"基址\": {新基址Y}", out var 块乙);
        z处数 = 替换次数(块乙, $"\"基址\": {旧基址Z}", $"\"基址\": {新基址Z}", out var 块丙);

        return 文本[..(起 + 1)] + 块丙 + 文本[止..];
    }

    private static int 替换次数(string 源, string 旧值, string 新值, out string 结果)
    {
        var 次数 = 0;
        var 位置 = 0;

        while ((位置 = 源.IndexOf(旧值, 位置, StringComparison.Ordinal)) >= 0)
        {
            次数++;
            位置 += 旧值.Length;
        }

        结果 = 次数 == 0 ? 源 : 源.Replace(旧值, 新值, StringComparison.Ordinal);
        return 次数;
    }

    /// <summary>
    /// 定位 <c>"键": "点胶"</c> 所在的那个 JSON 对象块的 <c>{ … }</c> 位置。
    /// <para>不用正则/缩进假设：按大括号配对扫一遍（跳过字符串字面量），
    /// 这样现场手工改过缩进或键顺序也能定位。</para>
    /// </summary>
    private static bool 尝试定位点胶块(string 文本, out int 起, out int 止)
    {
        起 = -1;
        止 = -1;

        var 目标 = 文本.IndexOf("\"键\": \"点胶\"", StringComparison.Ordinal);

        if (目标 < 0)
        {
            return false;
        }

        var 栈 = new Stack<int>();

        for (var i = 0; i < 文本.Length; i++)
        {
            if (i == 目标 && 栈.Count > 0 && 起 < 0)
            {
                起 = 栈.Peek();   // 此刻栈顶就是"包含该键"的对象
            }

            var 字符 = 文本[i];

            if (字符 == '"')
            {
                i = 字符串终点(文本, i);
                continue;
            }

            if (字符 == '{')
            {
                栈.Push(i);
                continue;
            }

            if (字符 != '}' || 栈.Count == 0)
            {
                continue;
            }

            var 打开位置 = 栈.Pop();

            if (打开位置 == 起)
            {
                止 = i;
                return true;
            }
        }

        return false;
    }

    /// <summary>返回从 <paramref name="起点"/> 的引号开始的字符串字面量的<b>结束引号</b>下标。</summary>
    private static int 字符串终点(string 文本, int 起点)
    {
        for (var i = 起点 + 1; i < 文本.Length; i++)
        {
            if (文本[i] != '"')
            {
                continue;
            }

            // 数一数前面有几个连续反斜杠：偶数说明这个引号没有被转义
            var 反斜杠 = 0;

            for (var 甲 = i - 1; 甲 > 起点 && 文本[甲] == '\\'; 甲--)
            {
                反斜杠++;
            }

            if (反斜杠 % 2 == 0)
            {
                return i;
            }
        }

        return 文本.Length - 1;
    }
}
