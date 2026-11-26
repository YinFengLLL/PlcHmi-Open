using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using PlcHmi.Core.Models;

namespace PlcHmi.Communication.Simulation;

/// <summary>
/// 虚拟 PLC 的 Modbus TCP 从站。把 <see cref="VirtualPlcDevice"/> 的内存映像按标准 Modbus TCP
/// 暴露出来，供 Modbus Poll / Modbus Slave 等外部工具连接，实现<b>不接真机的离线调试</b>。
///
/// <para><b>为什么自研而不复用 FluentModbus 服务端：</b>外部工具与上位机自用的
/// <see cref="SimulationModbusClient"/> 必须操作<b>同一份</b>内存。若用库自带的服务端缓冲，
/// 就得在"库缓冲 ↔ 虚拟内存"之间来回同步，还要处理读写竞态；直接实现协议帧则一步到位——
/// 请求进来就地读写虚拟内存，零同步、零拷贝、零错位。</para>
///
/// <para><b>本类只负责"连接与生命周期"</b>（监听端口、管理调试客户端、启停），
/// 字节级的协议翻译在 <see cref="ModbusSlaveProtocol"/>。</para>
///
/// <para><b>安全边界：</b>默认只监听回环地址，避免把一台可写的虚拟 PLC 暴露到车间网络；
/// 需要跨机调试时由管理员在系统设置页显式改监听地址。</para>
/// </summary>
public sealed class VirtualPlcSlave : IAsyncDisposable
{
    /// <summary>MBAP 报文头长度（与协议实现共用同一常量，避免两处各写一个 7）。</summary>
    private const int messageHeadLength = ModbusSlaveProtocol.messageHeadLength;

    /// <summary>标准 Modbus TCP 帧长上限。</summary>
    private const int maxFrameLong = ModbusSlaveProtocol.maxFrameLong;

    private static readonly TimeSpan stopWait = TimeSpan.FromSeconds(2);

    private readonly VirtualPlcDevice _device;
    private readonly PlcConnectionOptions _options;
    private readonly ILogger<VirtualPlcSlave> _log;
    private readonly ModbusSlaveProtocol _protocol;
    private readonly SemaphoreSlim _startStopGate = new(1, 1);

    private TcpListener? _listen;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _acceptTask;
    private int _clientNumber;
    private int _actualPort;
    private string _listenAddress = "127.0.0.1";

    public VirtualPlcSlave(VirtualPlcDevice Device, PlcConnectionOptions Options, ILogger<VirtualPlcSlave> Log)
    {
        _device = Device;
        _options = Options;
        _log = Log;
        _protocol = new ModbusSlaveProtocol(Log);
    }

    /// <summary>从站是否正在监听。</summary>
    public bool IsRun { get; private set; }

    /// <summary>实际监听端口（端口配 0 时由系统分配，以实际值为准）。</summary>
    public int Port => IsRun ? _actualPort : _options.SimulationSlavePort;

    /// <summary>实际监听地址。</summary>
    public string ListenAddress => IsRun ? _listenAddress : _options.SimulationSlaveListenAddress;

    /// <summary>当前连接的调试客户端数。</summary>
    public int ConnectedClientNumber => Volatile.Read(ref _clientNumber);

    /// <summary>监听端点文本，如 <c>127.0.0.1:5020</c>。</summary>
    public string EndpointText => $"{ListenAddress}:{Port}";

    /// <summary>给管理员看的从站状态说明。</summary>
    public string StateText => IsRun
        ? $"监听中 · 已连接 {ConnectedClientNumber} 个调试客户端"
        : "未启动";

    /// <summary>
    /// 启动监听。已在监听时幂等返回。
    /// <para>监听地址非法或端口被占用时抛 <see cref="InvalidOperationException"/>——
    /// 让"切到模拟模式"这一步明确失败，而不是静默地进入一个外部工具连不上的模拟模式。</para>
    /// <para><b>必须异步调用</b>：内部经 <c>SemaphoreSlim.WaitAsync</c> 取启停闸锁。
    /// 早期的同步 <c>Wait()</c> 版本在"UI 线程发起切换、后台线程正持锁停止"时
    /// 会把 UI 线程同步卡住最多 2 秒，并构成 UI 等后台、后台等 UI 的死锁拓扑（R2）。</para>
    /// </summary>
    public async Task LaunchAsync(CancellationToken cancellationToken = default)
    {
        await _startStopGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (IsRun)
            {
                return;
            }

            var addressValue = ParseListenAddress(_options.SimulationSlaveListenAddress);
            var Listen = new TcpListener(addressValue, _options.SimulationSlavePort);

            // 先挂到字段：任何异常路径都能经 释放监听器() 统一清理，避免套接字泄漏
            _listen = Listen;

            try
            {
                Listen.Start();
            }
            catch (Exception exception) when (exception is SocketException or ArgumentOutOfRangeException)
            {
                ReleaseListen();
                throw new InvalidOperationException(
                    $"模拟 PLC 从站无法监听 {addressValue}:{_options.SimulationSlavePort} —— {exception.Message}。" +
                    "请在系统设置页改一个空闲端口，或检查该地址是否属于本机。", exception);
            }

            _listenAddress = addressValue.ToString();
            _actualPort = ((IPEndPoint)Listen.LocalEndpoint).Port;
            _cancellationTokenSource = new CancellationTokenSource();
            IsRun = true;
            _acceptTask = Task.Run(() => AcceptClientAsync(Listen, _cancellationTokenSource.Token), CancellationToken.None);

            _log.LogWarning(
                "虚拟 PLC Modbus 从站已启动：{Endpoint}（可用 Modbus 工具连接进行离线调试）", EndpointText);
        }
        finally
        {
            _startStopGate.Release();
        }
    }

    /// <summary>停止监听并断开全部调试客户端。未运行时幂等返回。</summary>
    public async Task StopAsync()
    {
        await _startStopGate.WaitAsync().ConfigureAwait(false);

        try
        {
            if (!IsRun)
            {
                return;
            }

            IsRun = false;

            var CancellationTokenSource = _cancellationTokenSource;
            if (CancellationTokenSource is not null)
            {
                await CancellationTokenSource.CancelAsync().ConfigureAwait(false);
            }

            ReleaseListen();

            if (_acceptTask is not null)
            {
                try
                {
                    await _acceptTask.WaitAsync(stopWait).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // 2 秒内没退出 = 接受循环卡住的征兆，不是正常关闭，留痕
                    _log.LogWarning("等待虚拟 PLC 从站接受循环退出超时（{Seconds}s），已放弃等待", stopWait.TotalSeconds);
                }
                catch (Exception exception)
                {
                    _log.LogDebug(exception, "虚拟 PLC 从站接受循环退出时出现异常，已忽略");
                }
            }

            _acceptTask = null;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            Volatile.Write(ref _clientNumber, 0);

            _log.LogInformation("虚拟 PLC Modbus 从站已停止");
        }
        finally
        {
            _startStopGate.Release();
        }
    }

    // ── 监听与连接管理 ──────────────────────────────────────────────────

    private async Task AcceptClientAsync(TcpListener Listen, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var Client = await Listen.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _clientNumber);
                _ = Task.Run(() => ServiceClientAsync(Client, cancellationToken), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                // 正常停止
                break;
            }
            catch (ObjectDisposedException)
            {
                // 停止时监听器已释放
                break;
            }
            catch (SocketException exception)
            {
                // 瞬时套接字错误只影响这一次 Accept：继续监听，
                // 否则从站会变成"显示运行中却再也接不上"的僵尸状态。
                _log.LogWarning(exception, "虚拟 PLC 从站接受连接时套接字异常，继续监听");
            }
        }
    }

    private async Task ServiceClientAsync(TcpClient Client, CancellationToken cancellationToken)
    {
        try
        {
            using (Client)
            {
                Client.NoDelay = true;
                var stream = Client.GetStream();
                var messageHead = new byte[messageHeadLength];

                while (!cancellationToken.IsCancellationRequested)
                {
                    if (!await ReadFullAsync(stream, messageHead, cancellationToken).ConfigureAwait(false))
                    {
                        break;
                    }

                    // MBAP 长度字段位于偏移 4–5（长度 = 单元号 1 字节 + PDU 字节数）。
                    // 写到偏移 5 会读到"长度低字节 + 单元号"，帧长必然错乱——这是最容易写错的一处。
                    var length = BinaryPrimitives.ReadUInt16BigEndian(messageHead.AsSpan(4, 2));

                    // 合法长度：单元号(1) + PDU(≥1)，且整帧不超过标准上限
                    if (length < 2 || length > maxFrameLong - messageHeadLength)
                    {
                        break;
                    }

                    var protocolData = new byte[length - 1];
                    if (!await ReadFullAsync(stream, protocolData, cancellationToken).ConfigureAwait(false))
                    {
                        break;
                    }

                    var response = _protocol.ProcessRequest(messageHead, protocolData, _device.memory);
                    await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 停止
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
            // 调试工具断开属常态，降级为 Debug
            _log.LogDebug(exception, "调试客户端连接中断");
        }
        finally
        {
            Interlocked.Decrement(ref _clientNumber);
        }
    }

    /// <summary>读满指定字节数；对端关闭时返回 <c>false</c>。</summary>
    private static async Task<bool> ReadFullAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;

        while (read < buffer.Length)
        {
            var thisTimes = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);

            if (thisTimes == 0)
            {
                return false;
            }

            read += thisTimes;
        }

        return true;
    }

    /// <summary>停止并释放监听器，幂等。停止/启动失败路径都经它收口，避免套接字泄漏。</summary>
    private void ReleaseListen()
    {
        var Listen = _listen;
        _listen = null;

        if (Listen is null)
        {
            return;
        }

        try
        {
            Listen.Stop();
        }
        catch (SocketException exception)
        {
            // 关闭监听时套接字已失效属预期（进程退出、端口被抢占等），不值得惊动操作员
            _log.LogDebug(exception, "停止模拟从站监听器时出现套接字异常，已忽略");
        }
    }

    private static IPAddress ParseListenAddress(string Text)
    {
        var Value = Text?.Trim();

        if (string.IsNullOrEmpty(Value))
        {
            return IPAddress.Loopback;
        }

        return IPAddress.TryParse(Value, out var addressValue)
            ? addressValue
            : throw new InvalidOperationException($"模拟从站监听地址「{Text}」不是合法的 IP 地址。");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _startStopGate.Dispose();
    }
}
