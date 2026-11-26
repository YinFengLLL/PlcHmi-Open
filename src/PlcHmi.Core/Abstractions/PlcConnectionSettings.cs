using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Core.Abstractions;

/// <summary>
/// PLC 连接参数编辑模型。
///
/// <para><b>刻意不包含的字段</b>（防"改了不起作用"的死配置陷阱，2026-09-12 已清理过一批同类）：
/// <list type="bullet">
/// <item><c>UseSimulator</c> —— 运行模式由 <see cref="IRunModeManager"/> 在运行期管理，
/// 不通过本模型编辑，也不写回配置文件；</item>
/// <item><c>Series</c> —— 机型决定地址解析体系（<c>InovanceAddressResolver</c>），
/// 运行期更换属高风险操作，不在界面开放。</item>
/// </list></para>
/// </summary>
public sealed class PlcConnectionEditModel
{
    /// <summary>PLC 的 IP 地址或主机名。</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Modbus TCP 端口（1–65535，标准 502）。</summary>
    public int Port { get; set; }

    /// <summary>从站号（1–247）。</summary>
    public int UnitId { get; set; }

    /// <summary>连接超时（毫秒）。</summary>
    public int ConnectTimeoutMilliseconds { get; set; }

    /// <summary>读写超时（毫秒）。</summary>
    public int ReadTimeoutMilliseconds { get; set; }

    /// <summary>心跳间隔（毫秒）。</summary>
    public int HeartbeatIntervalMilliseconds { get; set; }

    /// <summary>断线重连初始退避（毫秒）。</summary>
    public int ReconnectInitialDelayMilliseconds { get; set; }

    /// <summary>断线重连退避上限（毫秒）。</summary>
    public int ReconnectMaxDelayMilliseconds { get; set; }

    /// <summary>单次请求最大寄存器数（1–123，汇川读上限 123）。</summary>
    public int PerRequestMaxRegisterNumber { get; set; }

    /// <summary>是否自动重连。</summary>
    public bool AutoReconnect { get; set; }

    /// <summary>
    /// 通讯协议（Modbus TCP / OPC UA）。
    /// <para><b>切换需重启程序</b>：客户端实现由 DI 装配定型，保存写入配置文件，
    /// 重启后按新协议装配。</para>
    /// </summary>
    public CommunicationProtocol CommunicationProtocol { get; set; }

    /// <summary>OPC UA 服务端端口（仅 通讯协议 = OPCUA 时使用，默认 4840）。</summary>
    public int OpcUaPort { get; set; }

    /// <summary>OPC UA 命名空间索引（NodeId 的 ns= 部分，默认 2）。</summary>
    public int OpcUaNamespaceIndex { get; set; }

    /// <summary>
    /// 模拟 PLC 从站监听端口（仅 Modbus 模式、模拟运行模式下生效），默认 5020。
    /// <para>外部 Modbus 调试工具连它即可对上位机的虚拟 PLC 做离线读写。</para>
    /// </summary>
    public int SimulationSlavePort { get; set; }

    /// <summary>
    /// 模拟 PLC 从站监听地址（仅 Modbus 模式、模拟运行模式下生效），默认 127.0.0.1。
    /// <para>只在本机调试时用回环地址；需要跨机调试才改成 0.0.0.0 或网卡地址。</para>
    /// </summary>
    public string SimulationSlaveListenAddress { get; set; } = string.Empty;
}

/// <summary>
/// PLC 连接参数管理（仅管理员，<see cref="Permission.EditPlcConnect"/>）。
///
/// <para><b>生效机制</b>：通信客户端每次连接时都从共享的 <see cref="PlcConnectionOptions"/>
/// 现读 IP / 端口，因此保存 = 校验 → 更新共享参数 → 原子落盘 appsettings.json →
/// 重启连接管理器（断开并用新参数重连），<b>无需重启程序</b>。</para>
///
/// <para><b>校验一律在服务层</b>：界面只负责隐藏入口，绕过界面直接调服务也不该写出坏配置。</para>
/// </summary>
public interface IPlcConnectionSettingsService
{
    /// <summary>参数已被保存并生效（界面据此刷新展示）。</summary>
    event EventHandler? saved;

    /// <summary>从当前生效参数生成编辑副本（界面编辑用，避免直接绑到共享单例上）。</summary>
    PlcConnectionEditModel CreateEditModel();

    /// <summary>校验一组待保存的连接参数，返回全部错误（不修改任何状态）。</summary>
    IReadOnlyList<string> Validate(PlcConnectionEditModel candidate);

    /// <summary>
    /// 保存连接参数：校验 → 更新共享参数 → 原子落盘 → 重启连接。
    /// <para>校验不通过时抛 <see cref="InvalidOperationException"/>，不会写入半个状态；
    /// 保存期间当前连接会中断数秒（重连循环自动恢复）。</para>
    /// </summary>
    Task SaveAsync(PlcConnectionEditModel candidate, CancellationToken cancellationToken = default);
}
