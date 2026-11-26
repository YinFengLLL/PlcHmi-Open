using System.Buffers.Binary;
using System.Net;
using FluentModbus;
using Microsoft.Extensions.Logging;
using 上位机.核心.抽象;
using 上位机.核心.模型;

namespace 上位机.通信.Modbus;

/// <summary>
/// 基于 FluentModbus 的 Modbus TCP 客户端实现。
///
/// <para><b>职责边界：</b>只负责“把字节收发到位”。软元件地址映射由
/// <c>InovanceAddressResolver</c> 完成，数据类型与字序由 <c>RegisterCodec</c> 完成。
/// 这样更换通信库（例如换成 NModbus）不影响地址与解析逻辑。</para>
///
/// <para><b>并发约束：</b>Modbus 是请求-应答式协议，同一连接不能并发请求，
/// 因此所有收发都经过 <see cref="_写入闸锁"/> 串行化。
/// <b>汇川 H5U 默认最多 8 个客户端连接</b>，故整个上位机只维持一个连接实例。</para>
/// </summary>
public sealed class FluentModbusTCP客户端 : Modbus客户端接口
{
    /// <summary>标准 Modbus 单次读寄存器上限。</summary>
    private const int 最大读取寄存器数 = 125;

    /// <summary>标准 Modbus 单次写寄存器上限。</summary>
    private const int 最大写入寄存器数 = 123;

    /// <summary>标准 Modbus 单次读线圈上限。</summary>
    private const int 最大读取线圈数 = 2000;

    /// <summary>
    /// 标准 Modbus FC0F 单次<b>写</b>线圈上限（0x07B0 = 1968）。
    /// <para>写上限低于读上限（2000）：报文长度受 PDU 256 字节约束，
    /// 1968 位 = 246 字节。放行到读上限会让 PLC 返回异常码 03。</para>
    /// </summary>
    private const int 最大写入线圈数 = 1968;

    private readonly PLC连接选项 _选项;
    private readonly ILogger<FluentModbusTCP客户端> _日志;
    private readonly SemaphoreSlim _写入闸锁 = new(1, 1);

    private ModbusTcpClient? _客户端;
    private bool _已释放;

    public FluentModbusTCP客户端(PLC连接选项 选项, ILogger<FluentModbusTCP客户端> 日志)
    {
        _选项 = 选项;
        _日志 = 日志;
    }

    /// <inheritdoc />
    public bool 是否已连接 => _客户端?.IsConnected == true;

    /// <inheritdoc />
    public async Task 异步连接(CancellationToken 取消令牌 = default)
    {
        await _写入闸锁.WaitAsync(取消令牌).ConfigureAwait(false);
        try
        {
            if (_客户端?.IsConnected == true)
            {
                return;
            }

            释放客户端();

            // 先记账再连接：Connect 抛异常时由 catch 里的 释放客户端()
            // 统一释放这个未连上的实例。此前失败路径释放的是 _客户端 字段
            // （此时为旧实例或 null），新实例永不 Dispose——重连按指数退避
            // 无限重试，这里恰是最高频执行的失败路径，每失败一次泄漏一个 Socket。
            _客户端 = new ModbusTcpClient
            {
                ConnectTimeout = _选项.连接超时毫秒,
                ReadTimeout = _选项.读取超时毫秒,
                WriteTimeout = _选项.读取超时毫秒,
            };

            var 结束点位 = new IPEndPoint(解析主机(_选项.主机), _选项.端口);

            // FluentModbus 的 Connect 是同步阻塞调用，放到线程池执行避免卡住 UI 线程
            await Task.Run(() => _客户端.Connect(结束点位, ModbusEndianness.BigEndian), 取消令牌)
                .ConfigureAwait(false);

            _日志.LogInformation("已连接 PLC {Host}:{Port}（从站号 {UnitId}）",
                _选项.主机, _选项.端口, _选项.单元标识);
        }
        catch (Exception 异常)
        {
            释放客户端();
            _日志.LogWarning(异常, "连接 PLC {Host}:{Port} 失败", _选项.主机, _选项.端口);
            throw;
        }
        finally
        {
            _写入闸锁.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 用 <c>WaitAsync</c> 而不是 <c>Wait()</c>：
    /// 同步等待会占住调用线程，若此时 UI 线程正同步等待后台线程（反之亦然）就会死锁。
    /// </remarks>
    public async Task 异步断开()
    {
        await _写入闸锁.WaitAsync().ConfigureAwait(false);

        try
        {
            if (_客户端 is not null)
            {
                try
                {
                    _客户端.Disconnect();
                }
                catch (Exception 异常)
                {
                    _日志.LogDebug(异常, "断开连接时出现异常，已忽略");
                }
            }

            释放客户端();
            _日志.LogInformation("已断开 PLC 连接");
        }
        finally
        {
            _写入闸锁.Release();
        }
    }

    // ── 读 ──────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<bool[]> 异步读取线圈(int 地址值, int 数量值, CancellationToken 取消令牌 = default)
    {
        校验范围(地址值, 数量值, 最大读取线圈数, "读线圈");
        return 异步执行位(
            模组客户端 => 模组客户端.ReadCoilsAsync(_选项.单元标识, 地址值, 数量值, 取消令牌),
            数量值,
            取消令牌);
    }

    /// <inheritdoc />
    public Task<bool[]> 异步读取离散输入(int 地址值, int 数量值, CancellationToken 取消令牌 = default)
    {
        校验范围(地址值, 数量值, 最大读取线圈数, "读离散输入");
        return 异步执行位(
            模组客户端 => 模组客户端.ReadDiscreteInputsAsync(_选项.单元标识, 地址值, 数量值, 取消令牌),
            数量值,
            取消令牌);
    }

    /// <inheritdoc />
    public Task<ushort[]> 异步读取保持寄存器(int 地址值, int 数量值, CancellationToken 取消令牌 = default)
    {
        校验范围(地址值, 数量值, 最大读取寄存器数, "读保持寄存器");
        return 异步执行寄存器(
            模组客户端 => 模组客户端.ReadHoldingRegistersAsync(_选项.单元标识, (ushort)地址值, (ushort)数量值, 取消令牌),
            取消令牌);
    }

    /// <inheritdoc />
    public Task<ushort[]> 异步读取输入寄存器(int 地址值, int 数量值, CancellationToken 取消令牌 = default)
    {
        校验范围(地址值, 数量值, 最大读取寄存器数, "读输入寄存器");
        return 异步执行寄存器(
            模组客户端 => 模组客户端.ReadInputRegistersAsync(_选项.单元标识, (ushort)地址值, (ushort)数量值, 取消令牌),
            取消令牌);
    }

    // ── 写 ──────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task 异步写入单个线圈(int 地址值, bool 值, CancellationToken 取消令牌 = default)
    {
        return 异步执行写入(
            模组客户端 => 模组客户端.WriteSingleCoilAsync(_选项.单元标识, 地址值, 值, 取消令牌),
            取消令牌);
    }

    /// <inheritdoc />
    public Task 异步写入单个寄存器(int 地址值, ushort 值, CancellationToken 取消令牌 = default)
    {
        return 异步执行写入(
            模组客户端 => 模组客户端.WriteSingleRegisterAsync(_选项.单元标识, 地址值, 值, 取消令牌),
            取消令牌);
    }

    /// <inheritdoc />
    public Task 异步写入多个线圈(int 地址值, IReadOnlyList<bool> 值列表, CancellationToken 取消令牌 = default)
    {
        ArgumentNullException.ThrowIfNull(值列表);
        校验范围(地址值, 值列表.Count, 最大写入线圈数, "写多线圈");

        var 位载荷 = 值列表 as bool[] ?? 值列表.ToArray();
        return 异步执行写入(
            模组客户端 => 模组客户端.WriteMultipleCoilsAsync(_选项.单元标识, 地址值, 位载荷, 取消令牌),
            取消令牌);
    }

    /// <inheritdoc />
    public Task 异步写入多个寄存器(int 地址值, IReadOnlyList<ushort> 值列表, CancellationToken 取消令牌 = default)
    {
        ArgumentNullException.ThrowIfNull(值列表);
        校验范围(地址值, 值列表.Count, 最大写入寄存器数, "写多寄存器");

        // 字序规则统一收口在 寄存器编解码器（R1）：此处不得再手写高低字节拆分，
        // 否则同一套字节序存在两份实现，改一处漏一处。
        var 测试载荷 = 寄存器编解码器.寄存器转字节(值列表 as ushort[] ?? 值列表.ToArray());

        return 异步执行写入(
            模组客户端 => 模组客户端.WriteMultipleRegistersAsync(_选项.单元标识, (ushort)地址值, 测试载荷, 取消令牌),
            取消令牌);
    }

    // ── 执行骨架 ────────────────────────────────────────────────────────

    /// <summary>
    /// 串行化执行一个位读取请求，并把 FluentModbus 返回的原始字节解包为布尔数组。
    /// <para>Modbus 位打包规则：每字节 8 位，<b>低位在前</b>（byte0.bit0 = 第 1 个线圈）。</para>
    /// </summary>
    private async Task<bool[]> 异步执行位(
        Func<ModbusTcpClient, Task<Memory<byte>>> 动作,
        int 数量值,
        CancellationToken 取消令牌)
    {
        await _写入闸锁.WaitAsync(取消令牌).ConfigureAwait(false);
        try
        {
            var 客户端 = 确保已连接();
            var 原始值 = await 动作(客户端).ConfigureAwait(false);
            return 解包位(原始值.Span, 数量值);
        }
        finally
        {
            _写入闸锁.Release();
        }
    }

    /// <summary>串行化执行一个返回寄存器数组的请求。</summary>
    private async Task<ushort[]> 异步执行寄存器(
        Func<ModbusTcpClient, Task<Memory<byte>>> 动作,
        CancellationToken 取消令牌)
    {
        await _写入闸锁.WaitAsync(取消令牌).ConfigureAwait(false);
        try
        {
            var 客户端 = 确保已连接();
            var 原始值 = await 动作(客户端).ConfigureAwait(false);
            return 解包寄存器(原始值.Span);
        }
        finally
        {
            _写入闸锁.Release();
        }
    }

    /// <summary>串行化执行一个写请求。</summary>
    private async Task 异步执行写入(
        Func<ModbusTcpClient, Task> 动作,
        CancellationToken 取消令牌)
    {
        await _写入闸锁.WaitAsync(取消令牌).ConfigureAwait(false);
        try
        {
            var 客户端 = 确保已连接();
            await 动作(客户端).ConfigureAwait(false);
        }
        finally
        {
            _写入闸锁.Release();
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
    private static ushort[] 解包寄存器(ReadOnlySpan<byte> 数据)
    {
        var 结果值 = new ushort[数据.Length / 2];

        for (var 条目 = 0; 条目 < 结果值.Length; 条目++)
        {
            结果值[条目] = BinaryPrimitives.ReadUInt16BigEndian(数据.Slice(条目 * 2, 2));
        }

        return 结果值;
    }

    /// <summary>把 Modbus 位字节流解包为布尔数组（每字节低位在前）。</summary>
    private static bool[] 解包位(ReadOnlySpan<byte> 数据, int 数量值)
    {
        var 结果值 = new bool[数量值];
        for (var 条目 = 0; 条目 < 数量值; 条目++)
        {
            var 字节序号 = 条目 / 8;
            if (字节序号 >= 数据.Length)
            {
                break;
            }

            结果值[条目] = (数据[字节序号] & (1 << (条目 % 8))) != 0;
        }

        return 结果值;
    }

    private ModbusTcpClient 确保已连接()
    {
        if (_客户端?.IsConnected != true)
        {
            throw new InvalidOperationException("PLC 未连接，无法执行读写操作。");
        }

        return _客户端;
    }

    private static void 校验范围(int 地址值, int 数量值, int 最大数量, string 操作)
    {
        if (地址值 < 0 || 地址值 > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(地址值), 地址值, $"{操作}：地址超出 Modbus 地址范围。");
        }

        if (数量值 <= 0 || 数量值 > 最大数量)
        {
            // 提示文案由常量推导，避免"写 121"与代码实际使用的上限再次漂移
            throw new ArgumentOutOfRangeException(nameof(数量值), 数量值,
                $"{操作}：单次数量必须在 1-{最大数量} 之间。" +
                (最大数量 == 最大读取寄存器数
                    ? $"（汇川 PLC 上限为读 {最大读取寄存器数} / 写 {最大写入寄存器数}，建议每块不超过 100）"
                    : string.Empty));
        }
    }

    private static IPAddress 解析主机(string 主机)
    {
        if (IPAddress.TryParse(主机, out var 地址值))
        {
            return 地址值;
        }

        var 已解析 = Dns.GetHostAddresses(主机)
            .FirstOrDefault(甲 => 甲.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

        return 已解析 ?? throw new InvalidOperationException($"无法解析 PLC 主机名：{主机}");
    }

    private void 释放客户端()
    {
        if (_客户端 is null)
        {
            return;
        }

        try
        {
            _客户端.Dispose();
        }
        catch (Exception 异常)
        {
            _日志.LogDebug(异常, "释放 Modbus 客户端时出现异常，已忽略");
        }
        finally
        {
            _客户端 = null;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_已释放)
        {
            return;
        }

        _已释放 = true;
        await 异步断开().ConfigureAwait(false);
        _写入闸锁.Dispose();
    }
}
