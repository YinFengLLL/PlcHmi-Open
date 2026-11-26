using Microsoft.Extensions.Logging;
using PlcHmi.Communication.Modbus;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Exceptions;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// 点位写入器。把“按名称写值”统一转换为地址解析 + 编码 + Modbus 写操作。
/// <para>所有写命令都应经过这里，以便统一做权限校验、范围校验与串行化。</para>
/// </summary>
public sealed class TagWriter
{
    private readonly IModbusClient _client;
    private readonly ITagRepository _tag;
    private readonly IAddressParser _parser;
    private readonly ILogger<TagWriter> _log;

    /// <summary>
    /// D 字内位「读-改-写」的进程内串行锁。
    /// <para>Modbus 没有写字内位的功能码，只能 读字 → 改位 → 写回；
    /// 两次上位机写命令并发交错会把彼此的位改回去，必须串行化。
    /// 注意这只能防上位机自身并发，与 PLC 程序并发写同一 D 字仍有竞态（见文档）。</para>
    /// </summary>
    private static readonly SemaphoreSlim wordBitWriteLock = new(1, 1);

    public TagWriter(
        IModbusClient Client,
        ITagRepository tagList,
        IAddressParser Parser,
        ILogger<TagWriter> Log)
    {
        _client = Client;
        _tag = tagList;
        _parser = Parser;
        _log = Log;
    }

    /// <summary>按点位名写入布尔值。</summary>
    public async Task WriteBooleanAsync(string TagFullName, bool Value, CancellationToken cancellationToken = default)
    {
        var (tagItem, addressValue) = ParseWritable(TagFullName);

        if (addressValue.IsIntraWordBit)
        {
            await WriteIntraWordBitCoreAsync(addressValue, Value, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ValidateCoilArea(addressValue, TagFullName);
            await _client.WriteSingleCoilAsync(addressValue.ModbusAddress, Value, cancellationToken).ConfigureAwait(false);
        }

        _log.LogDebug("写入 {Tag} = {Value}", tagItem.Name, Value);
    }

    /// <summary>
    /// 写线圈前的最后一道防线（R4）：上位机只允许置位 <b>M 区内部继电器</b>，
    /// Y 物理输出由 PLC 程序联锁决定。此前 <see cref="WriteCoilAddressAsync"/> 有 M 区校验，
    /// 而按点位名写入的路径没有——tags.json 被手工改错即可越权驱动物理输出。
    /// </summary>
    private static void ValidateCoilArea(PlcAddress addressValue, string TagFullName)
    {
        if (addressValue.ElementItem != SoftElement.M)
        {
            throw new InvalidOperationException(
                $"点位「{TagFullName}」解析为 {addressValue.ElementItem} 区，拒绝写入。"
                + "上位机只允许操作 M 区内部继电器，由 PLC 程序决定何时驱动 Y 输出。");
        }
    }

    /// <summary>
    /// 按<b>软元件地址</b>直接写线圈，不要求该地址预先在 <c>tags.json</c> 里定义。
    ///
    /// <para>给「配置驱动」的点位用：参数页的按钮表由管理员在界面上增删，
    /// 地址是运行时才知道的，不可能提前写进 tags.json。</para>
    ///
    /// <para><b>仍然强制 M 区</b>：这是"上位机不直接驱动 Y 输出"这条硬约束的最后一道防线，
    /// 即使配置文件被手工改错，也不会执行到真实输出点。</para>
    /// </summary>
    public async Task WriteCoilAddressAsync(
        string addressValue, bool Value, CancellationToken cancellationToken = default)
    {
        var resolved = _parser.Parse(addressValue);

        if (resolved.ElementItem != SoftElement.M)
        {
            throw new InvalidOperationException(
                $"「{addressValue}」必须是 M 区地址（当前解析为 {resolved.ElementItem} 区）。"
                + "上位机只允许操作内部继电器，由 PLC 程序决定何时驱动 Y 输出。");
        }

        await _client.WriteSingleCoilAsync(resolved.ModbusAddress, Value, cancellationToken).ConfigureAwait(false);
        _log.LogInformation("按地址写入线圈 {Address} = {Value}", addressValue, Value);
    }

    /// <summary>
    /// 按<b>软元件地址</b>直接写保持寄存器（16 位），不要求该地址预先在 <c>tags.json</c> 里定义。
    ///
    /// <para>给「配置驱动」的点位用：延时表的地址由管理员在界面上配置，运行时才知道。</para>
    ///
    /// <para><b>强制 D 区</b>：延时值属于 PLC 参数字，写错区（如线圈）没有意义且危险。</para>
    /// </summary>
    public async Task WriteRegisterAddressAsync(
        string addressValue, int Value, CancellationToken cancellationToken = default)
    {
        var resolved = _parser.Parse(addressValue);

        if (resolved.ElementItem != SoftElement.D)
        {
            throw new InvalidOperationException(
                $"「{addressValue}」必须是 D 区地址（当前解析为 {resolved.ElementItem} 区）。");
        }

        var registerItem = checked((ushort)Value);   // 超出 0–65535 立刻抛，绝不静默截断
        await _client.WriteSingleRegisterAsync(resolved.ModbusAddress, registerItem, cancellationToken)
            .ConfigureAwait(false);
        _log.LogInformation("按地址写入寄存器 {Address} = {Value}", addressValue, registerItem);
    }

    /// <summary>
    /// 按<b>软元件地址</b>直接写 <b>32 位整数（DINT）</b>，占 2 个连续寄存器（字序 CDAB：低字在前），
    /// 不要求该地址预先在 <c>tags.json</c> 里定义。
    ///
    /// <para><b>2026-10-16 现场要求：</b>参数页延时表默认按 DINT 存毫秒——16 位整数毫秒
    /// 上限只有 65.535 S（65535 ms），实际工位动辄十几秒到几分钟的等待会直接溢出。</para>
    ///
    /// <para><b>字序走 <c>寄存器编解码器</c>（红线 R1）</b>：调用方不得自行拆高低字；
    /// 与轴速度 Float32 参数、机种快照用的是同一份实现。</para>
    ///
    /// <para><b>强制 D 区</b>：延时值属于 PLC 参数字，写错区（如线圈）没有意义且危险。</para>
    /// </summary>
    public async Task WriteRegisterAddress32Async(
        string addressValue, int Value, CancellationToken cancellationToken = default)
    {
        var resolved = _parser.Parse(addressValue);

        if (resolved.ElementItem != SoftElement.D)
        {
            throw new InvalidOperationException(
                $"「{addressValue}」必须是 D 区地址（当前解析为 {resolved.ElementItem} 区）。");
        }

        var registerList = RegisterCodec.Encode(TagDataType.Int32, Value, WordOrder.LittleEndian);

        await _client.WriteMultiRegisterAsync(resolved.ModbusAddress, registerList, cancellationToken)
            .ConfigureAwait(false);

        _log.LogInformation(
            "按地址写入 32 位寄存器 {Address} = {Value}（占 {Count} 字，低字在前）",
            addressValue, Value, registerList.Length);
    }

    /// <summary>
    /// 按<b>软元件地址</b>直接读 <b>32 位整数（DINT）</b>（占 2 个连续寄存器，字序 CDAB：低字在前），
    /// 不要求该地址预先在 <c>tags.json</c> 里定义。
    ///
    /// <para><b>2026-10-17 现场要求：</b>参数页延时区要<b>回读</b>——延时值由 PLC 程序也可能改写
    /// （切换机种、HMI 旁的操作面板），界面必须显示 PLC 当下真实值，而不是本地配置值。</para>
    ///
    /// <para><b>字序走 <c>寄存器编解码器</c>（红线 R1）</b>：与写入侧 <see cref="WriteRegisterAddress32Async"/>
    /// 用的是同一份实现，读回与写出严格互逆。</para>
    ///
    /// <para><b>强制 D 区</b>：与写入侧同口径。</para>
    /// </summary>
    public async Task<int> ReadRegisterAddress32Async(
        string addressValue, CancellationToken cancellationToken = default)
    {
        var resolved = _parser.Parse(addressValue);

        if (resolved.ElementItem != SoftElement.D)
        {
            throw new InvalidOperationException(
                $"「{addressValue}」必须是 D 区地址（当前解析为 {resolved.ElementItem} 区）。");
        }

        var registerList = await _client
            .ReadHoldingRegisterAsync(resolved.ModbusAddress, 2, cancellationToken)
            .ConfigureAwait(false);

        if (registerList.Length < 2)
        {
            throw new InvalidOperationException(
                $"从 {addressValue} 读取 2 个字时 PLC 只返回 {registerList.Length} 个，已中止。");
        }

        return RegisterCodec.Decode(TagDataType.Int32, registerList, WordOrder.LittleEndian) is int Value
            ? Value
            : throw new InvalidOperationException($"解码 {addressValue} 的 32 位整数失败。");
    }

    /// <summary>
    /// 按<b>软元件地址</b>直接读一个线圈（布尔），不要求该地址预先在 <c>tags.json</c> 里定义。
    /// <para>给参数页按钮区回读用：按钮状态由 PLC 程序或手动页都可能改写，界面要显示真实状态。</para>
    /// </summary>
    public async Task<bool> ReadCoilAddressAsync(string addressValue, CancellationToken cancellationToken = default)
    {
        var resolved = _parser.Parse(addressValue);

        if (resolved.ElementItem != SoftElement.M)
        {
            throw new InvalidOperationException(
                $"「{addressValue}」必须是 M 区线圈地址（当前解析为 {resolved.ElementItem} 区）。");
        }

        var coilList = await _client
            .ReadCoilAsync(resolved.ModbusAddress, 1, cancellationToken)
            .ConfigureAwait(false);

        return coilList.Length > 0 && coilList[0];
    }

    /// <summary>
    /// 写 D/R 区<b>字内位</b>（如 <c>D1210.2</c>）：读 1 字 → 改位 → 写回。
    ///
    /// <para>Modbus 没有写字内位的功能码（汇川位打包：BOOL 占 D 字的一位），
    /// 只能读-改-写。用进程内串行锁防止两条写命令并发交错互相覆盖；
    /// 与 PLC 程序并发写同一 D 字的竞态无法在上位机侧消除，
    /// 需 PLC 侧保证命令字只由上位机写（或走 OPC UA 变量节点直写位）。</para>
    /// </summary>
    private async Task WriteIntraWordBitCoreAsync(PlcAddress addressValue, bool Value, CancellationToken cancellationToken)
    {
        await wordBitWriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var registerList = await _client.ReadHoldingRegisterAsync(addressValue.ModbusAddress, 1, cancellationToken)
                .ConfigureAwait(false);

            if (registerList.Length < 1)
            {
                throw new InvalidOperationException(
                    $"写入字内位 {addressValue.Source} 失败：读回的寄存器为空。");
            }

            var wordValue = registerList[0];
            var bitMask = (ushort)(1 << addressValue.BitIndex);
            var newWordValue = (ushort)(Value ? wordValue | bitMask : wordValue & ~bitMask);

            // 刻意不做"值相同就跳过"的优化：点动方向命令里"停止"是安全出口，
            // 必须把目标状态真正写下去，不能因为一次读回恰好相符就省掉写。
            await _client.WriteSingleRegisterAsync(addressValue.ModbusAddress, newWordValue, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            wordBitWriteLock.Release();
        }

        _log.LogInformation("写字内位 {Address}（D 字第 {Bit} 位） = {Value}",
            addressValue.Source, addressValue.BitIndex, Value);
    }

    /// <summary>按点位名写入数值（含范围校验）。</summary>
    public async Task WriteNumericValueAsync(string TagFullName, double Value, CancellationToken cancellationToken = default)
    {
        var (tagItem, addressValue) = ParseWritable(TagFullName);
        ValidateRange(tagItem, Value);

        // 显示值还原为原始值
        var SourceValue = tagItem.Scale != 0 ? (Value - tagItem.Offset) / tagItem.Scale : Value;

        if (addressValue.Area == ModbusArea.Coil)
        {
            ValidateCoilArea(addressValue, TagFullName);
            await _client.WriteSingleCoilAsync(addressValue.ModbusAddress, SourceValue != 0, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var registerList = RegisterCodec.Encode(tagItem.DataType, SourceValue, tagItem.WordOrder);

            if (registerList.Length == 1)
            {
                await _client.WriteSingleRegisterAsync(addressValue.ModbusAddress, registerList[0], cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await _client.WriteMultiRegisterAsync(addressValue.ModbusAddress, registerList, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        _log.LogDebug("写入 {Tag} = {Value}{Unit}", tagItem.Name, Value, tagItem.Unit ?? string.Empty);
    }

    /// <summary>短脉冲写命令：置位后延时复位，用于启动/停止这类按钮式命令。</summary>
    public async Task PulseAsync(string TagFullName, TimeSpan whenLong, CancellationToken cancellationToken = default)
    {
        await WriteBooleanAsync(TagFullName, true, cancellationToken).ConfigureAwait(false);

        try
        {
            await Task.Delay(whenLong, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // 即使取消也要复位，避免命令点一直保持
            await WriteBooleanAsync(TagFullName, false, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private (TagDefinition Tag, PlcAddress Address) ParseWritable(string TagFullName)
    {
        var tagItem = _tag.Find(TagFullName)
                  ?? throw new InvalidOperationException($"点位 \"{TagFullName}\" 未在 tags.json 中定义。");

        if (!tagItem.Writable)
        {
            throw new InvalidOperationException($"点位 \"{TagFullName}\" 为只读，不允许写入。");
        }

        var addressValue = _parser.Parse(tagItem.Address);
        return (tagItem, addressValue);
    }

    private static void ValidateRange(TagDefinition tagItem, double Value)
    {
        if (tagItem.Min.HasValue && Value < tagItem.Min.Value)
        {
            throw new TagValueOutOfRangeException(tagItem.Name, Value, tagItem.Min, tagItem.Max);
        }

        if (tagItem.Max.HasValue && Value > tagItem.Max.Value)
        {
            throw new TagValueOutOfRangeException(tagItem.Name, Value, tagItem.Min, tagItem.Max);
        }
    }
}
