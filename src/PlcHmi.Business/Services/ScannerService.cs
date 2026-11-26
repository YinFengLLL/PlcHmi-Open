using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PlcHmi.Business.Configuration;
using PlcHmi.Communication.Modbus;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// 扫码枪服务（2026-11-12 新增，实现思路参考 SmartScanner）。
///
/// <para><b>手动型：</b>不管触发寄存器——扫码枪自己扫到码就往串口/网口发，
/// 上位机收到即视作"扫码成功"：把条码记为工单号，并给
/// <see cref="ScannerConfig.ScanSuccessRegister"/> 写 1。</para>
///
/// <para><b>触发型：</b>轮询 <see cref="ScannerConfig.ScanTriggerRegister"/>，读到 1 就写 0 应答，
/// 下发触发指令并开一个等码窗口（<see cref="ScannerConfig.TimeoutMilliseconds"/>）：
/// 窗口内收到条码 → 写 1；超时未收到 → 写 2 并下发释放指令（见 <c>ScannerService.Timeout.cs</c>）。</para>
///
/// <para><b>工单号只存内存：</b>关软件即清空（现场要求：不跨重启保留扫码信息）。</para>
/// </summary>
public sealed partial class ScannerService : IHostedService, IScannerService, IDisposable
{
    private readonly IAddressParser _parser;
    private readonly IModbusClient _client;
    private readonly IPermissionGuard _permission;
    private readonly ScannerConfig _config;
    private readonly ILogger<ScannerService> _log;

    private IScannerTransport? _transport;
    private CancellationTokenSource? _triggerCancel;
    private Task? _triggerTask;
    private volatile string _workOrder = string.Empty;

    private int _successRegister;
    private int _triggerRegister;

    public ScannerService(
        ScannerConfig Config,
        IAddressParser Parser,
        IModbusClient Client,
        IPermissionGuard Permission,
        ILogger<ScannerService> Log)
    {
        _config = Config;
        _parser = Parser;
        _client = Client;
        _permission = Permission;
        _log = Log;

        RefreshRegisterAddress();
    }

    /// <inheritdoc />
    public ScannerConfig Config => _config;

    /// <inheritdoc />
    public bool Connected => _transport?.Opened == true;

    /// <inheritdoc />
    public string WorkOrder => _workOrder;

    /// <inheritdoc />
    public event EventHandler<string>? workOrderChanged;

    /// <inheritdoc />
    public event EventHandler<bool>? connectChanged;

    // ── 生命周期 ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await RestartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        StopInnerPart();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RestartAsync(CancellationToken cancellationToken = default)
    {
        StopInnerPart();

        if (!_config.Enabled)
        {
            _log.LogInformation("扫码枪未启用（系统设置 → 扫码枪设置）");
            return Task.CompletedTask;
        }

        OpenChannel();

        if (_config.WorkMode == ScanWorkMode.TriggerKind)
        {
            StartTriggerListen();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task SaveSetAsync(ScannerConfig candidateConfig, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidateConfig);

        _permission.WantRequire(Permission.EditPlcConnect);

        Validate(candidateConfig);

        var Snapshot = candidateConfig.Clone();

        // 先落盘、后应用：写入是原子的，写失败时内存与文件都还是旧值。
        // 2026-11-19 安全加固：主文件与 .bak 都坏时拒绝落盘，绝不拿出厂默认值覆盖现场配置。
        var result = AppSettingsLoader.LoadWithResult();

        var Document = result.loadResult switch
        {
            LoadResultSplitCategory.Loaded => result.Value!,
            LoadResultSplitCategory.Missing => new AppSettingsDocument(),      // 首次部署：按默认文档创建
            LoadResultSplitCategory.RestoredFromBackup => result.Value!,
            _ => throw new InvalidOperationException(
                $"应用配置读取失败（主文件与备份均不可用），为避免覆盖现场配置已中止保存：{result.ErrorInfo}"),
        };

        if (result.loadResult is LoadResultSplitCategory.RestoredFromBackup)
        {
            _log.LogWarning(
                "保存扫码枪设置前发现 appsettings.json 损坏，已从 .bak 恢复后继续：{Path}", result.path);
        }

        Document.Scanner = Snapshot;

        var jsonText = JsonSerializer.Serialize(Document, JsonConfig.Write);

        await AtomicFile
            .WriteAllTextAsync(ConfigPath.AppSettings, jsonText, cancellationToken)
            .ConfigureAwait(false);

        _config.CopySelf(Snapshot);
        RefreshRegisterAddress();

        await RestartAsync(cancellationToken).ConfigureAwait(false);

        _log.LogInformation(
            "扫码枪设置已保存：{Mode} · {Transport} · 成功 {Ok} / 触发 {Trigger}",
            _config.WorkMode, _config.CommunicationMode, _config.ScanSuccessRegister, _config.ScanTriggerRegister);
    }

    /// <inheritdoc />
    public void Dispose() => StopInnerPart();

    // ── 通道 ────────────────────────────────────────────────────────────

    private void OpenChannel()
    {
        var Transport = _config.CommunicationMode == ScanCommunicationMode.TcpIp
            ? new TcpScanTransport()
            : (IScannerTransport)new SerialPortScanTransport();

        Transport.barcodeReceived += (_, barcode) => ProcessBarcode(barcode);
        Transport.ConnectionStateChanged += (_, connectedValue) => connectChanged?.Invoke(this, connectedValue);

        if (!Transport.Open(_config, out var error))
        {
            _log.LogWarning("扫码枪打开失败：{Error}", error);
            Transport.Dispose();
            connectChanged?.Invoke(this, false);
            return;
        }

        _transport = Transport;

        _log.LogInformation(
            "扫码枪已连接：{Transport}（{Mode}）",
            _config.CommunicationMode == ScanCommunicationMode.TcpIp
                ? $"TCP {_config.EthernetAddress}:{_config.EthernetPort}"
                : $"串口 {_config.SerialPortName} {_config.BaudRate},{_config.DataBit},{_config.Validate},{_config.StopBits}",
            _config.WorkMode);
    }

    private void StopInnerPart()
    {
        // 先关等码窗口：停止期间不应再因为超时去写 PLC
        CloseWaitCodeWindow();

        _triggerCancel?.Cancel();
        _triggerCancel?.Dispose();
        _triggerCancel = null;

        if (_triggerTask is { } task)
        {
            try
            {
                task.Wait(TimeSpan.FromSeconds(1));
            }
            catch (AggregateException)
            {
                // 停止时的取消异常无需上报
            }

            _triggerTask = null;
        }

        if (_transport is { } Transport)
        {
            // 通道对象直接丢弃：句柄在 Dispose 里释放，事件订阅随对象一起失效
            Transport.Dispose();
            _transport = null;
        }
    }

    // ── 扫码结果 ────────────────────────────────────────────────────────

    /// <summary>
    /// 把一条条码当作"刚从扫码枪收到"注入（与真实接收走同一条处理链）。
    /// <para>用途：现场排查（没带扫码枪也能验证信号与工单号）与自动化测试。
    /// 注意它<b>不是</b>后门：触发型下同样要处在等码窗口内才算数，
    /// 否则就绕过"超时后晚到的码不算数"这条规矩了。</para>
    /// </summary>
    public void InjectScanResult(string barcode) => ProcessBarcode(barcode);

    private void ProcessBarcode(string barcode)
    {
        var Text = barcode?.Trim() ?? string.Empty;

        if (Text.Length == 0)
        {
            return;
        }

        if (!ClaimScanResult())
        {
            // 触发型：没有待扫码请求（例如上一件已判超时失败）→ 晚到的码不能算数
            _log.LogWarning("触发型：收到条码「{Code}」但当前没有待扫码请求，已忽略", Text);
            return;
        }

        // 先记工单号、后写成功信号：PLC 见到 D4050=1 时，历史记录取到的工单号就是这一条
        _workOrder = Text;
        workOrderChanged?.Invoke(this, Text);

        _log.LogInformation("扫码成功：{Code}", Text);

        _ = Task.Run(() => WriteSignalAsync(1, "扫码成功信号"));
    }

    /// <summary>写扫码结果信号（<c>1</c> = 扫到；<c>2</c> = 触发后超时未扫到）。</summary>
    private async Task WriteSignalAsync(int Value, string usePurpose)
    {
        if (!_client.IsConnected || _successRegister <= 0)
        {
            _log.LogWarning("需要写{Use}（{Value}）但 PLC 未连接（或成功信号寄存器未配置）", usePurpose, Value);
            return;
        }

        try
        {
            await _client.WriteSingleRegisterAsync(_successRegister, (ushort)Value).ConfigureAwait(false);
            _log.LogInformation("已写{Use}：D{Address} = {Value}", usePurpose, _successRegister, Value);
        }
        catch (InvalidOperationException exception)
        {
            _log.LogWarning(exception, "写{Use}失败（D{Address}）", usePurpose, _successRegister);
        }
        catch (IOException exception)
        {
            _log.LogWarning(exception, "写{Use}失败（D{Address}）", usePurpose, _successRegister);
        }
        catch (SocketException exception)
        {
            _log.LogWarning(exception, "写{Use}失败（D{Address}）", usePurpose, _successRegister);
        }
    }

    // ── 触发型 ──────────────────────────────────────────────────────────

    private void StartTriggerListen()
    {
        _triggerCancel = new CancellationTokenSource();
        var token = _triggerCancel.Token;

        _triggerTask = Task.Run(() => TriggerLoopRing(token), token);

        _log.LogInformation(
            "触发型扫码已启动：监听 D{Address}（每 {Interval} ms）",
            _triggerRegister, Math.Clamp(_config.PollingIntervalMilliseconds, 100, 5000));
    }

    private async Task TriggerLoopRing(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Clamp(_config.PollingIntervalMilliseconds, 100, 5000));
        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!_client.IsConnected || _triggerRegister <= 0)
                {
                    continue;
                }

                ushort[] Value;

                try
                {
                    Value = await _client.ReadHoldingRegisterAsync(_triggerRegister, 1, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    continue;
                }
                catch (IOException)
                {
                    continue;
                }
                catch (SocketException)
                {
                    continue;
                }

                if (Value.Length == 0 || Value[0] == 0)
                {
                    continue;
                }

                try
                {
                    await _client.WriteSingleRegisterAsync(_triggerRegister, 0, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    continue;
                }
                catch (IOException)
                {
                    continue;
                }
                catch (SocketException)
                {
                    continue;
                }

                // 顺序要紧：**先开窗、后下发触发指令**。
                // 反过来的话，自动感应型扫码枪可能在指令发出后几毫秒就回码，
                // 那条码会撞在"窗口还没开"上被判成"没有待扫码请求"丢掉，
                // 这一件就白白走到超时失败。
                OpenWaitCodeWindow();
                SendCommand(_config.TriggerCommand, "触发");

                _log.LogInformation(
                    "触发型：PLC 置位 D{Address}，已写 0 应答并开等码窗口（超时 {Timeout} ms）",
                    _triggerRegister, Math.Clamp(_config.TimeoutMilliseconds, 200, 60000));
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
    }
}
