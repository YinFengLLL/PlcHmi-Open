namespace PlcHmi.Core.Abstractions;

/// <summary>
/// Modbus 客户端契约。实现方只负责“把字节收发到位”，
/// <b>不承担软元件地址映射与字序处理</b>——那两件事由 <see cref="IAddressParser"/> 完成。
/// </summary>
public interface IModbusClient : IAsyncDisposable
{
    /// <summary>当前是否处于已连接状态。</summary>
    bool IsConnected { get; }

    /// <summary>建立连接。</summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>断开连接。</summary>
    Task DisconnectAsync();

    // ── 读 ──────────────────────────────────────────────────────────────

    /// <summary>读线圈（功能码 01）。</summary>
    Task<bool[]> ReadCoilAsync(int addressValue, int countValue, CancellationToken cancellationToken = default);

    /// <summary>读离散输入（功能码 02）。</summary>
    Task<bool[]> ReadDiscreteInputAsync(int addressValue, int countValue, CancellationToken cancellationToken = default);

    /// <summary>读保持寄存器（功能码 03）。</summary>
    Task<ushort[]> ReadHoldingRegisterAsync(int addressValue, int countValue, CancellationToken cancellationToken = default);

    /// <summary>读输入寄存器（功能码 04）。</summary>
    Task<ushort[]> ReadInputRegisterAsync(int addressValue, int countValue, CancellationToken cancellationToken = default);

    // ── 写 ──────────────────────────────────────────────────────────────

    /// <summary>写单个线圈（功能码 05）。</summary>
    Task WriteSingleCoilAsync(int addressValue, bool Value, CancellationToken cancellationToken = default);

    /// <summary>写单个寄存器（功能码 06）。</summary>
    Task WriteSingleRegisterAsync(int addressValue, ushort Value, CancellationToken cancellationToken = default);

    /// <summary>写多个线圈（功能码 0F）。</summary>
    Task WriteMultiCoilAsync(int addressValue, IReadOnlyList<bool> ValueList, CancellationToken cancellationToken = default);

    /// <summary>写多个寄存器（功能码 10）。</summary>
    Task WriteMultiRegisterAsync(int addressValue, IReadOnlyList<ushort> ValueList, CancellationToken cancellationToken = default);
}
