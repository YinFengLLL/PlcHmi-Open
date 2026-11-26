using PlcHmi.Core.Models;

namespace PlcHmi.Core.Abstractions;

/// <summary>
/// 扫码枪通讯通道：串口与 TCP/IP 两种实现共用同一契约
/// （思路参考 SmartScanner 的 <c>IScannerTransport</c>，2026-11-12）。
///
/// <para><b>不抛异常：</b><see cref="Open"/> 失败返回 <c>false</c> 并给出可读原因——
/// 扫码枪没插好是现场的常态，不该让一个可选外设把整页/整个启动流程打断。</para>
/// </summary>
public interface IScannerTransport : IDisposable
{
    /// <summary>是否已打开 / 已连接。</summary>
    bool Opened { get; }

    /// <summary>收到一条完整条码（在接收线程触发，界面需自行调度）。</summary>
    event EventHandler<string>? barcodeReceived;

    /// <summary>连接状态变化。</summary>
    event EventHandler<bool>? ConnectionStateChanged;

    /// <summary>打开 / 连接。失败返回 <c>false</c>，原因写进 <paramref name="error"/>。</summary>
    bool Open(ScannerConfig Config, out string? error);

    /// <summary>关闭 / 断开。</summary>
    void Close();

    /// <summary>发送 ASCII 指令（如 TRIGGER / RELEASE）；未连接或写入失败返回 <c>false</c>。</summary>
    bool SenderCommand(string command);
}

/// <summary>
/// 扫码枪业务服务：打开通道、接收条码、写 PLC 成功信号、维护"上一次扫码结果"。
///
/// <para><b>工单号不落盘：</b>扫码结果只存在内存里（关软件即清空），
/// 历史记录页用它标记"这一行是哪一次扫码后产生的"。</para>
/// </summary>
public interface IScannerService
{
    /// <summary>当前运行配置（只读快照的引用；修改请走 <see cref="SaveSetAsync"/>）。</summary>
    ScannerConfig Config { get; }

    /// <summary>扫码通道是否已打开。</summary>
    bool Connected { get; }

    /// <summary>上一次扫码得到的条码（工单号）；未扫过为空串。</summary>
    string WorkOrder { get; }

    /// <summary>工单号变化（在接收线程触发）。</summary>
    event EventHandler<string>? workOrderChanged;

    /// <summary>扫码通道连接状态变化。</summary>
    event EventHandler<bool>? connectChanged;

    /// <summary>保存并立即应用新配置（落盘 + 重连）。要求管理员权限。</summary>
    Task SaveSetAsync(ScannerConfig candidateConfig, CancellationToken cancellationToken = default);

    /// <summary>按当前配置重新打开通道（未启用时只关闭）。</summary>
    Task RestartAsync(CancellationToken cancellationToken = default);
}
