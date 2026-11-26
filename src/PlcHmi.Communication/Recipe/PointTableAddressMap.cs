using PlcHmi.Core.Models;

namespace PlcHmi.Communication.Recipe;

/// <summary>
/// 通用点表的地址计算。
///
/// <para><b>布局是「按列分区」：</b>每个列（轴 / 浮高 / 指令码）各占一段连续寄存器，
/// 第 i 点该列的地址 = <c>列基址 + i × 字每点位</c>。</para>
///
/// <para><b>为什么必须被测试：</b>偏移算错一个数，现场表现是"轴跑到错误位置"或
/// "指令码错位"，排查成本极高。它是整个方案里最该被钉死的部分。</para>
/// </summary>
public sealed class PointTableAddressMap
{
    private readonly PointTableDefinition _table;

    /// <summary>用点表定义构造。</summary>
    public PointTableAddressMap(PointTableDefinition TableDefinition)
    {
        _table = TableDefinition ?? throw new ArgumentNullException(nameof(TableDefinition));

        if (_table.PointCount <= 0)
        {
            throw new ArgumentException("点数必须大于 0。", nameof(TableDefinition));
        }

        if (_table.Column.Count == 0)
        {
            throw new ArgumentException("点表至少需要一列。", nameof(TableDefinition));
        }
    }

    /// <summary>点表定义。</summary>
    public PointTableDefinition Definition => _table;

    /// <summary>点总数。</summary>
    public int PointCount => _table.PointCount;

    /// <summary>列基数。</summary>
    public int ColumnCount => _table.Column.Count;

    /// <summary>列基址。</summary>
    public int ColumnBaseAddress(string ColumnKey) => WantRequireColumn(ColumnKey).BaseAddress;

    /// <summary>列每个点占用的字数。</summary>
    public int WordPerPointBit(string ColumnKey) => WantRequireColumn(ColumnKey).WordPerPointBit;

    /// <summary>某列整块连续字数（一次请求的长度）。</summary>
    public int ColumnBlockWord(string ColumnKey) => PointCount * WordPerPointBit(ColumnKey);

    /// <summary>序号 + 列键 → 该点该列的起始寄存器地址（D 区序号）。</summary>
    public int Address(int Sequence, string ColumnKey)
    {
        ValidateSequence(Sequence);
        return WantRequireColumn(ColumnKey).BaseAddress + Sequence * WantRequireColumn(ColumnKey).WordPerPointBit;
    }

    /// <summary>某列最后一个点的起始地址。</summary>
    public int LastPointAddress(string ColumnKey) => Address(PointCount - 1, ColumnKey);

    /// <summary>序号是否合法。</summary>
    public bool IndexValid(int Sequence) => Sequence >= 0 && Sequence < PointCount;

    private PointTableColumnDefinition WantRequireColumn(string ColumnKey) =>
        _table.FindColumn(ColumnKey)
        ?? throw new ArgumentOutOfRangeException(nameof(ColumnKey), ColumnKey, $"点表「{_table.DisplayName}」中没有列「{ColumnKey}」。");

    private void ValidateSequence(int Sequence)
    {
        if (!IndexValid(Sequence))
        {
            throw new ArgumentOutOfRangeException(
                nameof(Sequence), Sequence, $"点索引必须在 0 – {PointCount - 1} 之间。");
        }
    }
}
