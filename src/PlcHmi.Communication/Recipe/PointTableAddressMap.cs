using 上位机.核心.模型;

namespace 上位机.通信.配方;

/// <summary>
/// 通用点表的地址计算。
///
/// <para><b>布局是「按列分区」：</b>每个列（轴 / 浮高 / 指令码）各占一段连续寄存器，
/// 第 i 点该列的地址 = <c>列基址 + i × 字每点位</c>。</para>
///
/// <para><b>为什么必须被测试：</b>偏移算错一个数，现场表现是"轴跑到错误位置"或
/// "指令码错位"，排查成本极高。它是整个方案里最该被钉死的部分。</para>
/// </summary>
public sealed class 点表地址映射
{
    private readonly 点表定义 _表;

    /// <summary>用点表定义构造。</summary>
    public 点表地址映射(点表定义 表定义)
    {
        _表 = 表定义 ?? throw new ArgumentNullException(nameof(表定义));

        if (_表.点数 <= 0)
        {
            throw new ArgumentException("点数必须大于 0。", nameof(表定义));
        }

        if (_表.列.Count == 0)
        {
            throw new ArgumentException("点表至少需要一列。", nameof(表定义));
        }
    }

    /// <summary>点表定义。</summary>
    public 点表定义 定义 => _表;

    /// <summary>点总数。</summary>
    public int 点数 => _表.点数;

    /// <summary>列基数。</summary>
    public int 列数量 => _表.列.Count;

    /// <summary>列基址。</summary>
    public int 列基址(string 列键) => 要求列(列键).基址;

    /// <summary>列每个点占用的字数。</summary>
    public int 字每点位(string 列键) => 要求列(列键).字每点位;

    /// <summary>某列整块连续字数（一次请求的长度）。</summary>
    public int 列块字(string 列键) => 点数 * 字每点位(列键);

    /// <summary>序号 + 列键 → 该点该列的起始寄存器地址（D 区序号）。</summary>
    public int 地址(int 序号, string 列键)
    {
        校验序号(序号);
        return 要求列(列键).基址 + 序号 * 要求列(列键).字每点位;
    }

    /// <summary>某列最后一个点的起始地址。</summary>
    public int 末点地址(string 列键) => 地址(点数 - 1, 列键);

    /// <summary>序号是否合法。</summary>
    public bool 序号合法(int 序号) => 序号 >= 0 && 序号 < 点数;

    private 点表列定义 要求列(string 列键) =>
        _表.查找列(列键)
        ?? throw new ArgumentOutOfRangeException(nameof(列键), 列键, $"点表「{_表.显示名称}」中没有列「{列键}」。");

    private void 校验序号(int 序号)
    {
        if (!序号合法(序号))
        {
            throw new ArgumentOutOfRangeException(
                nameof(序号), 序号, $"点索引必须在 0 – {点数 - 1} 之间。");
        }
    }
}
