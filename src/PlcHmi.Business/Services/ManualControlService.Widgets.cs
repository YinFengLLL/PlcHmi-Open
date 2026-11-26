using Microsoft.Extensions.Logging;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// <see cref="ManualWidgetService"/> 的「手动页元件（按地址操作）」部分（partial）。
///
/// <para>从主文件拆出：主文件已接近规模门禁，而这三件事（写线圈 / 读线圈 / 脉冲线圈）
/// 与"按轴点动"是两条独立链路——前者按配置里的元件地址工作，后者按轴标签名拼装。</para>
/// </summary>
public sealed partial class ManualWidgetService
{
    // ── 手动页元件（按地址操作） ────────────────────────────────────────

    /// <inheritdoc />
    public async Task WriteCoilAsync(
        string addressValue, bool Value, CancellationToken cancellationToken = default)
    {
        _permission.WantRequire(Permission.ViewAllPage);
        EnsureEnabled();

        var ModbusAddress = ParseDeviceContinue(addressValue);

        await _client.WriteSingleCoilAsync(ModbusAddress, Value, cancellationToken).ConfigureAwait(false);

        _log.LogInformation("手动元件 {Address} 写入 {Value}", addressValue, Value);
    }

    /// <inheritdoc />
    public async Task<bool> ReadCoilAsync(string addressValue, CancellationToken cancellationToken = default)
    {
        _permission.WantRequire(Permission.ViewAllPage);

        // 只读操作不受手动使能限制：使能关着也该能看到线圈的真实状态
        var ModbusAddress = ParseDeviceContinue(addressValue);

        var coilList = await _client.ReadCoilAsync(ModbusAddress, 1, cancellationToken).ConfigureAwait(false);

        return coilList.Length > 0 && coilList[0];
    }

    /// <inheritdoc />
    public async Task PulseCoilAsync(
        string addressValue, TimeSpan width, CancellationToken cancellationToken = default)
    {
        _permission.WantRequire(Permission.ViewAllPage);
        EnsureEnabled();

        var ModbusAddress = ParseDeviceContinue(addressValue);
        var pulse = width <= TimeSpan.Zero ? defaultPulseWidth : width;

        // 复位是无条件安全出口：即使等待期间被取消（页面切换/断开），
        // 线圈也绝不能停在置位态——否则气缸/命令会卡在动作位。
        // 与 TagWriter.异步脉冲 同款 try/finally 结构，复位写不携带取消令牌。
        try
        {
            await _client.WriteSingleCoilAsync(ModbusAddress, true, cancellationToken).ConfigureAwait(false);
            await Task.Delay(pulse, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await _client.WriteSingleCoilAsync(ModbusAddress, false, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // 复位失败必须显式告警：此时线圈可能停在置位态，需要现场介入
                _log.LogError(exception, "手动元件 {Address} 脉冲复位失败，线圈可能停在置位态", addressValue);
            }
        }

        _log.LogInformation("手动元件 {Address} 脉冲 {Width}ms", addressValue, pulse.TotalMilliseconds);
    }

    /// <summary>
    /// 解析元件地址，并要求必须是 M 区。
    /// <para>这是"不直接控制 Y 点"这条约束的最后一道防线——即使配置文件被手工改错，
    /// 这里也会拒绝执行。</para>
    /// </summary>
    private int ParseDeviceContinue(string addressValue)
    {
        if (!_client.IsConnected)
        {
            throw new InvalidOperationException("PLC 未连接，无法执行手动动作。");
        }

        if (!_parser.TryParse(addressValue, out var PlcAddress) || PlcAddress is null)
        {
            throw new InvalidOperationException($"「{addressValue}」不是有效的软元件地址。");
        }

        if (PlcAddress.ElementItem != SoftElement.M)
        {
            throw new InvalidOperationException(
                $"「{addressValue}」必须是 M 区地址（当前解析为 {PlcAddress.ElementItem} 区）。"
                + "上位机只允许操作内部继电器，由 PLC 程序决定何时驱动 Y 输出。");
        }

        return PlcAddress.ModbusAddress;
    }
}
