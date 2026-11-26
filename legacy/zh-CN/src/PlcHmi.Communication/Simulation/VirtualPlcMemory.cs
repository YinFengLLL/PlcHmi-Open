namespace 上位机.通信.仿真;

/// <summary>
/// 虚拟 PLC 内存映像。模拟一个 Modbus 从站的四个数据区。
/// </summary>
public sealed class 虚拟PLC内存
{
    private readonly object _同步 = new();

    private readonly bool[] _线圈 = new bool[65536];
    private readonly bool[] _离散输入 = new bool[65536];
    private readonly ushort[] _保持寄存器 = new ushort[65536];
    private readonly ushort[] _输入寄存器 = new ushort[65536];

    // ── 读 ──────────────────────────────────────────────────────────────

    public bool[] 读取线圈(int 地址值, int 数量值)
    {
        lock (_同步)
        {
            return 切片(_线圈, 地址值, 数量值);
        }
    }

    public bool[] 读取离散输入(int 地址值, int 数量值)
    {
        lock (_同步)
        {
            return 切片(_离散输入, 地址值, 数量值);
        }
    }

    public ushort[] 读取保持寄存器(int 地址值, int 数量值)
    {
        lock (_同步)
        {
            return 切片(_保持寄存器, 地址值, 数量值);
        }
    }

    public ushort[] 读取输入寄存器(int 地址值, int 数量值)
    {
        lock (_同步)
        {
            return 切片(_输入寄存器, 地址值, 数量值);
        }
    }

    // ── 写 ──────────────────────────────────────────────────────────────

    public void 写入线圈(int 地址值, bool 值)
    {
        lock (_同步)
        {
            _线圈[地址值] = 值;
        }
    }

    public void 写入线圈项(int 地址值, IReadOnlyList<bool> 值列表)
    {
        lock (_同步)
        {
            for (var 条目 = 0; 条目 < 值列表.Count; 条目++)
            {
                _线圈[地址值 + 条目] = 值列表[条目];
            }
        }
    }

    public void 写入保持寄存器(int 地址值, ushort 值)
    {
        lock (_同步)
        {
            _保持寄存器[地址值] = 值;
        }
    }

    public void 写入保持寄存器项(int 地址值, IReadOnlyList<ushort> 值列表)
    {
        lock (_同步)
        {
            for (var 条目 = 0; 条目 < 值列表.Count; 条目++)
            {
                _保持寄存器[地址值 + 条目] = 值列表[条目];
            }
        }
    }

    // ── 模拟器内部使用（不加锁直接访问，调用方自行保证）────────────────

    /// <summary>直接设置线圈值（模拟器内部使用）。</summary>
    public void 不安全设置线圈(int 地址值, bool 值) => _线圈[地址值] = 值;

    /// <summary>直接设置离散输入值（模拟器内部使用）。</summary>
    public void 不安全设置离散输入(int 地址值, bool 值) => _离散输入[地址值] = 值;

    /// <summary>直接读取线圈值（模拟器内部使用）。</summary>
    public bool 不安全获取线圈(int 地址值) => _线圈[地址值];

    /// <summary>直接读取离散输入值（模拟器内部使用）。X 是物理输入，读的是这一区。</summary>
    public bool 不安全获取离散输入(int 地址值) => _离散输入[地址值];

    /// <summary>直接设置保持寄存器值（模拟器内部使用）。</summary>
    public void 不安全设置保持寄存器(int 地址值, ushort 值) => _保持寄存器[地址值] = 值;

    /// <summary>直接读取保持寄存器值（模拟器内部使用）。</summary>
    public ushort 不安全获取保持寄存器(int 地址值) => _保持寄存器[地址值];

    /// <summary>批量设置保持寄存器（模拟器内部使用）。</summary>
    public void 不安全设置保持寄存器项(int 启动地址, ReadOnlySpan<ushort> 值列表)
    {
        for (var 条目 = 0; 条目 < 值列表.Length; 条目++)
        {
            _保持寄存器[启动地址 + 条目] = 值列表[条目];
        }
    }

    /// <summary>模拟器内部写操作加锁范围。</summary>
    public void 运行独占(Action 动作)
    {
        lock (_同步)
        {
            动作();
        }
    }

    private static T[] 切片<T>(T[] 来源, int 地址值, int 数量值)
    {
        if (地址值 < 0 || 地址值 + 数量值 > 来源.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(地址值),
                $"模拟器地址越界：address={地址值}, count={数量值}, 容量={来源.Length}");
        }

        var 结果值 = new T[数量值];
        Array.Copy(来源, 地址值, 结果值, 0, 数量值);
        return 结果值;
    }
}
