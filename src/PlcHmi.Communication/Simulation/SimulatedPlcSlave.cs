using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using 上位机.核心.模型;

namespace 上位机.通信.仿真;

/// <summary>
/// 虚拟 PLC 的 Modbus TCP 从站。把 <see cref="虚拟PLC设备"/> 的内存映像按标准 Modbus TCP
/// 暴露出来，供 Modbus Poll / Modbus Slave 等外部工具连接，实现<b>不接真机的离线调试</b>。
///
/// <para><b>为什么自研而不复用 FluentModbus 服务端：</b>外部工具与上位机自用的
/// <see cref="仿真Modbus客户端"/> 必须操作<b>同一份</b>内存。若用库自带的服务端缓冲，
/// 就得在"库缓冲 ↔ 虚拟内存"之间来回同步，还要处理读写竞态；直接实现协议帧则一步到位——
/// 请求进来就地读写虚拟内存，零同步、零拷贝、零错位。</para>
///
/// <para><b>本类只负责"连接与生命周期"</b>（监听端口、管理调试客户端、启停），
/// 字节级的协议翻译在 <see cref="Modbus从站协议"/>。</para>
///
/// <para><b>安全边界：</b>默认只监听回环地址，避免把一台可写的虚拟 PLC 暴露到车间网络；
/// 需要跨机调试时由管理员在系统设置页显式改监听地址。</para>
/// </summary>
public sealed class 虚拟PLC从站 : IAsyncDisposable
{
    /// <summary>MBAP 报文头长度（与协议实现共用同一常量，避免两处各写一个 7）。</summary>
    private const int 报文头长度 = Modbus从站协议.报文头长度;

    /// <summary>标准 Modbus TCP 帧长上限。</summary>
    private const int 最大帧长 = Modbus从站协议.最大帧长;

    private static readonly TimeSpan 停止等待 = TimeSpan.FromSeconds(2);

    private readonly 虚拟PLC设备 _设备;
    private readonly PLC连接选项 _选项;
    private readonly ILogger<虚拟PLC从站> _日志;
    private readonly Modbus从站协议 _协议;
    private readonly SemaphoreSlim _启停闸锁 = new(1, 1);

    private TcpListener? _监听器;
    private CancellationTokenSource? _取消令牌源;
    private Task? _接受任务;
    private int _客户端数;
    private int _实际端口;
    private string _监听地址 = "127.0.0.1";

    public 虚拟PLC从站(虚拟PLC设备 设备, PLC连接选项 选项, ILogger<虚拟PLC从站> 日志)
    {
        _设备 = 设备;
        _选项 = 选项;
        _日志 = 日志;
        _协议 = new Modbus从站协议(日志);
    }

    /// <summary>从站是否正在监听。</summary>
    public bool 是否运行 { get; private set; }

    /// <summary>实际监听端口（端口配 0 时由系统分配，以实际值为准）。</summary>
    public int 端口 => 是否运行 ? _实际端口 : _选项.模拟从站端口;

    /// <summary>实际监听地址。</summary>
    public string 监听地址 => 是否运行 ? _监听地址 : _选项.模拟从站监听地址;

    /// <summary>当前连接的调试客户端数。</summary>
    public int 已连接客户端数 => Volatile.Read(ref _客户端数);

    /// <summary>监听端点文本，如 <c>127.0.0.1:5020</c>。</summary>
    public string 端点文本 => $"{监听地址}:{端口}";

    /// <summary>给管理员看的从站状态说明。</summary>
    public string 状态文本 => 是否运行
        ? $"监听中 · 已连接 {已连接客户端数} 个调试客户端"
        : "未启动";

    /// <summary>
    /// 启动监听。已在监听时幂等返回。
    /// <para>监听地址非法或端口被占用时抛 <see cref="InvalidOperationException"/>——
    /// 让"切到模拟模式"这一步明确失败，而不是静默地进入一个外部工具连不上的模拟模式。</para>
    /// <para><b>必须异步调用</b>：内部经 <c>SemaphoreSlim.WaitAsync</c> 取启停闸锁。
    /// 早期的同步 <c>Wait()</c> 版本在"UI 线程发起切换、后台线程正持锁停止"时
    /// 会把 UI 线程同步卡住最多 2 秒，并构成 UI 等后台、后台等 UI 的死锁拓扑（R2）。</para>
    /// </summary>
    public async Task 启动Async(CancellationToken 取消令牌 = default)
    {
        await _启停闸锁.WaitAsync(取消令牌).ConfigureAwait(false);

        try
        {
            if (是否运行)
            {
                return;
            }

            var 地址值 = 解析监听地址(_选项.模拟从站监听地址);
            var 监听器 = new TcpListener(地址值, _选项.模拟从站端口);

            // 先挂到字段：任何异常路径都能经 释放监听器() 统一清理，避免套接字泄漏
            _监听器 = 监听器;

            try
            {
                监听器.Start();
            }
            catch (Exception 异常) when (异常 is SocketException or ArgumentOutOfRangeException)
            {
                释放监听器();
                throw new InvalidOperationException(
                    $"模拟 PLC 从站无法监听 {地址值}:{_选项.模拟从站端口} —— {异常.Message}。" +
                    "请在系统设置页改一个空闲端口，或检查该地址是否属于本机。", 异常);
            }

            _监听地址 = 地址值.ToString();
            _实际端口 = ((IPEndPoint)监听器.LocalEndpoint).Port;
            _取消令牌源 = new CancellationTokenSource();
            是否运行 = true;
            _接受任务 = Task.Run(() => 异步接受客户端(监听器, _取消令牌源.Token), CancellationToken.None);

            _日志.LogWarning(
                "虚拟 PLC Modbus 从站已启动：{Endpoint}（可用 Modbus 工具连接进行离线调试）", 端点文本);
        }
        finally
        {
            _启停闸锁.Release();
        }
    }

    /// <summary>停止监听并断开全部调试客户端。未运行时幂等返回。</summary>
    public async Task 异步停止()
    {
        await _启停闸锁.WaitAsync().ConfigureAwait(false);

        try
        {
            if (!是否运行)
            {
                return;
            }

            是否运行 = false;

            var 取消令牌源 = _取消令牌源;
            if (取消令牌源 is not null)
            {
                await 取消令牌源.CancelAsync().ConfigureAwait(false);
            }

            释放监听器();

            if (_接受任务 is not null)
            {
                try
                {
                    await _接受任务.WaitAsync(停止等待).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // 2 秒内没退出 = 接受循环卡住的征兆，不是正常关闭，留痕
                    _日志.LogWarning("等待虚拟 PLC 从站接受循环退出超时（{Seconds}s），已放弃等待", 停止等待.TotalSeconds);
                }
                catch (Exception 异常)
                {
                    _日志.LogDebug(异常, "虚拟 PLC 从站接受循环退出时出现异常，已忽略");
                }
            }

            _接受任务 = null;
            _取消令牌源?.Dispose();
            _取消令牌源 = null;
            Volatile.Write(ref _客户端数, 0);

            _日志.LogInformation("虚拟 PLC Modbus 从站已停止");
        }
        finally
        {
            _启停闸锁.Release();
        }
    }

    // ── 监听与连接管理 ──────────────────────────────────────────────────

    private async Task 异步接受客户端(TcpListener 监听器, CancellationToken 取消令牌)
    {
        while (!取消令牌.IsCancellationRequested)
        {
            try
            {
                var 客户端 = await 监听器.AcceptTcpClientAsync(取消令牌).ConfigureAwait(false);
                Interlocked.Increment(ref _客户端数);
                _ = Task.Run(() => 异步服务客户端(客户端, 取消令牌), CancellationToken.None);
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
            catch (SocketException 异常)
            {
                // 瞬时套接字错误只影响这一次 Accept：继续监听，
                // 否则从站会变成"显示运行中却再也接不上"的僵尸状态。
                _日志.LogWarning(异常, "虚拟 PLC 从站接受连接时套接字异常，继续监听");
            }
        }
    }

    private async Task 异步服务客户端(TcpClient 客户端, CancellationToken 取消令牌)
    {
        try
        {
            using (客户端)
            {
                客户端.NoDelay = true;
                var 流 = 客户端.GetStream();
                var 报文头 = new byte[报文头长度];

                while (!取消令牌.IsCancellationRequested)
                {
                    if (!await 异步读满(流, 报文头, 取消令牌).ConfigureAwait(false))
                    {
                        break;
                    }

                    // MBAP 长度字段位于偏移 4–5（长度 = 单元号 1 字节 + PDU 字节数）。
                    // 写到偏移 5 会读到"长度低字节 + 单元号"，帧长必然错乱——这是最容易写错的一处。
                    var 长度 = BinaryPrimitives.ReadUInt16BigEndian(报文头.AsSpan(4, 2));

                    // 合法长度：单元号(1) + PDU(≥1)，且整帧不超过标准上限
                    if (长度 < 2 || 长度 > 最大帧长 - 报文头长度)
                    {
                        break;
                    }

                    var 协议数据 = new byte[长度 - 1];
                    if (!await 异步读满(流, 协议数据, 取消令牌).ConfigureAwait(false))
                    {
                        break;
                    }

                    var 响应 = _协议.处理请求(报文头, 协议数据, _设备.内存);
                    await 流.WriteAsync(响应, 取消令牌).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 停止
        }
        catch (Exception 异常) when (异常 is IOException or SocketException or ObjectDisposedException)
        {
            // 调试工具断开属常态，降级为 Debug
            _日志.LogDebug(异常, "调试客户端连接中断");
        }
        finally
        {
            Interlocked.Decrement(ref _客户端数);
        }
    }

    /// <summary>读满指定字节数；对端关闭时返回 <c>false</c>。</summary>
    private static async Task<bool> 异步读满(NetworkStream 流, byte[] 缓冲, CancellationToken 取消令牌)
    {
        var 已读 = 0;

        while (已读 < 缓冲.Length)
        {
            var 本次 = await 流.ReadAsync(缓冲.AsMemory(已读), 取消令牌).ConfigureAwait(false);

            if (本次 == 0)
            {
                return false;
            }

            已读 += 本次;
        }

        return true;
    }

    /// <summary>停止并释放监听器，幂等。停止/启动失败路径都经它收口，避免套接字泄漏。</summary>
    private void 释放监听器()
    {
        var 监听器 = _监听器;
        _监听器 = null;

        if (监听器 is null)
        {
            return;
        }

        try
        {
            监听器.Stop();
        }
        catch (SocketException 异常)
        {
            // 关闭监听时套接字已失效属预期（进程退出、端口被抢占等），不值得惊动操作员
            _日志.LogDebug(异常, "停止模拟从站监听器时出现套接字异常，已忽略");
        }
    }

    private static IPAddress 解析监听地址(string 文本)
    {
        var 值 = 文本?.Trim();

        if (string.IsNullOrEmpty(值))
        {
            return IPAddress.Loopback;
        }

        return IPAddress.TryParse(值, out var 地址值)
            ? 地址值
            : throw new InvalidOperationException($"模拟从站监听地址「{文本}」不是合法的 IP 地址。");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await 异步停止().ConfigureAwait(false);
        _启停闸锁.Dispose();
    }
}
