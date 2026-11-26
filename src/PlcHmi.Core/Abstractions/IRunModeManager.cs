using PlcHmi.Core.Enums;

namespace PlcHmi.Core.Abstractions;

/// <summary>
/// 运行模式管理器（仅 Modbus 模式注册）。
///
/// <para><b>职责：</b>在运行期把上位机在「真实 PLC / 模拟 PLC」之间切换，并管理模拟模式下
/// 供外部工具离线调试的 Modbus 从站。切换过程<b>无需重启程序</b>。</para>
///
/// <para><b>不跨重启记忆：</b>这是刻意的安全约定——生产机台重启后必须回到真实 PLC，
/// 绝不能因为上次调试时切到了模拟模式，就悄悄停在一个"看着在跑、其实没连设备"的状态。
/// 因此模拟开关不写入配置文件（见 <see cref="RunModeStrategy"/>）。</para>
///
/// <para><b>与通讯协议的关系：</b>运行模式是 Modbus 模式下的能力；OPC UA 模式下
/// <see cref="CanAtRuntimeSwitch"/> 为 <c>false</c>，协议切换仍走"保存 + 重启生效"。</para>
/// </summary>
public interface IRunModeManager
{
    /// <summary>当前运行模式。</summary>
    RunMode CurrentMode { get; }

    /// <summary>当前协议是否允许运行期切换（仅 Modbus TCP 为 <c>true</c>）。</summary>
    bool CanAtRuntimeSwitch { get; }

    /// <summary>是否正在执行切换（界面据此禁用按钮，避免重复触发）。</summary>
    bool IsSwitching { get; }

    /// <summary>模拟从站是否正在监听。</summary>
    bool SimulationSlaveIsRun { get; }

    /// <summary>模拟从站监听端点文本（如 <c>127.0.0.1:5020</c>）；未运行时为配置值。</summary>
    string SimulationSlaveEndpointText { get; }

    /// <summary>模拟从站状态说明（给管理员看的可读文案）。</summary>
    string SimulationSlaveStateText { get; }

    /// <summary>运行模式已变更（含从站启停结果）。界面据此刷新展示。</summary>
    event EventHandler? modeChanged;

    /// <summary>
    /// 切换到目标运行模式。幂等：目标与当前一致时直接返回。
    ///
    /// <para>流程：校验权限与可切换性 → 需要时先拉起模拟从站（失败即中止、状态不变）→
    /// 停止连接 → 替换客户端实现 → 重启连接 → 需要时停止从站 → 广播事件。</para>
    ///
    /// <para>无权限、目标非法或从站监听失败时抛 <see cref="InvalidOperationException"/>，
    /// <b>不会写入半个状态</b>。</para>
    /// </summary>
    Task SwitchAsync(RunMode target, CancellationToken cancellationToken = default);
}
