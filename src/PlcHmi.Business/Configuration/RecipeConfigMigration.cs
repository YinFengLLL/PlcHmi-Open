using System.Text.Json;
using Microsoft.Extensions.Logging;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Configuration;

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
/// <c>Z 基址 2820→2900</c>。其余内容一个字节都不动，并沿用 <see cref="AtomicFile"/> 留 <c>.bak</c>。</para>
///
/// <para><b>三条保守规则（宁可不动，也不要把地址改错）：</b></para>
/// <list type="number">
///   <item>已经是 50 点或更多 → 什么都不做（幂等）；</item>
///   <item>点数不是 30 → 不动（现场自己算过账，交给人工）；</item>
///   <item>X/Y/Z 基址不是出厂旧值 2700/2760/2820 → 不动并记警告
///   （此时照搬 2800/2900 可能与现场别的元件撞车，属于"必须有人看一眼"的情况）。</item>
/// </list>
/// </summary>
public static class RecipeConfigMigration
{
    /// <summary>目标点数（与出厂配置一致）。</summary>
    public const int dispenseTargetPointCount = 50;

    /// <summary>出厂旧布局（30 点时代）。</summary>
    private const int oldBaseAddressX = 2700;
    private const int oldBaseAddressY = 2760;
    private const int oldBaseAddressZ = 2820;

    /// <summary>新布局（50 点：每列 100 字）。</summary>
    private const int newBaseAddressY = 2800;
    private const int newBaseAddressZ = 2900;

    /// <summary>
    /// 就地升级 <paramref name="filePath"/> 指向的 <c>recipe-config.json</c>。
    /// </summary>
    /// <returns>发生了迁移时返回给操作员/日志看的一句话；不需要动或条件不满足时返回 <c>null</c>。</returns>
    public static string? App(string filePath, ILogger? Log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
        {
            return null;
        }

        string Text;

        try
        {
            Text = File.ReadAllText(filePath);
        }
        catch (IOException exception)
        {
            Log?.LogWarning(exception, "读取 {Path} 失败，跳过点胶点数迁移", filePath);
            return null;
        }
        catch (UnauthorizedAccessException exception)
        {
            Log?.LogWarning(exception, "读取 {Path} 无权限，跳过点胶点数迁移", filePath);
            return null;
        }

        RecipeConfig? Config;

        try
        {
            Config = JsonSerializer.Deserialize<RecipeConfig>(Text, JsonConfig.Read);
        }
        catch (JsonException exception)
        {
            // 配置本身坏了：更上层的加载器负责"从 .bak 恢复 / 登记健康告警"，这里不抢它的活
            Log?.LogWarning(exception, "{Path} 解析失败，跳过点胶点数迁移", filePath);
            return null;
        }

        if (Config?.PointTable.Find("点胶") is not { } dispense)
        {
            return null;
        }

        if (dispense.PointCount >= dispenseTargetPointCount)
        {
            return null;   // 已是新布局（或现场自己扩过）：幂等
        }

        if (dispense.PointCount != 30)
        {
            Log?.LogWarning(
                "点胶点表当前 {Current} 点（既不是 30 也不是 ≥{Target}），不自动迁移，请人工核对地址",
                dispense.PointCount, dispenseTargetPointCount);
            return null;
        }

        var baseAddressX = dispense.FindColumn("X")?.BaseAddress ?? 0;
        var baseAddressY = dispense.FindColumn("Y")?.BaseAddress ?? 0;
        var baseAddressZ = dispense.FindColumn("Z")?.BaseAddress ?? 0;

        if (baseAddressX != oldBaseAddressX || baseAddressY != oldBaseAddressY || baseAddressZ != oldBaseAddressZ)
        {
            Log?.LogWarning(
                "点胶点表仍是 30 点，但列基址不是出厂旧值（X{DX}/Y{DY}/Z{DZ}），"
                + "未自动迁移到 50 点——请人工确认 50 点后的列地址（出厂布局为 X2700/Y2800/Z2900）",
                baseAddressX, baseAddressY, baseAddressZ);
            return null;
        }

        var changeAfter = ReplaceDispenseBlock(Text, out var pointCountAtNumber, out var yAtNumber, out var zAtNumber);

        // 期望形态：点数 1 处；Y 与 Z 各 1 处（新版只写"列"）或各 2 处（老版还带派生属性"坐标列"）。
        // 对不上说明文件结构超出预期（例如手工改坏），宁可不写也不要把地址改出半截。
        if (changeAfter is null || pointCountAtNumber != 1 || yAtNumber < 1 || yAtNumber != zAtNumber)
        {
            Log?.LogWarning(
                "点胶点表的升级未执行：点数处 {Point}、Y 处 {Y}、Z 处 {Z}（请人工核对 {Path}）",
                pointCountAtNumber, yAtNumber, zAtNumber, filePath);
            return null;
        }

        try
        {
            // 原子写 + 旧文件留 .bak：改坏了现场还能回滚
            AtomicFile.WriteAllTextAsync(filePath, changeAfter).GetAwaiter().GetResult();
        }
        catch (IOException exception)
        {
            Log?.LogWarning(exception, "写回 {Path} 失败，点胶点数未迁移", filePath);
            return null;
        }
        catch (UnauthorizedAccessException exception)
        {
            Log?.LogWarning(exception, "写回 {Path} 无权限，点胶点数未迁移", filePath);
            return null;
        }

        var Remark = $"点胶示教已由 30 点升级为 {dispenseTargetPointCount} 点："
                   + $"Y 基址 D{oldBaseAddressY}→D{newBaseAddressY}、Z 基址 D{oldBaseAddressZ}→D{newBaseAddressZ}"
                   + "（原文件已备份为 .bak）";

        Log?.LogInformation("配置升级：{Summary}（{Path}）", Remark, filePath);
        return Remark;
    }

    /// <summary>
    /// 在点胶表的对象块内做三处文本替换（保持文件其余部分逐字节不变）。
    /// </summary>
    /// <returns>替换后的全文；找不到点胶块时返回 <c>null</c>。</returns>
    private static string? ReplaceDispenseBlock(
        string Text, out int pointCountAtNumber, out int yAtNumber, out int zAtNumber)
    {
        yAtNumber = 0;
        zAtNumber = 0;

        if (!TryPositioningDispenseBlock(Text, out var begin, out var stop))
        {
            pointCountAtNumber = 0;
            return null;
        }

        var block = Text[(begin + 1)..stop];

        pointCountAtNumber = ReplaceCount(block, "\"pointCount\": 30", "\"pointCount\": 50", out var blockA);
        yAtNumber = ReplaceCount(blockA, $"\"baseAddress\": {oldBaseAddressY}", $"\"baseAddress\": {newBaseAddressY}", out var blockB);
        zAtNumber = ReplaceCount(blockB, $"\"baseAddress\": {oldBaseAddressZ}", $"\"baseAddress\": {newBaseAddressZ}", out var blockC);

        return Text[..(begin + 1)] + blockC + Text[stop..];
    }

    private static int ReplaceCount(string source, string oldValue, string newValue, out string result)
    {
        var count = 0;
        var Position = 0;

        while ((Position = source.IndexOf(oldValue, Position, StringComparison.Ordinal)) >= 0)
        {
            count++;
            Position += oldValue.Length;
        }

        result = count == 0 ? source : source.Replace(oldValue, newValue, StringComparison.Ordinal);
        return count;
    }

    /// <summary>
    /// 定位 <c>"键": "点胶"</c> 所在的那个 JSON 对象块的 <c>{ … }</c> 位置。
    /// <para>不用正则/缩进假设：按大括号配对扫一遍（跳过字符串字面量），
    /// 这样现场手工改过缩进或键顺序也能定位。</para>
    /// </summary>
    private static bool TryPositioningDispenseBlock(string Text, out int begin, out int stop)
    {
        begin = -1;
        stop = -1;

        var target = Text.IndexOf("\"key\": \"点胶\"", StringComparison.Ordinal);

        if (target < 0)
        {
            return false;
        }

        var stack = new Stack<int>();

        for (var i = 0; i < Text.Length; i++)
        {
            if (i == target && stack.Count > 0 && begin < 0)
            {
                begin = stack.Peek();   // 此刻栈顶就是"包含该键"的对象
            }

            var character = Text[i];

            if (character == '"')
            {
                i = StringEndPoint(Text, i);
                continue;
            }

            if (character == '{')
            {
                stack.Push(i);
                continue;
            }

            if (character != '}' || stack.Count == 0)
            {
                continue;
            }

            var openPosition = stack.Pop();

            if (openPosition == begin)
            {
                stop = i;
                return true;
            }
        }

        return false;
    }

    /// <summary>返回从 <paramref name="startPoint"/> 的引号开始的字符串字面量的<b>结束引号</b>下标。</summary>
    private static int StringEndPoint(string Text, int startPoint)
    {
        for (var i = startPoint + 1; i < Text.Length; i++)
        {
            if (Text[i] != '"')
            {
                continue;
            }

            // 数一数前面有几个连续反斜杠：偶数说明这个引号没有被转义
            var backslash = 0;

            for (var item = i - 1; item > startPoint && Text[item] == '\\'; item--)
            {
                backslash++;
            }

            if (backslash % 2 == 0)
            {
                return i;
            }
        }

        return Text.Length - 1;
    }
}
