namespace 上位机.核心.模型;

/// <summary>
/// 点胶示教的一条指令码（PLC 指令表里的一个动作）。
///
/// <para><b>码值即 PLC 里 D3400 段写下的数：</b>现场口径（2026-10-21）——指令码 0…9 对应
/// PLC 指令表的行号，点表第 n 点（1 基）的指令码落在 <c>D3400 + (n - 1)</c>。
/// 例：选中第 2 点、下拉选「直线」（02）→ <c>D3401 = 2</c>。</para>
/// </summary>
public sealed class 指令码项
{
    /// <summary>写入 PLC 的码值（0…9，与 PLC 指令表行号一致）。</summary>
    public int 码 { get; init; }

    /// <summary>界面文字（结束 / 起点 / 直线 …）。</summary>
    public string 名称 { get; init; } = string.Empty;

    /// <summary>下拉框显示文本，如 <c>02 - 直线</c>。</summary>
    public string 显示 => $"{码:00} - {名称}";

    /// <summary>悬停提示（码值 + 对应 PLC 的 Step 号，便于现场与指令表对照）。</summary>
    public string 提示 => $"指令码 {码:00}（{名称}）→ 写入 D 区指令码字";
}

/// <summary>
/// 点胶示教指令码表（PLC 指令表：0 结束 / 1 起点 / 2 直线 … 9 对针）。
///
/// <para><b>为什么写死在代码里而不进 JSON：</b>这份清单由 <b>PLC 程序</b>决定（指令表的行号），
/// 不是现场可调的工艺参数；让它进配置只会出现"界面有 09 对针、PLC 却没有这一行"的错配。
/// 地址（指令码列基址 D3400）仍由点表配置提供，管理员可改。</para>
/// </summary>
public static class 指令码定义
{
    /// <summary>全部指令码（顺序即下拉顺序）。</summary>
    public static IReadOnlyList<指令码项> 全部 { get; } =
    [
        new() { 码 = 0, 名称 = "结束" },
        new() { 码 = 1, 名称 = "起点" },
        new() { 码 = 2, 名称 = "直线" },
        new() { 码 = 3, 名称 = "直线终点" },
        new() { 码 = 4, 名称 = "圆弧中点" },
        new() { 码 = 5, 名称 = "圆弧终点" },
        new() { 码 = 6, 名称 = "单点" },
        new() { 码 = 7, 名称 = "空移" },
        new() { 码 = 8, 名称 = "刮胶" },
        new() { 码 = 9, 名称 = "对针" },
    ];

    /// <summary>按码值查找（不在 0…9 内返回 <c>null</c>）。</summary>
    public static 指令码项? 查找(int 码) => 全部.FirstOrDefault(项 => 项.码 == 码);

    /// <summary>
    /// 码值 → 点表里显示的指令文字。
    /// <para>未知码值原样显示数字：老机种里可能存着按旧口径手填的值（例如 100），
    /// 此时把它显示成"未知"会让人以为数据坏了，显示原值才能对照 PLC。</para>
    /// </summary>
    public static string 显示文本(double 码值)
    {
        var 码 = (int)Math.Round(码值);
        return 查找(码)?.名称 ?? 码.ToString();
    }
}
