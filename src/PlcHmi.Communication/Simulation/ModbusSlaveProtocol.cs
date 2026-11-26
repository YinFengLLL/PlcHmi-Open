using System.Buffers.Binary;
using Microsoft.Extensions.Logging;

namespace 上位机.通信.仿真;

/// <summary>
/// 虚拟 PLC 从站的 Modbus TCP 协议实现：把一条请求 PDU 翻译成对
/// <see cref="虚拟PLC内存"/> 的读写，并组出响应帧。
///
/// <para><b>为什么单独一个文件：</b>协议解析是纯字节级的活（位打包、字节序、异常码、地址边界），
/// 与"监听端口、管理客户端连接"是两类完全不同的关注点。分开后各自都好读、也好单独验证；
/// 合成一个 590 行的类只会让"到底哪段是协议、哪段是网络"变得含糊。</para>
///
/// <para><b>协议范围：</b>FC 01/02/03/04（读位 / 读寄存器）与 FC 05/06/0F/10（写单线圈 / 写单寄存器 /
/// 写多线圈 / 写多寄存器），覆盖调试工具的常用功能。寄存器按原样 16 位大端转发，
/// <b>不做 32 位字序拆分</b>——字序是客户端侧 <c>寄存器编解码器</c> 的职责，从站不越界。</para>
/// </summary>
internal sealed class Modbus从站协议
{
    /// <summary>Modbus 标准单次读线圈上限。</summary>
    private const int 最大读线圈 = 2000;

    /// <summary>Modbus 标准单次读寄存器上限。</summary>
    private const int 最大读寄存器 = 125;

    /// <summary>Modbus 标准单次写线圈上限。</summary>
    private const int 最大写线圈 = 1968;

    /// <summary>Modbus 标准单次写寄存器上限。</summary>
    private const int 最大写寄存器 = 123;

    /// <summary>MBAP 报文头长度：事务号 2 + 协议号 2 + 长度 2 + 单元号 1。</summary>
    public const int 报文头长度 = 7;

    /// <summary>标准 Modbus TCP 帧长上限。</summary>
    public const int 最大帧长 = 260;

    /// <summary>虚拟软元件区容量（与 <see cref="虚拟PLC内存"/> 的数组长度一致）。</summary>
    private const int 软元件容量 = 65536;

    private readonly ILogger _日志;

    public Modbus从站协议(ILogger 日志)
    {
        _日志 = 日志;
    }

    /// <summary>处理一条请求，返回完整响应帧（MBAP + PDU）。</summary>
    public byte[] 处理请求(ReadOnlySpan<byte> 报文头, byte[] 协议数据, 虚拟PLC内存 内存)
    {
        var 功能码 = 协议数据[0];

        try
        {
            var 响应 = 功能码 switch
            {
                0x01 => 读位(功能码, 协议数据, 区间 => 内存.读取线圈(区间.地址, 区间.数量), 最大读线圈),
                0x02 => 读位(功能码, 协议数据, 区间 => 内存.读取离散输入(区间.地址, 区间.数量), 最大读线圈),
                0x03 => 读寄存器(功能码, 协议数据, 区间 => 内存.读取保持寄存器(区间.地址, 区间.数量), 最大读寄存器),
                0x04 => 读寄存器(功能码, 协议数据, 区间 => 内存.读取输入寄存器(区间.地址, 区间.数量), 最大读寄存器),
                0x05 => 写单个线圈(协议数据, 内存),
                0x06 => 写单个寄存器(协议数据, 内存),
                0x0F => 写多个线圈(协议数据, 内存),
                0x10 => 写多个寄存器(协议数据, 内存),
                _ => 异常响应(功能码, 0x01),
            };

            return 组包响应(报文头, 响应);
        }
        catch (Modbus协议异常 异常)
        {
            _日志.LogDebug("Modbus 从站请求被拒（功能码 0x{Function:X2}，异常码 0x{Code:X2}）：{Message}",
                功能码, 异常.异常码, 异常.Message);

            return 组包响应(报文头, 异常响应(功能码, 异常.异常码));
        }
        catch (Exception 异常)
        {
            // 虚拟内存越界等未预期错误：回"非法数据地址"，并留下痕迹
            _日志.LogWarning(异常, "处理 Modbus 从站请求时出现未预期异常（功能码 0x{Function:X2}）", 功能码);
            return 组包响应(报文头, 异常响应(功能码, 0x02));
        }
    }

    // ── 读 ──────────────────────────────────────────────────────────────

    /// <summary>读线圈 / 离散输入（FC 01 / 02）。</summary>
    private static byte[] 读位(
        byte 功能码,
        byte[] 协议数据,
        Func<(int 地址, int 数量), bool[]> 读取,
        int 上限)
    {
        if (协议数据.Length != 5)
        {
            throw new Modbus协议异常(0x03, "读位请求长度非法。");
        }

        var (地址, 数量) = 解析起始与数量(协议数据);

        if (数量 < 1 || 数量 > 上限)
        {
            throw new Modbus协议异常(0x03, $"单次读位数量必须在 1–{上限} 之间。");
        }

        校验地址范围(地址, 数量);

        var 位列表 = 读取((地址, 数量));
        var 字节数 = (数量 + 7) / 8;
        var 响应 = new byte[2 + 字节数];
        响应[0] = 功能码;
        响应[1] = (byte)字节数;

        for (var 条目 = 0; 条目 < 数量; 条目++)
        {
            if (位列表[条目])
            {
                // Modbus 位打包：每字节低位在前
                响应[2 + 条目 / 8] |= (byte)(1 << (条目 % 8));
            }
        }

        return 响应;
    }

    /// <summary>读保持 / 输入寄存器（FC 03 / 04），寄存器按大端 16 位输出。</summary>
    private static byte[] 读寄存器(
        byte 功能码,
        byte[] 协议数据,
        Func<(int 地址, int 数量), ushort[]> 读取,
        int 上限)
    {
        if (协议数据.Length != 5)
        {
            throw new Modbus协议异常(0x03, "读寄存器请求长度非法。");
        }

        var (地址, 数量) = 解析起始与数量(协议数据);

        if (数量 < 1 || 数量 > 上限)
        {
            throw new Modbus协议异常(0x03, $"单次读寄存器数量必须在 1–{上限} 之间。");
        }

        校验地址范围(地址, 数量);

        var 寄存器列表 = 读取((地址, 数量));
        var 响应 = new byte[2 + 数量 * 2];
        响应[0] = 功能码;
        响应[1] = (byte)(数量 * 2);

        for (var 条目 = 0; 条目 < 数量; 条目++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(响应.AsSpan(2 + 条目 * 2, 2), 寄存器列表[条目]);
        }

        return 响应;
    }

    // ── 写 ──────────────────────────────────────────────────────────────

    /// <summary>写单个线圈（FC 05），响应原样回显。</summary>
    private static byte[] 写单个线圈(byte[] 协议数据, 虚拟PLC内存 内存)
    {
        if (协议数据.Length != 5)
        {
            throw new Modbus协议异常(0x03, "写单线圈请求长度非法。");
        }

        var 地址 = BinaryPrimitives.ReadUInt16BigEndian(协议数据.AsSpan(1, 2));
        var 值 = BinaryPrimitives.ReadUInt16BigEndian(协议数据.AsSpan(3, 2));

        if (值 != 0x0000 && 值 != 0xFF00)
        {
            throw new Modbus协议异常(0x03, $"写单线圈的值必须为 0x0000 或 0xFF00，收到 0x{值:X4}。");
        }

        校验地址范围(地址, 1);
        内存.写入线圈(地址, 值 == 0xFF00);

        return new byte[] { 0x05, 协议数据[1], 协议数据[2], 协议数据[3], 协议数据[4] };
    }

    /// <summary>写单个寄存器（FC 06），响应原样回显。</summary>
    private static byte[] 写单个寄存器(byte[] 协议数据, 虚拟PLC内存 内存)
    {
        if (协议数据.Length != 5)
        {
            throw new Modbus协议异常(0x03, "写单寄存器请求长度非法。");
        }

        var 地址 = BinaryPrimitives.ReadUInt16BigEndian(协议数据.AsSpan(1, 2));
        var 值 = BinaryPrimitives.ReadUInt16BigEndian(协议数据.AsSpan(3, 2));

        校验地址范围(地址, 1);
        内存.写入保持寄存器(地址, 值);

        return new byte[] { 0x06, 协议数据[1], 协议数据[2], 协议数据[3], 协议数据[4] };
    }

    /// <summary>写多个线圈（FC 0F）。</summary>
    private static byte[] 写多个线圈(byte[] 协议数据, 虚拟PLC内存 内存)
    {
        var (地址, 数量, 字节数, 起始) = 解析写多个头(协议数据, 最大写线圈);

        if (协议数据.Length != 起始 + 字节数)
        {
            throw new Modbus协议异常(0x03, "写多线圈的字节数与报文长度不一致。");
        }

        校验地址范围(地址, 数量);

        var 位列表 = new bool[数量];
        for (var 条目 = 0; 条目 < 数量; 条目++)
        {
            位列表[条目] = (协议数据[起始 + 条目 / 8] & (1 << (条目 % 8))) != 0;
        }

        内存.写入线圈项(地址, 位列表);

        return new byte[] { 0x0F, 协议数据[1], 协议数据[2], 协议数据[3], 协议数据[4] };
    }

    /// <summary>写多个寄存器（FC 10）。</summary>
    private static byte[] 写多个寄存器(byte[] 协议数据, 虚拟PLC内存 内存)
    {
        var (地址, 数量, 字节数, 起始) = 解析写多个头(协议数据, 最大写寄存器);

        if (字节数 != 数量 * 2 || 协议数据.Length != 起始 + 字节数)
        {
            throw new Modbus协议异常(0x03, "写多寄存器的字节数与数量不一致。");
        }

        校验地址范围(地址, 数量);

        var 寄存器列表 = new ushort[数量];
        for (var 条目 = 0; 条目 < 数量; 条目++)
        {
            寄存器列表[条目] = BinaryPrimitives.ReadUInt16BigEndian(协议数据.AsSpan(起始 + 条目 * 2, 2));
        }

        内存.写入保持寄存器项(地址, 寄存器列表);

        return new byte[] { 0x10, 协议数据[1], 协议数据[2], 协议数据[3], 协议数据[4] };
    }

    // ── 解析与组包 ──────────────────────────────────────────────────────

    private static (int 地址, int 数量) 解析起始与数量(byte[] 协议数据)
    {
        var 地址 = BinaryPrimitives.ReadUInt16BigEndian(协议数据.AsSpan(1, 2));
        var 数量 = BinaryPrimitives.ReadUInt16BigEndian(协议数据.AsSpan(3, 2));
        return (地址, 数量);
    }

    private static (int 地址, int 数量, int 字节数, int 起始) 解析写多个头(byte[] 协议数据, int 上限)
    {
        if (协议数据.Length < 6)
        {
            throw new Modbus协议异常(0x03, "写多个请求长度非法。");
        }

        var 地址 = BinaryPrimitives.ReadUInt16BigEndian(协议数据.AsSpan(1, 2));
        var 数量 = BinaryPrimitives.ReadUInt16BigEndian(协议数据.AsSpan(3, 2));
        var 字节数 = 协议数据[5];

        if (数量 < 1 || 数量 > 上限)
        {
            throw new Modbus协议异常(0x03, $"单次写数量必须在 1–{上限} 之间。");
        }

        return (地址, 数量, 字节数, 6);
    }

    private static void 校验地址范围(int 地址, int 数量)
    {
        if (地址 < 0 || 数量 < 1 || 地址 + 数量 > 软元件容量)
        {
            throw new Modbus协议异常(0x02, $"地址越界：起始 {地址}、数量 {数量}（虚拟软元件区共 {软元件容量} 点）。");
        }
    }

    private static byte[] 异常响应(byte 功能码, byte 异常码) => new byte[] { (byte)(功能码 | 0x80), 异常码 };

    /// <summary>按 MBAP 规范组包：事务号与单元号原样回带，协议号恒为 0。</summary>
    private static byte[] 组包响应(ReadOnlySpan<byte> 报文头, byte[] 协议数据)
    {
        var 响应 = new byte[报文头长度 + 协议数据.Length];

        报文头[..2].CopyTo(响应);                                  // 事务号
        响应[2] = 0;                                               // 协议号高字节
        响应[3] = 0;                                               // 协议号低字节
        BinaryPrimitives.WriteUInt16BigEndian(响应.AsSpan(4, 2), (ushort)(协议数据.Length + 1)); // 长度 = 单元号 + PDU
        响应[6] = 报文头[6];                                       // 单元号
        协议数据.CopyTo(响应.AsSpan(报文头长度));

        return 响应;
    }

    /// <summary>协议级拒绝（映射为 Modbus 异常响应）。</summary>
    private sealed class Modbus协议异常(byte 代码值, string 消息) : Exception(消息)
    {
        /// <summary>Modbus 异常码：0x01 非法功能 / 0x02 非法数据地址 / 0x03 非法数据值。</summary>
        public byte 异常码 { get; } = 代码值;
    }
}
