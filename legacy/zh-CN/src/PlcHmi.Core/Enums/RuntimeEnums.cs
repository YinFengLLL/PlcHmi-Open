namespace 上位机.核心.枚举;

/// <summary>设备运行状态。</summary>
/// <remarks>
/// 与 PLC 状态字（默认寄存器 D6，运行监控页可改）的对应关系见
/// <see cref="业务.服务.设备服务.映射状态字"/>：<c>0=空闲中 1=运行中 2=待机中 3=复位中 4=报警中</c>。
/// 枚举编号与状态字编号<b>刻意不一致</b>（未知=0 是"尚未拿到有效数据"的哨兵），禁止直接强转。
/// </remarks>
public enum 设备状态
{
    /// <summary>未知（尚未与 PLC 建立有效通信）</summary>
    未知 = 0,
    /// <summary>空闲中（状态字 0：上电就绪，未运行）</summary>
    空闲 = 1,
    /// <summary>运行中（状态字 1：自动循环运行）</summary>
    运行中 = 2,
    /// <summary>待机中（状态字 2：暂停/等待，满足条件可继续）</summary>
    待机中 = 3,
    /// <summary>复位中（状态字 3：正在执行复位/回原点流程）</summary>
    复位中 = 4,
    /// <summary>报警中（状态字 4：有报警，运行被中止）</summary>
    报警中 = 5,
}

/// <summary>PLC 连接状态。</summary>
public enum 连接状态
{
    已断开 = 0,
    连接中 = 1,
    已连接 = 2,
    重连中 = 3,
    故障 = 4,
}

/// <summary>报警等级。</summary>
public enum 报警等级
{
    /// <summary>提示（蓝）</summary>
    信息 = 0,
    /// <summary>警告（橙）</summary>
    警告 = 1,
    /// <summary>紧急（红）</summary>
    紧急 = 2,
}
