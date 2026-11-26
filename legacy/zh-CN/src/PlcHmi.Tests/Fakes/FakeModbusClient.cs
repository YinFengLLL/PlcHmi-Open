using 上位机.核心.抽象;

namespace 上位机.上位机测试.替身;

/// <summary>
/// 可控的 <see cref="Modbus客户端接口"/> 测试替身，用于验证连接管理器的重连状态机。
///
/// <para><b>为什么不用真实模拟器：</b>验证"指数退避"必须能精确控制失败次数与观测尝试时刻。
/// 真实 <c>VirtualPlcDevice</c> 是完整从站，无法按次数强制失败，也拿不到连接尝试的时间戳。
/// 本替身只做两件事：<b>按设定失败 N 次</b>，并<b>记录每次尝试的时间</b>。</para>
/// </summary>
public sealed class 替身Modbus客户端 : Modbus客户端接口
{
    private readonly object _同步 = new();
    private readonly List<DateTime> _连接尝试次数 = new();
    private readonly List<(int 地址, bool 值)> _线圈写入 = new();
    private readonly List<(int 地址, ushort 值)> _寄存器写入 = new();
    private readonly List<(int 地址, IReadOnlyList<ushort> 值)> _寄存器块写入 = new();

    private int _剩余失败次数;
    private volatile bool _是否已连接;

    /// <inheritdoc />
    public bool 是否已连接 => _是否已连接;

    /// <summary>连接尝试的时间戳（UTC），按发生顺序排列。</summary>
    public IReadOnlyList<DateTime> 连接尝试次数
    {
        get
        {
            lock (_同步)
            {
                return _连接尝试次数.ToList();
            }
        }
    }

    /// <summary>连接尝试总次数。</summary>
    public int 连接尝试数量
    {
        get
        {
            lock (_同步)
            {
                return _连接尝试次数.Count;
            }
        }
    }

    /// <summary>写单个线圈的记录（Modbus 地址、值），按发生顺序。</summary>
    public IReadOnlyList<(int 地址, bool 值)> 线圈写入
    {
        get
        {
            lock (_同步)
            {
                return _线圈写入.ToList();
            }
        }
    }

    /// <summary>写单个寄存器的记录（Modbus 地址、值），按发生顺序。</summary>
    public IReadOnlyList<(int 地址, ushort 值)> 寄存器写入
    {
        get
        {
            lock (_同步)
            {
                return _寄存器写入.ToList();
            }
        }
    }

    /// <summary>
    /// 写多个寄存器的记录（起始地址、寄存器列表），按发生顺序。
    /// <para>供 32 位（DINT / Float32）下发验证字数与字序——红线 R1 要求字序可被独立复算。</para>
    /// </summary>
    public IReadOnlyList<(int 地址, IReadOnlyList<ushort> 值)> 寄存器块写入
    {
        get
        {
            lock (_同步)
            {
                return _寄存器块写入.ToList();
            }
        }
    }

    /// <summary>让接下来 <paramref name="数量值"/> 次连接尝试失败；之后的尝试会成功。</summary>
    public void 失败下一个连接(int 数量值)
    {
        lock (_同步)
        {
            _剩余失败次数 = 数量值;
        }
    }

    /// <inheritdoc />
    public Task 异步连接(CancellationToken 取消令牌 = default)
    {
        取消令牌.ThrowIfCancellationRequested();

        lock (_同步)
        {
            _连接尝试次数.Add(DateTime.UtcNow);

            if (_剩余失败次数 > 0)
            {
                _剩余失败次数--;
                _是否已连接 = false;
                return Task.FromException(new IOException("模拟：连接 PLC 失败"));
            }

            _是否已连接 = true;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task 异步断开()
    {
        _是否已连接 = false;
        return Task.CompletedTask;
    }

    // ── 读写：返回空数据即可，重连测试不依赖取值 ──────────────────────────

    /// <summary>预设的保持寄存器值（读取时优先查表，查不到按 0）。</summary>
    private readonly Dictionary<int, ushort> _保持寄存器表 = new();

    /// <summary>批量读取回归用：记录 <c>异步读取保持寄存器</c> 的调用次数。</summary>
    private int _保持寄存器读取次数;

    /// <summary>保持寄存器读取请求次数（批量合并读取的性能断言用）。</summary>
    public int 保持寄存器读取次数 => Volatile.Read(ref _保持寄存器读取次数);

    /// <summary>预设的线圈状态（读取时优先查表，查不到按 false）。</summary>
    private readonly Dictionary<int, bool> _线圈表 = new();

    /// <summary>
    /// 预设某个保持寄存器的值，供"回读"类用例使用（如参数页延时回读）。
    /// <para>写入（异步写入单个寄存器 / 多个寄存器）也会同步进表，因此"写下再读回"天然一致。</para>
    /// </summary>
    public void 预设保持寄存器(int 地址值, ushort 值)
    {
        lock (_同步)
        {
            _保持寄存器表[地址值] = 值;
        }
    }

    /// <summary>预设某个线圈的状态，供回读类用例使用。</summary>
    public void 预设线圈(int 地址值, bool 值)
    {
        lock (_同步)
        {
            _线圈表[地址值] = 值;
        }
    }

    /// <inheritdoc />
    public Task<bool[]> 异步读取线圈(int 地址值, int 数量值, CancellationToken 取消令牌 = default)
    {
        lock (_同步)
        {
            var 值列表 = new bool[数量值];

            for (var 序号 = 0; 序号 < 数量值; 序号++)
            {
                值列表[序号] = _线圈表.TryGetValue(地址值 + 序号, out var 值) && 值;
            }

            return Task.FromResult(值列表);
        }
    }

    /// <inheritdoc />
    public Task<bool[]> 异步读取离散输入(int 地址值, int 数量值, CancellationToken 取消令牌 = default) =>
        Task.FromResult(new bool[数量值]);

    /// <inheritdoc />
    public Task<ushort[]> 异步读取保持寄存器(int 地址值, int 数量值, CancellationToken 取消令牌 = default)
    {
        Interlocked.Increment(ref _保持寄存器读取次数);

        lock (_同步)
        {
            var 值列表 = new ushort[数量值];

            for (var 序号 = 0; 序号 < 数量值; 序号++)
            {
                值列表[序号] = _保持寄存器表.TryGetValue(地址值 + 序号, out var 值) ? 值 : (ushort)0;
            }

            return Task.FromResult(值列表);
        }
    }

    /// <inheritdoc />
    public Task<ushort[]> 异步读取输入寄存器(int 地址值, int 数量值, CancellationToken 取消令牌 = default) =>
        Task.FromResult(new ushort[数量值]);

    /// <inheritdoc />
    public Task 异步写入单个线圈(int 地址值, bool 值, CancellationToken 取消令牌 = default)
    {
        lock (_同步)
        {
            _线圈写入.Add((地址值, 值));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task 异步写入单个寄存器(int 地址值, ushort 值, CancellationToken 取消令牌 = default)
    {
        lock (_同步)
        {
            _寄存器写入.Add((地址值, 值));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task 异步写入多个线圈(
        int 地址值, IReadOnlyList<bool> 值列表, CancellationToken 取消令牌 = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task 异步写入多个寄存器(
        int 地址值, IReadOnlyList<ushort> 值列表, CancellationToken 取消令牌 = default)
    {
        lock (_同步)
        {
            _寄存器块写入.Add((地址值, 值列表.ToList()));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _是否已连接 = false;
        return ValueTask.CompletedTask;
    }
}
