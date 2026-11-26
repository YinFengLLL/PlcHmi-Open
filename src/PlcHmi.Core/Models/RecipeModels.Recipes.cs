namespace PlcHmi.Core.Models;

/// <summary>
/// 机种区段：<b>D2500–D3999（含两端）就是机种区域</b>。
///
/// <para><b>现场约定（2026-10-12 确认，2026-10-21 扩到 D3999）：</b>凡落在该区间的数据
/// （锁附点表 X/Y/浮高、锁附辅助地址值、点胶点表 指令码/X/Y/Z、
/// <b>点胶辅助地址结构体数组 D3500–D3739</b> ……）都<b>跟随机种切换</b>；
/// 区间之外的 D 区（轴速度参数 D1200 段、动作指令等）一律<b>全局记忆</b>，切机种不触碰。</para>
///
/// <para><b>为什么从 D3500 扩到 D3999：</b>点胶示教页的辅助地址改为"结构体数组"
/// （每个点一份、地址随点序号偏移，见 <c>辅助地址项.按点地址</c>），
/// 30 个点 × 8 字 = 240 个寄存器（D3500–D3739），必须整段落在机种区域内，
/// 否则"换机种"时点胶的单点工艺参数不会跟着换。</para>
///
/// <para><b>连带调整：</b>标定地址原在 D3600–D3613，正好被上面的数组压到，
/// 已整体后移到 <b>D3740–D3753</b>；「其它点」允许范围由 D3500–D3999 收为
/// <b>D3760–D3999</b>，避免与数组和标定重叠。</para>
/// </summary>
public static class MachineModelSection
{
    /// <summary>区段起始地址（D 区序号）。</summary>
    public const int StartAt = 2500;

    /// <summary>区段结束地址（含）。</summary>
    public const int end = 3999;

    /// <summary>区段长度（字数）。</summary>
    public const int length = end - StartAt + 1;

    /// <summary>相对区段起始的偏移是否在范围内。</summary>
    public static bool Contains(int Address) => Address >= StartAt && Address <= end;
}

/// <summary>机种摘要（机种列表显示用）。</summary>
public sealed class RecipeInfo
{
    /// <summary>机种号（1 基）。</summary>
    public int Number { get; set; }

    /// <summary>机种名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>该机种是否已录入数据（区段快照非空）。</summary>
    public bool HasData { get; set; }

    /// <summary>快照中非 0 寄存器的个数（粗略代表该机种的数据量）。</summary>
    public int RegisterCount { get; set; }

    /// <summary>显示名称（未命名时回退到「机种 NN」）。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"机种 {Number:00}" : Name;

    /// <summary>数据量文本。</summary>
    public string DataSummary => HasData ? $"{RegisterCount} 项" : "空";
}

/// <summary>
/// 一个机种的数据 = <b>D2500–D3999 区段的寄存器快照</b>。
///
/// <para>这是 <c>config/recipes.json</c>（上位机主存）中的一个条目。
/// 快照采用<b>稀疏存储</b>：键是相对 <see cref="MachineModelSection.StartAt"/> 的偏移，
/// 值为 16 位寄存器内容；全 0 的字不写文件，20 个机种也依然轻量。</para>
///
/// <para><b>为什么用"整段快照"而不是逐点表结构化存储：</b>现场口径是"D2500–3500 都是机种区域"，
/// 整段快照不需要为每个新增元件维护一份字段映射，配置改动不会让老机种数据错位。</para>
/// </summary>
public sealed class RecipeData
{
    /// <summary>机种号（1 基）。</summary>
    public int Number { get; set; }

    /// <summary>机种名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>区段快照：相对 D2500 的偏移 → 16 位寄存器值（稀疏，只存非 0）。</summary>
    public Dictionary<int, int> SectionSnapshot { get; set; } = new();

    /// <summary>快照是否为空（= 该机种还没保存过数据）。</summary>
    public bool IsEmpty => SectionSnapshot.Count == 0;

    /// <summary>按绝对 D 地址取值（无记录返回 0）。</summary>
    public int Take(int Address) =>
        SectionSnapshot.TryGetValue(Address - MachineModelSection.StartAt, out var Value) ? Value : 0;

    /// <summary>按绝对 D 地址写值（写 0 等于删除该条记录）。</summary>
    public void put(int Address, int Value)
    {
        var Offset = Address - MachineModelSection.StartAt;

        if (Value == 0)
        {
            SectionSnapshot.Remove(Offset);
        }
        else
        {
            SectionSnapshot[Offset] = Value;
        }
    }

    /// <summary>深拷贝（避免上位机内存与文件快照互相污染）。</summary>
    public RecipeData Clone() => new()
    {
        Number = Number,
        Name = Name,
        SectionSnapshot = new Dictionary<int, int>(SectionSnapshot),
    };
}

/// <summary>
/// 全局参数主存：<c>config/parameters.json</c>。
///
/// <para><b>为什么不放在机种里：</b>机种区段只有 D2500–D3999；轴速度这类参数在 D1200 段，
/// 属"全局记忆"——切机种不得改变它们。</para>
/// </summary>
public sealed class ParameterFile
{
    /// <summary>参数键 → 值（全局唯一一份）。</summary>
    public Dictionary<string, double> CurrentParameter { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary><c>config/recipes.json</c> 的顶层结构——<b>上位机侧的主存</b>。</summary>
public sealed class RecipeFile
{
    /// <summary>当前机种号（1 基）。</summary>
    public int CurrentRecipeNumber { get; set; } = 1;

    /// <summary>全部机种数据。只保存有内容的机种。</summary>
    public List<RecipeData> RecipeCollection { get; set; } = new();
}
