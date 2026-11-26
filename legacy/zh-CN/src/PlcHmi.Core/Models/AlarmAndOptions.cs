using 上位机.核心.枚举;

namespace 上位机.核心.模型;

/// <summary>报警条目。</summary>
public sealed class 报警条目
{
    /// <summary>报警代码，如 <c>"E1024"</c>。</summary>
    public string 代码 { get; set; } = string.Empty;

    /// <summary>报警描述，如 <c>"压装气缸超时未到位"</c>。</summary>
    public string 消息 { get; set; } = string.Empty;

    /// <summary>报警等级。</summary>
    public 报警等级 等级 { get; set; } = 报警等级.警告;

    /// <summary>触发时间。</summary>
    public DateTime 已触发 { get; set; } = DateTime.Now;

    /// <summary>消除时间。为 <c>null</c> 表示仍在活动中。</summary>
    public DateTime? 已清除 { get; set; }

    /// <summary>是否已确认。</summary>
    public bool 是否已确认 { get; set; }

    /// <summary>是否仍处于活动状态。</summary>
    public bool 是否活动 => 已清除 is null;

    public 报警条目 克隆() => (报警条目)MemberwiseClone();
}

/// <summary>PLC 连接参数。对应 <c>config/appsettings.json</c> 的 <c>Plc</c> 节。</summary>
public sealed class PLC连接选项
{
    public const string 区段名称 = "Plc";

    /// <summary>PLC IP 地址。</summary>
    public string 主机 { get; set; } = "192.168.1.10";

    /// <summary>Modbus TCP 端口，标准为 502。</summary>
    public int 端口 { get; set; } = 502;

    /// <summary>
    /// 从站号。TCP 下由 IP 判定站号，<b>固定填 1</b> 即可，但必须是合法值。
    /// </summary>
    public byte 单元标识 { get; set; } = 1;

    /// <summary>机型系列，决定使用哪套地址体系。</summary>
    public PLC系列 系列 { get; set; } = PLC系列.小型PLC;

    /// <summary>连接超时（毫秒）。</summary>
    public int 连接超时毫秒 { get; set; } = 3000;

    /// <summary>读写超时（毫秒）。</summary>
    public int 读取超时毫秒 { get; set; } = 1000;

    /// <summary>心跳间隔（毫秒）。无专用心跳寄存器，用轻量读请求保活。</summary>
    public int 心跳间隔毫秒 { get; set; } = 5000;

    /// <summary>重连初始延迟（毫秒），按指数退避递增。</summary>
    public int 重连初始延迟毫秒 { get; set; } = 1000;

    /// <summary>重连最大延迟（毫秒）。</summary>
    public int 重连最大延迟毫秒 { get; set; } = 30000;

    /// <summary>
    /// 单次请求最大寄存器数。<b>汇川上限为读 123 / 写 121，工程上取 100 留余量。</b>
    /// </summary>
    public int 每次请求最大寄存器数 { get; set; } = 100;

    /// <summary>是否启用自动重连。</summary>
    public bool 自动重连 { get; set; } = true;

    /// <summary>
    /// 启动缺省是否使用模拟器（无 PLC 时便于开发调试）。
    ///
    /// <para><b>Modbus 模式下该字段不参与决策</b>：运行模式由管理员在界面运行期切换，
    /// 且每次启动一律进入真实 PLC（见 <see cref="运行模式策略"/>），模拟开关不写回本文件。
    /// OPC UA 模式仍沿用"读配置 + 重启生效"的既有机制。</para>
    /// </summary>
    public bool 使用模拟器 { get; set; }

    // ── 模拟模式离线调试从站（运行模式 = 模拟PLC 时生效）────────────────────

    /// <summary>
    /// 模拟 PLC 从站的监听端口。默认 5020（避开真实 PLC 常用的 502，便于同机并存调试）。
    /// </summary>
    public int 模拟从站端口 { get; set; } = 5020;

    /// <summary>
    /// 模拟 PLC 从站的监听地址。默认仅回环 <c>127.0.0.1</c>——离线调试工具通常与上位机同机；
    /// 需要跨机调试时由管理员显式改为 <c>0.0.0.0</c> 或本机网卡地址。
    /// </summary>
    public string 模拟从站监听地址 { get; set; } = "127.0.0.1";

    // ── OPC UA 专属参数（通讯协议 = OPCUA 时生效）────────────────────────

    /// <summary>
    /// 通讯协议。缺省（旧配置文件无此字段）回退 Modbus TCP，现场配置无需迁移。
    /// <para><b>属 DI 装配定型参数</b>——与 系列 / 使用模拟器 一样，运行期修改无效，
    /// 需重启程序生效。</para>
    /// </summary>
    public 通讯协议 通讯协议 { get; set; } = 通讯协议.ModbusTCP;

    /// <summary>OPC UA 服务端端口。汇川 H5U 内置服务端默认 4840。</summary>
    public int OPCUA端口 { get; set; } = 4840;

    /// <summary>OPC UA 命名空间索引（NodeIds 的 ns= 部分）。默认 2，与汇川软元件节点所在命名空间一致。</summary>
    public int OPCUA命名空间索引 { get; set; } = 2;

    /// <summary>
    /// NodeId 推导格式模板。占位符：<c>{命名空间}</c>、<c>{软元件}</c>、<c>{序号}</c>。
    /// 默认 <c>ns={命名空间};s={软元件}{序号}</c>，即软元件 D100 推导为 <c>ns=2;s=D100</c>。
    /// </summary>
    public string OPCUA节点格式 { get; set; } = "ns={命名空间};s={软元件}{序号}";

    /// <summary>
    /// 变量绑定点位的 NodeId 模板。占位符：<c>{命名空间}</c>、<c>{变量}</c>。
    /// 默认 <c>ns={命名空间};s={变量}</c>——变量绑定的点位直接以变量名做符号寻址。
    /// </summary>
    public string OPCUA变量格式 { get; set; } = "ns={命名空间};s={变量}";

    /// <summary>OPC UA 用户名。为空时使用匿名身份。</summary>
    public string OPCUA用户名 { get; set; } = string.Empty;

    /// <summary>OPC UA 密码。仅 用户名 非空时使用。</summary>
    public string OPCUA密码 { get; set; } = string.Empty;
}

/// <summary>采集参数。对应 <c>config/appsettings.json</c> 的 <c>Polling</c> 节。</summary>
public sealed class 轮询选项
{
    public const string 区段名称 = "Polling";

    /// <summary>快组周期（毫秒）。</summary>
    public int 快间隔毫秒 { get; set; } = 100;

    /// <summary>慢组周期（毫秒）。</summary>
    public int 慢间隔毫秒 { get; set; } = 500;

    /// <summary>事件组周期（毫秒）。</summary>
    public int 事件间隔毫秒 { get; set; } = 1000;
}
