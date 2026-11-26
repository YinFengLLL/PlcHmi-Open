using System.Buffers.Binary;
using Microsoft.Extensions.Logging;

namespace PlcHmi.Communication.Simulation;

/// <summary>
/// 虚拟 PLC 从站的 Modbus TCP 协议实现：把一条请求 PDU 翻译成对
/// <see cref="VirtualPlcMemory"/> 的读写，并组出响应帧。
///
/// <para><b>为什么单独一个文件：</b>协议解析是纯字节级的活（位打包、字节序、异常码、地址边界），
/// 与"监听端口、管理客户端连接"是两类完全不同的关注点。分开后各自都好读、也好单独验证；
/// 合成一个 590 行的类只会让"到底哪段是协议、哪段是网络"变得含糊。</para>
///
/// <para><b>协议范围：</b>FC 01/02/03/04（读位 / 读寄存器）与 FC 05/06/0F/10（写单线圈 / 写单寄存器 /
/// 写多线圈 / 写多寄存器），覆盖调试工具的常用功能。寄存器按原样 16 位大端转发，
/// <b>不做 32 位字序拆分</b>——字序是客户端侧 <c>寄存器编解码器</c> 的职责，从站不越界。</para>
/// </summary>
internal sealed class ModbusSlaveProtocol
{
    /// <summary>Modbus 标准单次读线圈上限。</summary>
    private const int maxReadCoil = 2000;

    /// <summary>Modbus 标准单次读寄存器上限。</summary>
    private const int maxReadRegister = 125;

    /// <summary>Modbus 标准单次写线圈上限。</summary>
    private const int maxWriteCoil = 1968;

    /// <summary>Modbus 标准单次写寄存器上限。</summary>
    private const int maxWriteRegister = 123;

    /// <summary>MBAP 报文头长度：事务号 2 + 协议号 2 + 长度 2 + 单元号 1。</summary>
    public const int messageHeadLength = 7;

    /// <summary>标准 Modbus TCP 帧长上限。</summary>
    public const int maxFrameLong = 260;

    /// <summary>虚拟软元件区容量（与 <see cref="VirtualPlcMemory"/> 的数组长度一致）。</summary>
    private const int softElementCapacity = 65536;

    private readonly ILogger _log;

    public ModbusSlaveProtocol(ILogger Log)
    {
        _log = Log;
    }

    /// <summary>处理一条请求，返回完整响应帧（MBAP + PDU）。</summary>
    public byte[] ProcessRequest(ReadOnlySpan<byte> messageHead, byte[] protocolData, VirtualPlcMemory memory)
    {
        var functionCode = protocolData[0];

        try
        {
            var response = functionCode switch
            {
                0x01 => ReadBit(functionCode, protocolData, areaBetween => memory.ReadCoil(areaBetween.Address, areaBetween.Count), maxReadCoil),
                0x02 => ReadBit(functionCode, protocolData, areaBetween => memory.ReadDiscreteInput(areaBetween.Address, areaBetween.Count), maxReadCoil),
                0x03 => ReadRegister(functionCode, protocolData, areaBetween => memory.ReadHoldingRegister(areaBetween.Address, areaBetween.Count), maxReadRegister),
                0x04 => ReadRegister(functionCode, protocolData, areaBetween => memory.ReadInputRegister(areaBetween.Address, areaBetween.Count), maxReadRegister),
                0x05 => WriteSingleCoil(protocolData, memory),
                0x06 => WriteSingleRegister(protocolData, memory),
                0x0F => WriteMultiCoil(protocolData, memory),
                0x10 => WriteMultiRegister(protocolData, memory),
                _ => ExceptionResponse(functionCode, 0x01),
            };

            return GroupIncludeResponse(messageHead, response);
        }
        catch (ModbusProtocolException exception)
        {
            _log.LogDebug("Modbus 从站请求被拒（功能码 0x{Function:X2}，异常码 0x{Code:X2}）：{Message}",
                functionCode, exception.ExceptionCode, exception.Message);

            return GroupIncludeResponse(messageHead, ExceptionResponse(functionCode, exception.ExceptionCode));
        }
        catch (Exception exception)
        {
            // 虚拟内存越界等未预期错误：回"非法数据地址"，并留下痕迹
            _log.LogWarning(exception, "处理 Modbus 从站请求时出现未预期异常（功能码 0x{Function:X2}）", functionCode);
            return GroupIncludeResponse(messageHead, ExceptionResponse(functionCode, 0x02));
        }
    }

    // ── 读 ──────────────────────────────────────────────────────────────

    /// <summary>读线圈 / 离散输入（FC 01 / 02）。</summary>
    private static byte[] ReadBit(
        byte functionCode,
        byte[] protocolData,
        Func<(int Address, int Count), bool[]> Read,
        int upperLimit)
    {
        if (protocolData.Length != 5)
        {
            throw new ModbusProtocolException(0x03, "读位请求长度非法。");
        }

        var (Address, Count) = ParseStartAndCount(protocolData);

        if (Count < 1 || Count > upperLimit)
        {
            throw new ModbusProtocolException(0x03, $"单次读位数量必须在 1–{upperLimit} 之间。");
        }

        ValidateAddressRange(Address, Count);

        var bitList = Read((Address, Count));
        var byteCount = (Count + 7) / 8;
        var response = new byte[2 + byteCount];
        response[0] = functionCode;
        response[1] = (byte)byteCount;

        for (var Entry = 0; Entry < Count; Entry++)
        {
            if (bitList[Entry])
            {
                // Modbus 位打包：每字节低位在前
                response[2 + Entry / 8] |= (byte)(1 << (Entry % 8));
            }
        }

        return response;
    }

    /// <summary>读保持 / 输入寄存器（FC 03 / 04），寄存器按大端 16 位输出。</summary>
    private static byte[] ReadRegister(
        byte functionCode,
        byte[] protocolData,
        Func<(int Address, int Count), ushort[]> Read,
        int upperLimit)
    {
        if (protocolData.Length != 5)
        {
            throw new ModbusProtocolException(0x03, "读寄存器请求长度非法。");
        }

        var (Address, Count) = ParseStartAndCount(protocolData);

        if (Count < 1 || Count > upperLimit)
        {
            throw new ModbusProtocolException(0x03, $"单次读寄存器数量必须在 1–{upperLimit} 之间。");
        }

        ValidateAddressRange(Address, Count);

        var registerList = Read((Address, Count));
        var response = new byte[2 + Count * 2];
        response[0] = functionCode;
        response[1] = (byte)(Count * 2);

        for (var Entry = 0; Entry < Count; Entry++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2 + Entry * 2, 2), registerList[Entry]);
        }

        return response;
    }

    // ── 写 ──────────────────────────────────────────────────────────────

    /// <summary>写单个线圈（FC 05），响应原样回显。</summary>
    private static byte[] WriteSingleCoil(byte[] protocolData, VirtualPlcMemory memory)
    {
        if (protocolData.Length != 5)
        {
            throw new ModbusProtocolException(0x03, "写单线圈请求长度非法。");
        }

        var Address = BinaryPrimitives.ReadUInt16BigEndian(protocolData.AsSpan(1, 2));
        var Value = BinaryPrimitives.ReadUInt16BigEndian(protocolData.AsSpan(3, 2));

        if (Value != 0x0000 && Value != 0xFF00)
        {
            throw new ModbusProtocolException(0x03, $"写单线圈的值必须为 0x0000 或 0xFF00，收到 0x{Value:X4}。");
        }

        ValidateAddressRange(Address, 1);
        memory.WriteCoil(Address, Value == 0xFF00);

        return new byte[] { 0x05, protocolData[1], protocolData[2], protocolData[3], protocolData[4] };
    }

    /// <summary>写单个寄存器（FC 06），响应原样回显。</summary>
    private static byte[] WriteSingleRegister(byte[] protocolData, VirtualPlcMemory memory)
    {
        if (protocolData.Length != 5)
        {
            throw new ModbusProtocolException(0x03, "写单寄存器请求长度非法。");
        }

        var Address = BinaryPrimitives.ReadUInt16BigEndian(protocolData.AsSpan(1, 2));
        var Value = BinaryPrimitives.ReadUInt16BigEndian(protocolData.AsSpan(3, 2));

        ValidateAddressRange(Address, 1);
        memory.WriteHoldingRegister(Address, Value);

        return new byte[] { 0x06, protocolData[1], protocolData[2], protocolData[3], protocolData[4] };
    }

    /// <summary>写多个线圈（FC 0F）。</summary>
    private static byte[] WriteMultiCoil(byte[] protocolData, VirtualPlcMemory memory)
    {
        var (Address, Count, byteCount, StartAt) = ParseWriteMultiHead(protocolData, maxWriteCoil);

        if (protocolData.Length != StartAt + byteCount)
        {
            throw new ModbusProtocolException(0x03, "写多线圈的字节数与报文长度不一致。");
        }

        ValidateAddressRange(Address, Count);

        var bitList = new bool[Count];
        for (var Entry = 0; Entry < Count; Entry++)
        {
            bitList[Entry] = (protocolData[StartAt + Entry / 8] & (1 << (Entry % 8))) != 0;
        }

        memory.WriteCoilItem(Address, bitList);

        return new byte[] { 0x0F, protocolData[1], protocolData[2], protocolData[3], protocolData[4] };
    }

    /// <summary>写多个寄存器（FC 10）。</summary>
    private static byte[] WriteMultiRegister(byte[] protocolData, VirtualPlcMemory memory)
    {
        var (Address, Count, byteCount, StartAt) = ParseWriteMultiHead(protocolData, maxWriteRegister);

        if (byteCount != Count * 2 || protocolData.Length != StartAt + byteCount)
        {
            throw new ModbusProtocolException(0x03, "写多寄存器的字节数与数量不一致。");
        }

        ValidateAddressRange(Address, Count);

        var registerList = new ushort[Count];
        for (var Entry = 0; Entry < Count; Entry++)
        {
            registerList[Entry] = BinaryPrimitives.ReadUInt16BigEndian(protocolData.AsSpan(StartAt + Entry * 2, 2));
        }

        memory.WriteHoldingRegisterItem(Address, registerList);

        return new byte[] { 0x10, protocolData[1], protocolData[2], protocolData[3], protocolData[4] };
    }

    // ── 解析与组包 ──────────────────────────────────────────────────────

    private static (int Address, int Count) ParseStartAndCount(byte[] protocolData)
    {
        var Address = BinaryPrimitives.ReadUInt16BigEndian(protocolData.AsSpan(1, 2));
        var Count = BinaryPrimitives.ReadUInt16BigEndian(protocolData.AsSpan(3, 2));
        return (Address, Count);
    }

    private static (int Address, int Count, int byteCount, int StartAt) ParseWriteMultiHead(byte[] protocolData, int upperLimit)
    {
        if (protocolData.Length < 6)
        {
            throw new ModbusProtocolException(0x03, "写多个请求长度非法。");
        }

        var Address = BinaryPrimitives.ReadUInt16BigEndian(protocolData.AsSpan(1, 2));
        var Count = BinaryPrimitives.ReadUInt16BigEndian(protocolData.AsSpan(3, 2));
        var byteCount = protocolData[5];

        if (Count < 1 || Count > upperLimit)
        {
            throw new ModbusProtocolException(0x03, $"单次写数量必须在 1–{upperLimit} 之间。");
        }

        return (Address, Count, byteCount, 6);
    }

    private static void ValidateAddressRange(int Address, int Count)
    {
        if (Address < 0 || Count < 1 || Address + Count > softElementCapacity)
        {
            throw new ModbusProtocolException(0x02, $"地址越界：起始 {Address}、数量 {Count}（虚拟软元件区共 {softElementCapacity} 点）。");
        }
    }

    private static byte[] ExceptionResponse(byte functionCode, byte ExceptionCode) => new byte[] { (byte)(functionCode | 0x80), ExceptionCode };

    /// <summary>按 MBAP 规范组包：事务号与单元号原样回带，协议号恒为 0。</summary>
    private static byte[] GroupIncludeResponse(ReadOnlySpan<byte> messageHead, byte[] protocolData)
    {
        var response = new byte[messageHeadLength + protocolData.Length];

        messageHead[..2].CopyTo(response);                                  // 事务号
        response[2] = 0;                                               // 协议号高字节
        response[3] = 0;                                               // 协议号低字节
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(4, 2), (ushort)(protocolData.Length + 1)); // 长度 = 单元号 + PDU
        response[6] = messageHead[6];                                       // 单元号
        protocolData.CopyTo(response.AsSpan(messageHeadLength));

        return response;
    }

    /// <summary>协议级拒绝（映射为 Modbus 异常响应）。</summary>
    private sealed class ModbusProtocolException(byte CodeRawValue, string Message) : Exception(Message)
    {
        /// <summary>Modbus 异常码：0x01 非法功能 / 0x02 非法数据地址 / 0x03 非法数据值。</summary>
        public byte ExceptionCode { get; } = CodeRawValue;
    }
}
