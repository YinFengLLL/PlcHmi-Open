using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PlcHmi.Business.Configuration;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// 历史记录服务。详见 <see cref="IHistoryRecordService"/>。
///
/// <para><b>采集原理：</b>周期性读 <c>触发位</c>（M 区线圈），检测 <b>0 → 1 上升沿</b>；
/// 沿到达时把配置里各数据项的地址读回来，拼成一条记录落盘（一天一个文件）。</para>
///
/// <para><b>为什么不用轮询引擎：</b>触发位与数据项地址都要允许现场随时改，
/// 而轮询引擎的读取计划是启动时按 <c>tags.json</c> 编译好的；把可编辑地址塞进点位表，
/// 就等于"改地址必须重启程序"。这里按需直读，与示教 / 参数页读回的做法一致。</para>
///
/// <para><b>整条记录要么全有要么全无：</b>任一数据项读失败就丢弃本次记录并记 Warning ——
/// 半条记录（前几列有值、后几列空白）在追溯时比"没有这一条"更危险：看的人以为设备就是这么干的。</para>
///
/// <para>本类按职责拆成三个文件：本文件（生命周期与采集）、
/// <c>.Acquisition.cs</c>（读取计划）、<c>.Config.cs</c>（校验与配置读写）。</para>
/// </summary>
public sealed partial class HistoryRecordService : IHostedService, IHistoryRecordService
{
    /// <summary>扫描周期：与报警扫描一致，够快且不给 PLC 添压力。</summary>
    private static readonly TimeSpan scanInterval = TimeSpan.FromMilliseconds(500);

    private readonly IAddressParser _parser;
    private readonly IModbusClient _client;
    private readonly IPermissionGuard _permission;
    private readonly HistoryRecordStore _store;
    private readonly ILogger<HistoryRecordService> _log;
    private readonly IConfigHealth? _health;
    private readonly Func<DateTime> _clock;

    /// <summary>
    /// 当前配置。<b>初值就是内置默认值</b>：宿主启动前（或配置文件读不出来时）
    /// <see cref="Config"/> 也必须是一份可用的数据体，否则界面会显示一个"无列可显"的空表，
    /// 让人以为是页面坏了。
    /// </summary>
    private HistoryRecordConfig _config = CreateDefaultValue();

    /// <summary>触发位上一次的电平。<c>null</c> = 尚未建立基线（启动后第一轮不记录）。</summary>
    private bool? _lastTrigger;

    /// <summary>记录已落盘但触发位复位没写出去：下个周期补一次复位（绝不重复记录）。</summary>
    private bool _pendingReset;

    /// <summary>上一轮数据项读取失败：触发位仍为 1，持续重试直到读取成功。</summary>
    private bool _readRetry;

    /// <summary>读取失败只提示一次（每 500ms 一条 Warning 会把日志刷爆），成功后复位。</summary>
    private bool _readFailedHinted;

    private bool _hintedConfigError;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _loopRing;

    /// <summary>扫码枪服务（工单号来源；未注册扫码枪时为空，记录里工单号就是空串）。</summary>
    private readonly IScannerService? _scanner;

    public HistoryRecordService(
        IAddressParser Parser,
        IModbusClient Client,
        IPermissionGuard Permission,
        HistoryRecordStore Store,
        ILogger<HistoryRecordService> Log,
        IConfigHealth? Health = null,
        Func<DateTime>? clockProvider = null,
        IScannerService? Scanner = null)
    {
        _parser = Parser;
        _client = Client;
        _permission = Permission;
        _store = Store;
        _log = Log;
        _health = Health;
        _clock = clockProvider ?? (() => DateTime.Now);
        _scanner = Scanner;
    }

    /// <inheritdoc />
    public HistoryRecordConfig Config => _config;

    /// <inheritdoc />
    public event EventHandler? configChanged;

    /// <inheritdoc />
    public event EventHandler<HistoryRecordEntry>? recordAlreadyAdd;

    // ── 生命周期 ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await LoadConfigAsync(cancellationToken).ConfigureAwait(false);

        // 装载当天已有记录：否则重启后第一条记录会把当天早班的数据整个覆盖
        await _store.InitializeAsync(_clock().Date, _config.RetainDays, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // 先把配置与当天已有记录装载完，再起采集循环：
        // 反过来的话，启动瞬间的第一条记录可能正好落在"写入日尚未就绪"的窗口里，
        // 而写入日未就绪时落盘会覆盖当天已有数据。
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopRing = Task.Run(() => SamplingLoopRingAsync(_cancellationTokenSource.Token), _cancellationTokenSource.Token);

        _log.LogInformation(
            "历史记录采集已启动：触发位 {Address}（{State}），{Count} 个数据项，周期 {Interval}ms",
            _config.TriggerAddress, _config.Enabled ? "已启用" : "已停用",
            _config.DataItemCollection.Count, scanInterval.TotalMilliseconds);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cancellationTokenSource is null)
        {
            return;
        }

        await _cancellationTokenSource.CancelAsync().ConfigureAwait(false);

        if (_loopRing is not null)
        {
            try
            {
                await _loopRing.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // 循环卡住是缺陷征兆，不是正常关闭，必须留痕
                _log.LogWarning("等待历史记录采集循环退出超时（5s），已放弃等待");
            }
            catch (OperationCanceledException)
            {
                // 外部取消（程序退出），正常
            }
        }

        _cancellationTokenSource.Dispose();
        _cancellationTokenSource = null;
        _log.LogInformation("历史记录采集已停止");
    }

    // ── 采集循环 ────────────────────────────────────────────────────────

    private async Task SamplingLoopRingAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_client.IsConnected)
                {
                    await SamplingAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                // 单轮失败不能打断循环：一次网络抖动不该让后续所有动作都不再入档
                _log.LogWarning(exception, "历史记录采集失败，将在下个周期重试");
            }

            try
            {
                await Task.Delay(scanInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 读一次触发位并推进采集状态机：
    /// <b>上升沿 → 读取数据项 → 落盘 → 复位触发位</b>（与 PLC 的握手）。
    ///
    /// <para><b>握手语义：</b>PLC 置位后只管等复位——读取失败就不复位，
    /// 位保持 1，下一周期继续重试，直到把数据完整读走才交还；任何一环失败
    /// 都不会出现"半条记录"或"PLC 干等"的单边状态。</para>
    /// </summary>
    private async Task SamplingAsync(CancellationToken cancellationToken)
    {
        var Config = _config;

        if (!Config.Enabled || Config.DataItemCollection.Count == 0)
        {
            return;
        }

        if (!_parser.TryParse(Config.TriggerAddress, out var triggerBit) || triggerBit is null || triggerBit.ElementItem != SoftElement.M)
        {
            HintConfigError($"触发位「{Config.TriggerAddress}」不是有效的 M 区地址，历史记录不会产生。");
            return;
        }

        var Coil = await _client
            .ReadCoilAsync(triggerBit.ModbusAddress, 1, cancellationToken)
            .ConfigureAwait(false);

        var Current = Coil.Length > 0 && Coil[0];
        var LastTime = _lastTrigger;
        _lastTrigger = Current;

        if (!Current)
        {
            return;
        }

        // 启动后第一次读到 1（基线尚未建立）不记录：那是"位本来就亮着"，不是这次动作产生的沿
        var isRisingEdge = LastTime == false;

        if (!isRisingEdge && !_pendingReset && !_readRetry)
        {
            // 位持续为 1 且没有任何待办：关闭"读取后复位"时由 PLC 管理该位，
            // 保持期间绝不能重复记录；开启时正常流程早被我们复位，不会走到这里
            return;
        }

        if (_pendingReset)
        {
            if (Config.ReadAfterResetTriggerBit)
            {
                // 上一条记录已落盘但复位没写出去：先补握手，绝不重复记录
                await ResetTriggerBit(triggerBit, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // 配置已改为 PLC 自管触发位：放弃补复位
                _pendingReset = false;
            }

            return;
        }

        var ValueList = await ReadDataItemAsync(Config, cancellationToken).ConfigureAwait(false);

        if (ValueList is null)
        {
            // 读取失败：不复位（PLC 的握手继续等待），位仍为 1，下一周期重试。
            // "没记录"还能从日志与报警里查到原因，半条记录却会让追溯的人信以为真
            _readRetry = true;

            if (!_readFailedHinted)
            {
                _readFailedHinted = true;
                _log.LogWarning("触发位 {Address} 的数据项读取失败，本条暂不落盘也不复位，将持续重试",
                    Config.TriggerAddress);
            }

            return;
        }

        _readRetry = false;
        _readFailedHinted = false;

        // 工单号 = 触发这一刻"上一次扫码的结果"（只存内存，不落盘）
        var Entry = new HistoryRecordEntry
        {
            Timestamp = _clock(),
            NumericValueCollection = ValueList,
            WorkOrder = _scanner?.WorkOrder ?? string.Empty,
        };

        // 无论落盘成功、达到单日上限（返回 false）还是写盘抛异常，都必须把触发位
        // 交还 PLC：否则位恒为 1，下一轮被上面的守卫直接 return，此后再无任何记录
        // （现场表现：历史记录某天起全部消失且无线索）。曾经只有 if(await ...) 的
        // 成功分支才复位——达上限与写盘失败两条路径全漏。
        var alreadyPersist = false;

        try
        {
            alreadyPersist = await _store.AppendAsync(Entry, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (Config.ReadAfterResetTriggerBit)
            {
                _pendingReset = true;
                await ResetTriggerBit(triggerBit, CancellationToken.None).ConfigureAwait(false);
            }
        }

        if (!alreadyPersist)
        {
            // 达到单日上限或存储拒绝：本条不推送事件，但握手已完成，PLC 可继续置位
            _log.LogWarning("历史记录「{Title}」未落盘（单日 {Max} 条上限或存储不可用），本条已丢弃",
                Config.Title, HistoryRecordStore.singleDayMaxRecordCount);
            return;
        }

        _log.LogInformation("已记录 {Title}：{Time:HH:mm:ss}，{Count} 个数据项",
            Config.Title, Entry.Timestamp, ValueList.Count);

        recordAlreadyAdd?.Invoke(this, Entry.Clone());
    }

    /// <summary>
    /// 把触发位复位为 0（读取完成握手）。
    ///
    /// <para><b>写失败不在这里吞：</b>异常抛给采集循环统一记日志；
    /// 调用方已提前置位 <see cref="_pendingReset"/>，下个周期会自动补一次复位，
    /// 触发位不会卡在 1 让 PLC 干等。</para>
    /// </summary>
    private async Task ResetTriggerBit(PlcAddress triggerBit, CancellationToken cancellationToken)
    {
        await _client
            .WriteSingleCoilAsync(triggerBit.ModbusAddress, false, cancellationToken)
            .ConfigureAwait(false);

        _pendingReset = false;

        // 复位成功即视为电平已归零：若 PLC 随即再次置位，下一轮读到的是新的上升沿，
        // 不会因为本轮采样值恰为 1 而漏掉那条记录
        _lastTrigger = false;

        _log.LogInformation("已复位触发位 {Address}（数据读取完成）", triggerBit.Source);
    }

    // ── 查询 / 导出 / 清除 ──────────────────────────────────────────────

    /// <inheritdoc />
    public Task<IReadOnlyList<HistoryRecordEntry>> ReadDayAsync(
        DateTime Date, CancellationToken cancellationToken = default) =>
        _store.ReadDayAsync(Date, cancellationToken);

    /// <inheritdoc />
    public IReadOnlyList<DateTime> HasRecordDateCollection() => _store.HasRecordDateCollection();

    /// <inheritdoc />
    public Task ClearRemoveAllAsync(CancellationToken cancellationToken = default)
    {
        _permission.WantRequire(Permission.EditHistoryRecord);

        _store.ClearRemoveAll();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task ExportCsvToFileAsync(
        string filePath, DateTime Date, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var entryList = await _store.ReadDayAsync(Date, cancellationToken).ConfigureAwait(false);
        var Text = HistoryRecordCsvCodec.Encode(_config, entryList);

        var dir = Path.GetDirectoryName(filePath);

        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // 必须带 BOM：否则 Excel 双击打开时中文会乱码
        await File.WriteAllTextAsync(
                filePath, Text,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                cancellationToken)
            .ConfigureAwait(false);

        _log.LogInformation("{Date:yyyy-MM-dd} 的历史记录已导出：{Path}（{Count} 条）",
            Date.Date, filePath, entryList.Count);
    }
}
