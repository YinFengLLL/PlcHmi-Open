namespace 上位机.核心.模型;

/// <summary>扫码枪通讯方式。</summary>
public enum 扫码通讯模式
{
    /// <summary>串口（USB 转 COM）。</summary>
    串口 = 0,

    /// <summary>TCP/IP（上位机作为客户端连接扫码枪）。</summary>
    TcpIp = 1,
}

/// <summary>扫码工作模式。</summary>
public enum 扫码工作模式
{
    /// <summary>
    /// 手动型：不管触发寄存器，扫码枪自己扫到码就从串口/网口发上来，
    /// 上位机收到即视作扫码成功（写成功信号寄存器）。
    /// </summary>
    手动型 = 0,

    /// <summary>
    /// 触发型：PLC 把触发寄存器置 1 → 上位机写 0 应答 →（预留）给扫码枪发触发指令 → 等扫码结果。
    /// </summary>
    触发型 = 1,
}

/// <summary>
/// 扫码枪配置（<c>config/appsettings.json</c> 的 <c>扫码枪</c> 节，2026-11-12 新增）。
///
/// <para><b>两个寄存器：</b><see cref="扫码成功寄存器"/>（默认 D4050）由上位机写 1 告诉 PLC
/// "这一件已扫到码"；<see cref="扫码触发寄存器"/>（默认 D4010）由 PLC 置 1 请求扫码，
/// 上位机读到后写 0 应答（仅触发型使用）。</para>
///
/// <para><b>指令码：</b><see cref="触发指令"/> / <see cref="释放指令"/> 默认沿用
/// SmartScanner 参考实现的 <c>TRIGGER</c> / <c>RELEASE</c>，可在界面上改、留空即不下发。
/// 2026-11-12 起触发型真正下发：检测到 PLC 触发 → 下发触发指令 → 等码；
/// 收到条码或超过 <see cref="超时毫秒"/> 后下发释放指令收尾（扫码枪不需要指令时清空这两项即可）。</para>
/// </summary>
public sealed class 扫码枪配置
{
    /// <summary>是否启用扫码功能。默认关闭：没接扫码枪的机器不必每次启动都报串口打不开。</summary>
    public bool 启用 { get; set; }

    /// <summary>通讯方式。</summary>
    public 扫码通讯模式 通讯模式 { get; set; } = 扫码通讯模式.串口;

    /// <summary>串口号（如 <c>COM3</c>）。</summary>
    public string 串口号 { get; set; } = "COM1";

    /// <summary>波特率。</summary>
    public int 波特率 { get; set; } = 9600;

    /// <summary>数据位（5–8）。</summary>
    public int 数据位 { get; set; } = 8;

    /// <summary>停止位：<c>1</c> / <c>1.5</c> / <c>2</c>。</summary>
    public string 停止位 { get; set; } = "1";

    /// <summary>校验：<c>无</c> / <c>奇</c> / <c>偶</c>。</summary>
    public string 校验 { get; set; } = "无";

    /// <summary>扫码枪 IP（TCP 模式；扫码枪侧需配成 TCP Server）。</summary>
    public string 网口地址 { get; set; } = "192.168.1.11";

    /// <summary>扫码枪端口（TCP 模式）。</summary>
    public int 网口端口 { get; set; } = 2000;

    /// <summary>扫码成功信号寄存器（D 区单字，上位机写 1）。</summary>
    public string 扫码成功寄存器 { get; set; } = "D4050";

    /// <summary>扫码触发寄存器（D 区单字，PLC 置 1，上位机写 0 应答）。</summary>
    public string 扫码触发寄存器 { get; set; } = "D4010";

    /// <summary>工作模式。</summary>
    public 扫码工作模式 工作模式 { get; set; } = 扫码工作模式.手动型;

    /// <summary>触发指令（触发型：检测到 PLC 触发后下发给扫码枪，默认参考 SmartScanner：<c>TRIGGER</c>；留空则不下发）。</summary>
    public string 触发指令 { get; set; } = "TRIGGER";

    /// <summary>释放指令（触发型：本次扫码结束 / 超时后下发给扫码枪，默认参考 SmartScanner：<c>RELEASE</c>；留空则不下发）。</summary>
    public string 释放指令 { get; set; } = "RELEASE";

    /// <summary>触发型轮询触发寄存器的间隔（毫秒）。</summary>
    public int 轮询间隔毫秒 { get; set; } = 500;

    /// <summary>
    /// 触发型扫码超时（毫秒）：PLC 触发后若在这个时间内没有收到条码，
    /// 视作本次扫码失败——向 <see cref="扫码成功寄存器"/> 写 <c>2</c> 并下发
    /// <see cref="释放指令"/> 结束本次触发（2026-11-12 现场要求）。
    /// </summary>
    public int 超时毫秒 { get; set; } = 5000;

    /// <summary>深拷贝。</summary>
    public 扫码枪配置 克隆() => new()
    {
        启用 = 启用,
        通讯模式 = 通讯模式,
        串口号 = 串口号,
        波特率 = 波特率,
        数据位 = 数据位,
        停止位 = 停止位,
        校验 = 校验,
        网口地址 = 网口地址,
        网口端口 = 网口端口,
        扫码成功寄存器 = 扫码成功寄存器,
        扫码触发寄存器 = 扫码触发寄存器,
        工作模式 = 工作模式,
        触发指令 = 触发指令,
        释放指令 = 释放指令,
        轮询间隔毫秒 = 轮询间隔毫秒,
        超时毫秒 = 超时毫秒,
    };

    /// <summary>把 <paramref name="源"/> 的字段复制到本实例（界面编辑 → 运行配置）。</summary>
    public void 复制自(扫码枪配置 源)
    {
        ArgumentNullException.ThrowIfNull(源);

        启用 = 源.启用;
        通讯模式 = 源.通讯模式;
        串口号 = 源.串口号;
        波特率 = 源.波特率;
        数据位 = 源.数据位;
        停止位 = 源.停止位;
        校验 = 源.校验;
        网口地址 = 源.网口地址;
        网口端口 = 源.网口端口;
        扫码成功寄存器 = 源.扫码成功寄存器;
        扫码触发寄存器 = 源.扫码触发寄存器;
        工作模式 = 源.工作模式;
        触发指令 = 源.触发指令;
        释放指令 = 源.释放指令;
        轮询间隔毫秒 = 源.轮询间隔毫秒;
        超时毫秒 = 源.超时毫秒;
    }
}
