namespace 上位机.核心.抽象;

/// <summary>
/// Modbus 客户端契约。实现方只负责“把字节收发到位”，
/// <b>不承担软元件地址映射与字序处理</b>——那两件事由 <see cref="地址解析器接口"/> 完成。
/// </summary>
public interface Modbus客户端接口 : IAsyncDisposable
{
    /// <summary>当前是否处于已连接状态。</summary>
    bool 是否已连接 { get; }

    /// <summary>建立连接。</summary>
    Task 异步连接(CancellationToken 取消令牌 = default);

    /// <summary>断开连接。</summary>
    Task 异步断开();

    // ── 读 ──────────────────────────────────────────────────────────────

    /// <summary>读线圈（功能码 01）。</summary>
    Task<bool[]> 异步读取线圈(int 地址值, int 数量值, CancellationToken 取消令牌 = default);

    /// <summary>读离散输入（功能码 02）。</summary>
    Task<bool[]> 异步读取离散输入(int 地址值, int 数量值, CancellationToken 取消令牌 = default);

    /// <summary>读保持寄存器（功能码 03）。</summary>
    Task<ushort[]> 异步读取保持寄存器(int 地址值, int 数量值, CancellationToken 取消令牌 = default);

    /// <summary>读输入寄存器（功能码 04）。</summary>
    Task<ushort[]> 异步读取输入寄存器(int 地址值, int 数量值, CancellationToken 取消令牌 = default);

    // ── 写 ──────────────────────────────────────────────────────────────

    /// <summary>写单个线圈（功能码 05）。</summary>
    Task 异步写入单个线圈(int 地址值, bool 值, CancellationToken 取消令牌 = default);

    /// <summary>写单个寄存器（功能码 06）。</summary>
    Task 异步写入单个寄存器(int 地址值, ushort 值, CancellationToken 取消令牌 = default);

    /// <summary>写多个线圈（功能码 0F）。</summary>
    Task 异步写入多个线圈(int 地址值, IReadOnlyList<bool> 值列表, CancellationToken 取消令牌 = default);

    /// <summary>写多个寄存器（功能码 10）。</summary>
    Task 异步写入多个寄存器(int 地址值, IReadOnlyList<ushort> 值列表, CancellationToken 取消令牌 = default);
}
