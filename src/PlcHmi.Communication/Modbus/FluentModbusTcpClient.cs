using System.Buffers.Binary;
using System.Net;
using FluentModbus;
using Microsoft.Extensions.Logging;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Models;

namespace PlcHmi.Communication.Modbus;

/// <summary>
/// 基于 FluentModbus 的 Modbus TCP 客户端实现。
///
/// <para><b>职责边界：</b>只负责“把字节收发到位”。软元件地址映射由
/// <c>InovanceAddressResolver</c> 完成，数据类型与字序由 <c>RegisterCodec</c> 完成。
/// 这样更换通信库（例如换成 NModbus）不影响地址与解析逻辑。</para>
///
/// <para><b>并发约束：</b>Modbus 是请求-应答式协议，同一连接不能并发请求，
/// 因此所有收发都经过 <see cref="_writeGate"/> 串行化。
/// <b>汇川 H5U 默认最多 8 个客户端连接</b>，故整个上位机只维持一个连接实例。</para>
/// </summary>
public sealed class FluentModbusTCPClient : IModbusClient
{
    /// <summary>标准 Modbus 单次读寄存器上限。</summary>
    private const int maxReadRegisterNumber = 125;

    /// <summary>标准 Modbus 单次写寄存器上限。</summary>
    private const int maxWriteRegisterNumber = 123;

    /// <summary>标准 Modbus 单次读线圈上限。</summary>
    private const int maxReadCoilNumber = 2000;

    /// <summary>
    /// 标准 Modbus FC0F 单次<b>写</b>线圈上限（0x07B0 = 1968）。
    /// <para>写上限低于读上限（2000）：报文长度受 PDU 256 字节约束，
    /// 1968 位 = 246 字节。放行到读上限会让 PLC 返回异常码 03。</para>
    /// </summary>
    private const int maxWriteCoilNumber = 1968;

    private readonly PlcConnectionOptions _options;
    private readonly ILogger<FluentModbusTCPClient> _log;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private ModbusTcpClient? _client;
    private bool _disposed;

    public FluentModbusTCPClient(PlcConnectionOptions Options, ILogger<FluentModbusTCPClient> Log)
    {
        _options = Options;
        _log = Log;
    }

    /// <inheritdoc />
    public bool IsConnected => _client?.IsConnected == true;

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client?.IsConnected == true)
            {
                return;
            }

            ReleaseClient();

            // 先记账再连接：Connect 抛异常时由 catch 里的 释放客户端()
            // 统一释放这个未连上的实例。此前失败路径释放的是 _客户端 字段
            // （此时为旧实例或 null），新实例永不 Dispose——重连按指数退避
            // 无限重试，这里恰是最高频执行的失败路径，每失败一次泄漏一个 Socket。
            _client = new ModbusTcpClient
            {
                ConnectTimeout = _options.ConnectTimeoutMilliseconds,
                ReadTimeout = _options.ReadTimeoutMilliseconds,
                WriteTimeout = _options.ReadTimeoutMilliseconds,
            };

            var EndPointItem = new IPEndPoint(ParseHost(_options.Host), _options.Port);

            // FluentModbus 的 Connect 是同步阻塞调用，放到线程池执行避免卡住 UI 线程
            await Task.Run(() => _client.Connect(EndPointItem, ModbusEndianness.BigEndian), cancellationToken)
                .ConfigureAwait(false);

            _log.LogInformation("已连接 PLC {Host}:{Port}（从站号 {UnitId}）",
                _options.Host, _options.Port, _options.UnitId);
        }
        catch (Exception exception)
        {
            ReleaseClient();
            _log.LogWarning(exception, "连接 PLC {Host}:{Port} 失败", _options.Host, _options.Port);
            throw;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 用 <c>WaitAsync</c> 而不是 <c>Wait()</c>：
    /// 同步等待会占住调用线程，若此时 UI 线程正同步等待后台线程（反之亦然）就会死锁。
    /// </remarks>
    public async Task DisconnectAsync()
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);

        try
        {
            if (_client is not null)
            {
                try
                {
                    _client.Disconnect();
                }
                catch (Exception exception)
                {
                    _log.LogDebug(exception, "断开连接时出现异常，已忽略");
                }
            }

            ReleaseClient();
            _log.LogInformation("已断开 PLC 连接");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // ── 读 ──────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<bool[]> ReadCoilAsync(int addressValue, int countValue, CancellationToken cancellationToken = default)
    {
        ValidateRange(addressValue, countValue, maxReadCoilNumber, "读线圈");
        return ExecuteBitAsync(
            moduleClient => moduleClient.ReadCoilsAsync(_options.UnitId, addressValue, countValue, cancellationToken),
            countValue,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool[]> ReadDiscreteInputAsync(int addressValue, int countValue, CancellationToken cancellationToken = default)
    {
        ValidateRange(addressValue, countValue, maxReadCoilNumber, "读离散输入");
        return ExecuteBitAsync(
            moduleClient => moduleClient.ReadDiscreteInputsAsync(_options.UnitId, addressValue, countValue, cancellationToken),
            countValue,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<ushort[]> ReadHoldingRegisterAsync(int addressValue, int countValue, CancellationToken cancellationToken = default)
    {
        ValidateRange(addressValue, countValue, maxReadRegisterNumber, "读保持寄存器");
        return ExecuteRegisterAsync(
            moduleClient => moduleClient.ReadHoldingRegistersAsync(_options.UnitId, (ushort)addressValue, (ushort)countValue, cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<ushort[]> ReadInputRegisterAsync(int addressValue, int countValue, CancellationToken cancellationToken = default)
    {
        ValidateRange(addressValue, countValue, maxReadRegisterNumber, "读输入寄存器");
        return ExecuteRegisterAsync(
            moduleClient => moduleClient.ReadInputRegistersAsync(_options.UnitId, (ushort)addressValue, (ushort)countValue, cancellationToken),
            cancellationToken);
    }

    // ── 写 ──────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task WriteSingleCoilAsync(int addressValue, bool Value, CancellationToken cancellationToken = default)
    {
        return ExecuteWriteAsync(
            moduleClient => moduleClient.WriteSingleCoilAsync(_options.UnitId, addressValue, Value, cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task WriteSingleRegisterAsync(int addressValue, ushort Value, CancellationToken cancellationToken = default)
    {
        return ExecuteWriteAsync(
            moduleClient => moduleClient.WriteSingleRegisterAsync(_options.UnitId, addressValue, Value, cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task WriteMultiCoilAsync(int addressValue, IReadOnlyList<bool> ValueList, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ValueList);
        ValidateRange(addressValue, ValueList.Count, maxWriteCoilNumber, "写多线圈");

        var bitPayload = ValueList as bool[] ?? ValueList.ToArray();
        return ExecuteWriteAsync(
            moduleClient => moduleClient.WriteMultipleCoilsAsync(_options.UnitId, addressValue, bitPayload, cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task WriteMultiRegisterAsync(int addressValue, IReadOnlyList<ushort> ValueList, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ValueList);
        ValidateRange(addressValue, ValueList.Count, maxWriteRegisterNumber, "写多寄存器");

        // 字序规则统一收口在 寄存器编解码器（R1）：此处不得再手写高低字节拆分，
        // 否则同一套字节序存在两份实现，改一处漏一处。
        var TestPayload = RegisterCodec.RegisterConvertByte(ValueList as ushort[] ?? ValueList.ToArray());

        return ExecuteWriteAsync(
            moduleClient => moduleClient.WriteMultipleRegistersAsync(_options.UnitId, (ushort)addressValue, TestPayload, cancellationToken),
            cancellationToken);
    }

    // ── 执行骨架 ────────────────────────────────────────────────────────

    /// <summary>
    /// 串行化执行一个位读取请求，并把 FluentModbus 返回的原始字节解包为布尔数组。
    /// <para>Modbus 位打包规则：每字节 8 位，<b>低位在前</b>（byte0.bit0 = 第 1 个线圈）。</para>
    /// </summary>
    private async Task<bool[]> ExecuteBitAsync(
        Func<ModbusTcpClient, Task<Memory<byte>>> action,
        int countValue,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var Client = EnsureConnected();
            var SourceValue = await action(Client).ConfigureAwait(false);
            return UnpackBit(SourceValue.Span, countValue);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>串行化执行一个返回寄存器数组的请求。</summary>
    private async Task<ushort[]> ExecuteRegisterAsync(
        Func<ModbusTcpClient, Task<Memory<byte>>> action,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var Client = EnsureConnected();
            var SourceValue = await action(Client).ConfigureAwait(false);
            return UnpackRegister(SourceValue.Span);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>串行化执行一个写请求。</summary>
    private async Task ExecuteWriteAsync(
        Func<ModbusTcpClient, Task> action,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var Client = EnsureConnected();
            await action(Client).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// 把 Modbus 寄存器字节流解包为逻辑寄存器值。
    ///
    /// <para><b>必须逐字按大端解码</b>：Modbus 规定寄存器在报文里<b>高字节在前</b>，
    /// 而 FluentModbus 的字节版接口不做字节序转换。若直接
    /// <c>MemoryMarshal.Cast&lt;byte, ushort&gt;</c>，在小端主机上每个寄存器都会被高低字节对调——
    /// 写入 <c>0x1234</c> 再读回变成 <c>0x3412</c>，且与模拟模式的值对不上
    /// （模拟客户端返回的是逻辑值）。上游 <c>寄存器编解码器</c> 也按逻辑值设计，故必须在入口处纠正。</para>
    /// </summary>
    private static ushort[] UnpackRegister(ReadOnlySpan<byte> data)
    {
        var resultValue = new ushort[data.Length / 2];

        for (var Entry = 0; Entry < resultValue.Length; Entry++)
        {
            resultValue[Entry] = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(Entry * 2, 2));
        }

        return resultValue;
    }

    /// <summary>把 Modbus 位字节流解包为布尔数组（每字节低位在前）。</summary>
    private static bool[] UnpackBit(ReadOnlySpan<byte> data, int countValue)
    {
        var resultValue = new bool[countValue];
        for (var Entry = 0; Entry < countValue; Entry++)
        {
            var byteIndex = Entry / 8;
            if (byteIndex >= data.Length)
            {
                break;
            }

            resultValue[Entry] = (data[byteIndex] & (1 << (Entry % 8))) != 0;
        }

        return resultValue;
    }

    private ModbusTcpClient EnsureConnected()
    {
        if (_client?.IsConnected != true)
        {
            throw new InvalidOperationException("PLC 未连接，无法执行读写操作。");
        }

        return _client;
    }

    private static void ValidateRange(int addressValue, int countValue, int maxCount, string operateOperation)
    {
        if (addressValue < 0 || addressValue > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(addressValue), addressValue, $"{operateOperation}：地址超出 Modbus 地址范围。");
        }

        if (countValue <= 0 || countValue > maxCount)
        {
            // 提示文案由常量推导，避免"写 121"与代码实际使用的上限再次漂移
            throw new ArgumentOutOfRangeException(nameof(countValue), countValue,
                $"{operateOperation}：单次数量必须在 1-{maxCount} 之间。" +
                (maxCount == maxReadRegisterNumber
                    ? $"（汇川 PLC 上限为读 {maxReadRegisterNumber} / 写 {maxWriteRegisterNumber}，建议每块不超过 100）"
                    : string.Empty));
        }
    }

    private static IPAddress ParseHost(string Host)
    {
        if (IPAddress.TryParse(Host, out var addressValue))
        {
            return addressValue;
        }

        var resolved = Dns.GetHostAddresses(Host)
            .FirstOrDefault(item => item.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

        return resolved ?? throw new InvalidOperationException($"无法解析 PLC 主机名：{Host}");
    }

    private void ReleaseClient()
    {
        if (_client is null)
        {
            return;
        }

        try
        {
            _client.Dispose();
        }
        catch (Exception exception)
        {
            _log.LogDebug(exception, "释放 Modbus 客户端时出现异常，已忽略");
        }
        finally
        {
            _client = null;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DisconnectAsync().ConfigureAwait(false);
        _writeGate.Dispose();
    }
}
