namespace PlcHmi.Core.Enums;

/// <summary>
/// 运行模式。决定通信客户端连接的是<b>真实 PLC</b>还是<b>内置虚拟 PLC</b>。
///
/// <para><b>与 <see cref="CommunicationProtocol"/> 的区别：</b>协议决定"用哪种语言与 PLC 说话"（DI 装配定型，改需重启）；
/// 运行模式决定"对谁说话"（Modbus 模式下可在运行期切换，无需重启）。</para>
/// </summary>
public enum RunMode
{
    /// <summary>真实 PLC。程序启动时的缺省模式。</summary>
    RealPlc = 0,

    /// <summary>模拟 PLC（内置虚拟设备，供无真机时的离线调试）。</summary>
    SimulationPlc = 1,
}

/// <summary>
/// 运行模式的启动策略。
///
/// <para><b>为什么单独抽一个策略类：</b>"启动缺省模式"与"是否允许运行期切换"是<b>产品约定</b>，
/// 不是某个服务内部的实现细节。把它集中在这里，App 启动流程与服务层都引用同一处真相，
/// 避免"界面能切、服务不认"这类两处各写一半的漂移。</para>
///
/// <para><b>两条约定：</b></para>
/// <list type="number">
///   <item><b>仅 Modbus 可运行期切换</b>：OPC UA 的客户端实现由 DI 装配定型，沿用"保存 + 重启生效"的既有机制。</item>
///   <item><b>切换不跨重启记忆</b>：每次启动一律进入真实 PLC —— 生产机台重启后若悄悄停在模拟模式，
///   界面看着一切正常、产线其实不受控，属于必须杜绝的隐性风险。因此模拟开关不写回配置文件。</item>
/// </list>
/// </summary>
public static class RunModeStrategy
{
    /// <summary>该协议是否允许在运行期切换运行模式（仅 Modbus TCP）。</summary>
    public static bool RuntimeSwitchable(CommunicationProtocol Protocol) => Protocol == CommunicationProtocol.ModbusTCP;

    /// <summary>
    /// 启动缺省模式。可运行期切换的协议一律从真实 PLC 起步（不记忆上次选择）；
    /// 其余协议沿用配置中的模拟开关。
    /// </summary>
    public static RunMode GetStartMode(CommunicationProtocol Protocol, bool UseSimulator)
    {
        if (RuntimeSwitchable(Protocol))
        {
            return RunMode.RealPlc;
        }

        return UseSimulator ? RunMode.SimulationPlc : RunMode.RealPlc;
    }

    /// <summary>
    /// 写回配置时应持久化的模拟开关值。
    ///
    /// <para>可运行期切换的协议<b>恒为 <c>false</c></b>：模拟开关是运行期状态，不进配置文件，
    /// 下次启动才不会"记住"上次的模拟选择。</para>
    /// </summary>
    public static bool PersistSimulationSwitch(CommunicationProtocol Protocol, bool runtimeUseSimulator)
        => RuntimeSwitchable(Protocol) ? false : runtimeUseSimulator;
}
