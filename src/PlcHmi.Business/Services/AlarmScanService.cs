using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// 报警扫描服务。
///
/// <para><b>做什么：</b>周期性把 <c>alarms.json</c> 里每条定义绑定的 PLC 地址读回来，
/// 按位或整字判断是否触发——触发则让 <see cref="IAlarmService"/> 产生报警，
/// 恢复为 0 则自动消除。这样"PLC 的某个位为 1"就映射成"界面上显示某条报警文本"。</para>
///
/// <para><b>读法：</b>M 区是位软元件，用 FC01 读线圈；D 区是字软元件，用 FC03 读保持寄存器。
/// 两者分流处理。同区且地址接近的合并为一次请求。</para>
/// </summary>
public sealed class AlarmScanService : IHostedService
{
    /// <summary>扫描周期。</summary>
    private static readonly TimeSpan scanInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>合并读取时允许的最大跨度（避免一次读太多无关寄存器）。</summary>
    private const int maxMergeInterval = 16;

    /// <summary>单次读的上限（模板保守取值）。</summary>
    private const int PerRequestMaxRegisterNumber = 100;

    private readonly IAlarmService _alarm;
    private readonly IAlarmDefinitionService _definition;
    private readonly IModbusClient _client;
    private readonly IAddressParser _parser;
    private readonly ILogger<AlarmScanService> _log;

    /// <summary>当前处于活动状态的定义 Id，用于判断何时该消除。</summary>
    private readonly HashSet<string> _activeId = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 读取计划缓存。定义很少变，此前每轮（500ms）都重新 Where/解析/排序/合并一遍，
    /// 还要对 定义集合（每次 ToArray 快照）再拷一次——纯浪费。
    /// 定义变更（增删改 / 重载）时经 <see cref="IAlarmDefinitionService.definitionChanged"/> 置空重建。
    /// </summary>
    private List<ReadPlan>? _planBufferStore;

    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _loopRing;

    public AlarmScanService(
        IAlarmService alarmList,
        IAlarmDefinitionService definitionList,
        IModbusClient Client,
        IAddressParser Parser,
        ILogger<AlarmScanService> Log)
    {
        _alarm = alarmList;
        _definition = definitionList;
        _client = Client;
        _parser = Parser;
        _log = Log;

        _definition.definitionChanged += (_, _) => _planBufferStore = null;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // 防重入：二次 Start 不先 Stop 会覆盖 _取消令牌源，泄漏旧 CTS 与旧循环
        if (_cancellationTokenSource is not null)
        {
            return Task.CompletedTask;
        }

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopRing = Task.Run(() => ScanLoopRingAsync(_cancellationTokenSource.Token), _cancellationTokenSource.Token);

        _log.LogInformation("报警扫描已启动，周期 {Interval}ms", scanInterval.TotalMilliseconds);
        return Task.CompletedTask;
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
            var waitTimeout = TimeSpan.FromSeconds(5);

            try
            {
                // 带超时：循环若因 bug 卡住，不能让程序退出无限期挂在这里
                await _loopRing.WaitAsync(waitTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // 5 秒内没退出 = 循环卡住的征兆，不是正常关闭，必须留下痕迹
                _log.LogWarning("等待报警扫描循环退出超时（{Seconds}s），已放弃等待", waitTimeout.TotalSeconds);
            }
            catch (OperationCanceledException)
            {
                // 外部取消（程序退出），正常
            }
            catch (Exception exception)
            {
                // 循环因自身 bug 挂掉：记录但不影响退出流程
                _log.LogWarning(exception, "报警扫描循环异常退出");
            }
        }

        _cancellationTokenSource.Dispose();
        _cancellationTokenSource = null;

        _log.LogInformation("报警扫描已停止");
    }

    // ── 扫描循环 ────────────────────────────────────────────────────────

    private async Task ScanLoopRingAsync(CancellationToken cancellationToken)
    {
        // 2026-11-19 性能改造：Task.Delay(扫描间隔) 的实际周期 = 扫描耗时 + 间隔，会累积漂移；
        // 改用 PeriodicTimer（与轮询引擎同一套写法），节拍固定，不再随扫描耗时后移。
        using var timer = new PeriodicTimer(scanInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    if (_client.IsConnected)
                    {
                        await ScanAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception exception)
                {
                    // 单次扫描失败不能把循环打断，否则一次网络抖动就永远不再报警
                    _log.LogWarning(exception, "报警扫描失败，将在下个周期重试");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
    }

    /// <summary>执行一轮扫描。</summary>
    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        // 读取计划缓存：定义未变更时直接复用，免去每轮重新解析/排序/合并。
        // 2026-11-19：启用项过滤从每轮无条件执行改为只在构建方案时执行一次
        // （缓存命中时那份列表根本用不上，白做一次 Where+ToList）。
        var planList = _planBufferStore;

        if (planList is null)
        {
            var definitionList = _definition.DefinitionCollection.Where(EntryItem => EntryItem.Enabled).ToList();

            if (definitionList.Count == 0)
            {
                return;
            }

            planList = BuildReadPlanList(definitionList);
            _planBufferStore = planList;
        }

        foreach (var plan in planList)
        {
            await ScanPlanAsync(plan, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ScanPlanAsync(ReadPlan plan, CancellationToken cancellationToken)
    {
        try
        {
            if (plan.ElementItem == SoftElement.M)
            {
                var Coil = await _client
                    .ReadCoilAsync(plan.StartAddress, plan.Count, cancellationToken)
                    .ConfigureAwait(false);

                foreach (var (DefinitionItem, addressValue) in plan.EntryCollection)
                {
                    var OffsetValue = addressValue - plan.StartAddress;

                    if (OffsetValue >= 0 && OffsetValue < Coil.Length)
                    {
                        ApplyState(DefinitionItem, Coil[OffsetValue]);
                    }
                }
            }
            else
            {
                var registerList = await _client
                    .ReadHoldingRegisterAsync(plan.StartAddress, plan.Count, cancellationToken)
                    .ConfigureAwait(false);

                foreach (var (DefinitionItem, addressValue) in plan.EntryCollection)
                {
                    var OffsetValue = addressValue - plan.StartAddress;

                    if (OffsetValue < 0 || OffsetValue >= registerList.Length)
                    {
                        continue;
                    }

                    var SourceValue = registerList[OffsetValue];

                    var IsOpenStart = DefinitionItem.IsBitMode
                        ? (SourceValue & (1 << DefinitionItem.Bit)) != 0
                        : SourceValue != 0;

                    ApplyState(DefinitionItem, IsOpenStart);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // 报警读不回来 = "该报的警不报"，属低频且关键，现场必须看得见（默认 Information 级过滤掉 Debug）
            _log.LogWarning(exception, "读取报警区 {Element}{Start} 失败，将在下个周期重试",
                plan.ElementItem, plan.StartAddress);
        }
    }

    /// <summary>根据状态决定产生还是消除报警。</summary>
    private void ApplyState(AlarmDefinition DefinitionItem, bool IsOpenStart)
    {
        var Active = _activeId.Contains(DefinitionItem.Id);

        if (IsOpenStart && !Active)
        {
            _activeId.Add(DefinitionItem.Id);
            _alarm.Trigger(DefinitionItem.Id, DefinitionItem.Message, DefinitionItem.Level);
        }
        else if (!IsOpenStart && Active)
        {
            _activeId.Remove(DefinitionItem.Id);
            _alarm.ClearEmpty(DefinitionItem.Id);
        }
    }

    // ── 读取计划 ────────────────────────────────────────────────────────

    /// <summary>把定义解析成地址，并按「同软元件区 + 地址接近」合并成读取计划。</summary>
    private List<ReadPlan> BuildReadPlanList(List<AlarmDefinition> definitionList)
    {
        var resolved = new List<(AlarmDefinition Definition, SoftElement ElementItem, int Address)>();

        foreach (var DefinitionItem in definitionList)
        {
            if (_parser.TryParse(DefinitionItem.Address, out var PlcAddress) && PlcAddress is not null)
            {
                if (PlcAddress.ElementItem == SoftElement.M && DefinitionItem.IsBitMode)
                {
                    // M 区报警走线圈路径，位号被完全忽略——配置里填了会静默无效，
                    // 现场表现是"报警该响不响"，必须留痕（构建只在缓存失效时发生，不会刷屏）
                    _log.LogWarning(
                        "报警 {Id} 的地址 {Address} 是 M 区，位号 {Bit} 不生效（M 区按整位扫描），请删除该位号",
                        DefinitionItem.Id, DefinitionItem.Address, DefinitionItem.Bit);
                }

                resolved.Add((DefinitionItem, PlcAddress.ElementItem, PlcAddress.ModbusAddress));
            }
            else
            {
                // 地址解析失败 = 这条报警永远不会触发，属配置错误，必须让现场看见
                _log.LogWarning("报警 {Id} 的地址「{Address}」无法解析，已跳过（该报警不会生效）",
                    DefinitionItem.Id, DefinitionItem.Address);
            }
        }

        var planList = new List<ReadPlan>();

        // 先按软元件区、再按地址排序 —— M0 与 D0 的 Modbus 地址都是 0，
        // 不区分区就会把两个不同区域的数据混到一次请求里。
        foreach (var Entry in resolved
                     .OrderBy(coordinateR => coordinateR.ElementItem)
                     .ThenBy(coordinateR => coordinateR.Address))
        {
            var Last = planList.Count > 0 ? planList[^1] : null;

            // 注意：last 为空（首条）时必须走新建分支。
            // 早先这里先算了 last.StartAddress 才判空，首轮扫描直接 NRE，
            // 异常被循环吞掉 → 报警永远不产生。别把判空挪到后面。
            if (Last is not null &&
                Last.ElementItem == Entry.ElementItem &&
                Entry.Address >= Last.StartAddress &&
                Entry.Address - Last.StartAddress + 1 <= PerRequestMaxRegisterNumber &&
                Entry.Address - (Last.StartAddress + Last.Count) <= maxMergeInterval)
            {
                Last.EntryCollection.Add((Entry.Definition, Entry.Address));
                Last.Count = Math.Max(Last.Count, Entry.Address - Last.StartAddress + 1);
            }
            else
            {
                var plan = new ReadPlan { StartAddress = Entry.Address, ElementItem = Entry.ElementItem };
                plan.EntryCollection.Add((Entry.Definition, Entry.Address));
                plan.Count = 1;
                planList.Add(plan);
            }
        }

        return planList;
    }

    /// <summary>一次读取计划。</summary>
    private sealed class ReadPlan
    {
        /// <summary>起始 Modbus 地址。</summary>
        public int StartAddress { get; init; }

        /// <summary>读取长度（会随合并增长）。</summary>
        public int Count { get; set; }

        /// <summary>所属软元件区（M 用线圈、D 用保持寄存器）。</summary>
        public SoftElement ElementItem { get; init; }

        /// <summary>本计划覆盖的报警定义与它们的 Modbus 地址。</summary>
        public List<(AlarmDefinition Definition, int Address)> EntryCollection { get; } = new();
    }
}
