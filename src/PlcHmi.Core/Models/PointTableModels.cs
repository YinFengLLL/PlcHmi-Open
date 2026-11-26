using PlcHmi.Core.Enums;

namespace PlcHmi.Core.Models;

/// <summary>
/// 点表的一列：一个轴坐标、浮高值或指令码。
///
/// <para><b>寻址：</b>第 i 点该列的寄存器地址 = <see cref="BaseAddress"/> + i × <see cref="WordPerPointBit"/>。
/// 列与列之间各自连续，互不影响。</para>
/// </summary>
public sealed class PointTableColumnDefinition
{
    /// <summary>列键，如 <c>X</c> / <c>Y</c> / <c>浮高</c> / <c>指令码</c>。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>界面显示名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>D 区基址（第一个点的寄存器序号）。</summary>
    public int BaseAddress { get; set; }

    /// <summary>数据类型，决定占用字数与编解码方式（指令码默认单字）。</summary>
    public TagDataType DataType { get; set; } = TagDataType.Float32;

    /// <summary>工程单位（可空）。</summary>
    public string? Unit { get; set; }

    /// <summary>
    /// 当前位置标签名（<c>tags.json</c> 中的点位名），用于「坐标示教」采集当前值。
    /// <para>为空表示该列不由轴位置示教（如指令码、浮高值手动填写）。</para>
    /// </summary>
    public string? PositionTag { get; set; }

    /// <summary>本列每个点占用的寄存器数量。</summary>
    public int WordPerPointBit => DataType switch
    {
        TagDataType.Float32 or TagDataType.Int32 or TagDataType.UInt32 => 2,
        TagDataType.Float64 => 4,
        _ => 1,
    };

    /// <summary>显示名称（为空时回退到键）。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Key : Name;

    /// <summary>浅拷贝（供界面编辑）。</summary>
    public PointTableColumnDefinition Clone() => (PointTableColumnDefinition)MemberwiseClone();
}

/// <summary>一张具名点表（如锁附示教、点胶示教）。</summary>
public sealed class PointTableDefinition
{
    /// <summary>指令码列的固定键名。</summary>
    public const string instructionCodeColumnKey = "指令码";

    /// <summary>表键，如 <c>锁附</c> / <c>点胶</c>。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>界面显示名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>最大点数（默认 30）。</summary>
    public int PointCount { get; set; } = 30;

    /// <summary>坐标下限（下发前的范围校验）。</summary>
    public double MinValue { get; set; } = -999.99;

    /// <summary>坐标上限。</summary>
    public double MaxValue { get; set; } = 9999.99;

    /// <summary>
    /// 手动控制区显示的轴分组（对应 <see cref="JogButtonItem.Group"/>）。
    /// <para>留空表示显示全部轴；本页只显示与该分组匹配的轴，
    /// 这样锁附页看到 X/Y/Z/ZZ、点胶页只看到点胶 X/Y/Z。</para>
    /// </summary>
    public string JogGroup { get; set; } = string.Empty;

    /// <summary>
    /// 「保存坐标点」要写入的点表列键。
    /// <para>留空表示"凡带位置标签的列都写"；显式列出则只写这些列。
    /// 例如锁附页只要 X/Y（浮高由人工填写，不能被 Z 轴当前位置覆盖）、
    /// 点胶页要 X/Y/Z（指令码本来就无位置标签，天然不写）。</para>
    /// </summary>
    public List<string> SaveCoordinateColumn { get; set; } = new();

    /// <summary>
    /// 「当前浮高」实时显示地址（D 区序号，REAL 占 2 字）；<c>0</c> = 本页不显示。
    ///
    /// <para><b>2026-10-24 现场要求（锁附页）：</b>手动区在 X/Y/Z/ZZ 当前位之后显示一个当前浮高，
    /// 「保存坐标点」时也把它写进选中点的「浮高」列（浮高由测高机构给出，不是 Z 轴位置）。
    /// 地址由管理员在「编辑地址」里改。</para>
    /// </summary>
    public int CurrentFloatHeightAddress { get; set; }

    /// <summary>列定义。</summary>
    public List<PointTableColumnDefinition> Column { get; set; } = new();

    /// <summary>按列键查找列定义。</summary>
    public PointTableColumnDefinition? FindColumn(string ColumnKey) =>
        Column.FirstOrDefault(columnItem => string.Equals(columnItem.Key, ColumnKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>该列是否参与「保存坐标点」（见 <see cref="SaveCoordinateColumn"/>）。</summary>
    public bool IsAndSaveCoordinate(string ColumnKey) =>
        SaveCoordinateColumn.Count == 0
        || SaveCoordinateColumn.Any(keyValue => string.Equals(keyValue, ColumnKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>坐标类列（排除指令码）：判空、示教与范围校验只看这些列。</summary>
    public IReadOnlyList<PointTableColumnDefinition> CoordinateColumn =>
        Column.Where(columnItem => !string.Equals(columnItem.Key, instructionCodeColumnKey, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>是否含指令码列。</summary>
    public bool HasInstructionCode => FindColumn(instructionCodeColumnKey) is not null;

    /// <summary>显示名称（为空时回退到键）。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Key : Name;
}

/// <summary><c>recipe-config.json</c> 的点表集合。</summary>
public sealed class PointTableConfig
{
    /// <summary>全部点表定义。</summary>
    public List<PointTableDefinition> PointTable { get; set; } = new();

    /// <summary>按表键查找。</summary>
    public PointTableDefinition? Find(string Key) =>
        PointTable.FirstOrDefault(TableEntry => string.Equals(TableEntry.Key, Key, StringComparison.OrdinalIgnoreCase));
}

/// <summary>点表里的一行（一个点）。</summary>
public sealed class PointValueRow
{
    /// <summary>点序号（0 基）。</summary>
    public int Sequence { get; set; }

    /// <summary>列键 → 值。</summary>
    public Dictionary<string, double> Value { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>按列键取值（缺省 0）。</summary>
    public double Take(string ColumnKey) => Value.TryGetValue(ColumnKey, out var valueItem) ? valueItem : 0;

    /// <summary>按列键写值。</summary>
    public void setup(string ColumnKey, double valueItem) => Value[ColumnKey] = valueItem;

    /// <summary>是否为空点（全部坐标列为 0；指令码不参与判空）。</summary>
    public bool IsEmpty(PointTableDefinition Table) => Table.CoordinateColumn.All(columnItem => Take(columnItem.Key) == 0);

    /// <summary>深拷贝。</summary>
    public PointValueRow Clone() => new()
    {
        Sequence = Sequence,
        Value = new Dictionary<string, double>(Value, StringComparer.OrdinalIgnoreCase),
    };
}

/// <summary>一张点表的落盘数据（稀疏：只存非空点）。</summary>
public sealed class PointTableData
{
    /// <summary>表键。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>已示教的点。</summary>
    public List<PointValueRow> Row { get; set; } = new();
}

/// <summary><c>config/point-tables.json</c> 的顶层结构。</summary>
public sealed class PointTableFile
{
    /// <summary>全部点表数据。</summary>
    public List<PointTableData> PointTableCollection { get; set; } = new();

    /// <summary>按表键查找。</summary>
    public PointTableData? Find(string Key) =>
        PointTableCollection.FirstOrDefault(TableEntry => string.Equals(TableEntry.Key, Key, StringComparison.OrdinalIgnoreCase));
}

/// <summary>点表地址允许范围（供管理员编辑时的粗粒度约束）。</summary>
public sealed class PointTableAddressRange
{
    /// <summary>下限（含）。</summary>
    public int Min { get; set; } = 2500;

    /// <summary>上限（含）。</summary>
    public int Max { get; set; } = 3999;

    /// <summary>是否包含。</summary>
    public bool Contains(int addressValue) => addressValue >= Min && addressValue <= Max;

    /// <summary>范围文本。</summary>
    public string Text => $"D{Min} – D{Max}";
}
