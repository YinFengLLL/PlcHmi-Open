namespace PlcHmi.Core.Enums;

/// <summary>设备运行状态。</summary>
/// <remarks>
/// 与 PLC 状态字（默认寄存器 D6，运行监控页可改）的对应关系见
/// <see cref="Business.Service.DeviceService.MapStatusWord"/>：<c>0=空闲中 1=运行中 2=待机中 3=复位中 4=报警中</c>。
/// 枚举编号与状态字编号<b>刻意不一致</b>（未知=0 是"尚未拿到有效数据"的哨兵），禁止直接强转。
/// </remarks>
public enum DeviceState
{
    /// <summary>未知（尚未与 PLC 建立有效通信）</summary>
    Unknown = 0,
    /// <summary>空闲中（状态字 0：上电就绪，未运行）</summary>
    Idle = 1,
    /// <summary>运行中（状态字 1：自动循环运行）</summary>
    Running = 2,
    /// <summary>待机中（状态字 2：暂停/等待，满足条件可继续）</summary>
    Standby = 3,
    /// <summary>复位中（状态字 3：正在执行复位/回原点流程）</summary>
    Resetting = 4,
    /// <summary>报警中（状态字 4：有报警，运行被中止）</summary>
    Alarming = 5,
}

/// <summary>PLC 连接状态。</summary>
public enum ConnectionState
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    Reconnecting = 3,
    Fault = 4,
}

/// <summary>报警等级。</summary>
public enum AlarmLevel
{
    /// <summary>提示（蓝）</summary>
    Info = 0,
    /// <summary>警告（橙）</summary>
    Warning = 1,
    /// <summary>紧急（红）</summary>
    Critical = 2,
}
