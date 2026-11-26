using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PlcHmi.Communication.Modbus;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// 轮询引擎。按分组周期采集点位，做变化检测后分发。
///
/// <para><b>优化策略：</b></para>
/// <list type="number">
///   <item>连续地址合并为一次请求，减少往返次数；</item>
///   <item>单次请求不超过 <c>MaxRegistersPerRequest</c>（默认 100，汇川上限 123）；</item>
///   <item>采集运行在后台线程，UI 只经事件收到变化的值。</item>
/// </list>
/// </summary>
public sealed partial class PollingEngine : IPollingEngine, IAsyncDisposable
{
    private readonly IModbusClient _client;
    private readonly ITagRepository _tag;
    private readonly IIoPointService _ioPointTable;
    private readonly IAddressParser _parser;
    private readonly TagValueService _valueList;
    private readonly PollingOptions _pollingOptions;
    private readonly PlcConnectionOptions _plcOptions;
    private readonly ILogger<PollingEngine> _log;

    private readonly List<PollingGroup> _group = new();

    /// <summary>各分组最近一轮采集耗时。三个分组并发写，故单独用小锁保护。</summary>
    private readonly Dictionary<TagGroup, double> _weekPeriodMillisecondsGroup = new();
    private readonly object _weekPeriodSync = new();

    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _workerThread;

    public PollingEngine(
        IModbusClient Client,
        ITagRepository tagList,
        IIoPointService IoPointTable,
        IAddressParser Parser,
        TagValueService ValueList,
        PollingOptions PollingOptions,
        PlcConnectionOptions PlcOptions,
        ILogger<PollingEngine> Log)
    {
        _client = Client;
        _tag = tagList;
        _ioPointTable = IoPointTable;
        _parser = Parser;
        _valueList = ValueList;
        _pollingOptions = PollingOptions;
        _plcOptions = PlcOptions;
        _log = Log;
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<TagGroup, double> WeekPeriodMillisecondsGroup
    {
        get
        {
            lock (_weekPeriodSync)
            {
                return new Dictionary<TagGroup, double>(_weekPeriodMillisecondsGroup);
            }
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_workerThread is not null)
        {
            return Task.CompletedTask;
        }

        PreparePlanList();

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _workerThread = Task.Run(() => RunAsync(_cancellationTokenSource.Token), CancellationToken.None);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_cancellationTokenSource is null || _workerThread is null)
        {
            return;
        }

        await _cancellationTokenSource.CancelAsync().ConfigureAwait(false);

        var waitTimeout = TimeSpan.FromSeconds(5);
        var alreadyExit = false;

        try
        {
            await _workerThread.WaitAsync(waitTimeout, cancellationToken).ConfigureAwait(false);
            alreadyExit = true;
        }
        catch (TimeoutException)
        {
            // 5 秒内没退出 = 循环卡住的征兆，不是正常关闭，必须留下痕迹
            _log.LogWarning("等待轮询引擎退出超时（{Seconds}s），已放弃等待", waitTimeout.TotalSeconds);
        }
        catch (OperationCanceledException)
        {
            // 外部取消（程序退出），正常
            alreadyExit = true;
        }
        catch (Exception exception)
        {
            // 循环本身有兜底 catch，这里只是防御：任何残留异常都不该拖住程序退出
            _log.LogWarning(exception, "轮询引擎异常退出");
            alreadyExit = true;
        }
        finally
        {
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = null;

            // 仅当循环确认已退出才清空方案并放行下次启动：
            // 超时后旧线程可能仍在 foreach 读取方案——此时 Clear() 会抛
            // InvalidOperationException（表现为"这一轮采集静默丢失"），
            // 且紧随的"重建方案列表"会因 _工作线程 == null 启动第二个轮询循环，
            // 两个循环同时抢占同一个串行化连接。
            if (alreadyExit)
            {
                _workerThread = null;
                _group.Clear();
                _ioReadPlan.Clear();

                lock (_weekPeriodSync)
                {
                    _weekPeriodMillisecondsGroup.Clear();
                }
            }
            else
            {
                // 旧循环未退出：保留句柄让 异步启动 拒绝重复启动（fail-safe），
                // 重建操作会因此被拒绝——比两个循环抢一条连接可诊断得多
                _log.LogWarning("轮询引擎旧循环仍未退出，已阻止重复启动；请重启程序恢复采集计划重建");
            }
        }
    }

    /// <inheritdoc />
    public async Task RebuildPlanListAsync(CancellationToken cancellationToken = default)
    {
        // 走"安全停止 → 重新编译 → 重新启动"，而不是运行中增量替换读取计划：
        // RunAsync 在启动那一刻就把 _groups 分发给各分组循环，运行中改集合会引入并发读写风险。
        // 重建只需几百毫秒，而且只在管理员改地址这种低频操作时发生，代价可接受。
        await StopAsync(cancellationToken).ConfigureAwait(false);
        await StartAsync(cancellationToken).ConfigureAwait(false);

        _log.LogInformation("采集计划已重建，共 {GroupCount} 个分组", _group.Count);
    }

    /// <summary>构建各分组的读取计划。</summary>
    private void PreparePlanList()
    {
        _group.Clear();

        var GridItem = _ioPointTable.DataGrid;
        _enabledInput.Clear();
        _enabledInput.AddRange(GridItem.Input.Where(inputPoint => inputPoint.Enabled));
        _enabledOutput.Clear();
        _enabledOutput.AddRange(GridItem.Output.Where(outputPoint => outputPoint.Enabled));

        // IO 点地址解析一次编译为读取方案（热路径每周期复用，不再重复正则解析）
        // X/Y 区位区可经 io-points.json 元显式覆盖（部分机型物理输入在离散输入区）
        BuildIoReadPlan(GridItem.Element.XArea, GridItem.Element.YArea);

        foreach (var (group, intervalItem) in new[]
                 {
                     (TagGroup.Fast, _pollingOptions.FastIntervalMilliseconds),
                     (TagGroup.Slow, _pollingOptions.SlowIntervalMilliseconds),
                     (TagGroup.Event, _pollingOptions.EventIntervalMilliseconds),
                 })
        {
            var groupTag = _tag.GetGroup(group);
            var planList = BuildReadPlanList(groupTag);

            if (planList.Count == 0 && group != TagGroup.Fast)
            {
                continue;
            }

            _group.Add(new PollingGroup(group, TimeSpan.FromMilliseconds(intervalItem), planList, group == TagGroup.Fast));
        }

        foreach (var groupItem in _group)
        {
            _log.LogInformation("轮询分组 {Group}：周期 {Interval}ms，{PlanCount} 个读取计划",
                groupItem.Group, groupItem.interval.TotalMilliseconds, groupItem.planCollection.Count);
        }

        _log.LogInformation("IO 轮询：X {InputCount} 点、Y {OutputCount} 点，共 {PlanCount} 个读取计划",
            _enabledInput.Count, _enabledOutput.Count, _ioReadPlan.Count);
    }

    // 读取计划编译（构建读取方案列表 / 合并地址 / 计划记录类型）见 PollingEngine.Plans.cs（partial）。

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var task = _group.Select(groupItem => RunGroupAsync(groupItem, cancellationToken)).ToList();

        try
        {
            await Task.WhenAll(task).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "轮询引擎异常退出");
        }
    }

    private async Task RunGroupAsync(PollingGroup group, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(group.interval);

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!_client.IsConnected)
            {
                continue;
            }

            var countWhen = Stopwatch.StartNew();

            try
            {
                // 快组顺带刷新 IO 点
                if (group.hasIo)
                {
                    await PollingIoPointAsync(cancellationToken).ConfigureAwait(false);
                }

                await PollingTagAsync(group, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                _log.LogWarning("轮询分组 {Group} 出现异常：{Message}", group.Group, exception.Message);
            }

            countWhen.Stop();

            lock (_weekPeriodSync)
            {
                _weekPeriodMillisecondsGroup[group.Group] = countWhen.Elapsed.TotalMilliseconds;
            }
        }
    }

    /// <summary>按读取计划采集点位，解码并发布。</summary>
    private async Task PollingTagAsync(PollingGroup group, CancellationToken cancellationToken)
    {
        foreach (var plan in group.planCollection)
        {
            try
            {
                switch (plan.Area)
                {
                    case ModbusArea.Coil:
                    {
                        var Bit = await _client.ReadCoilAsync(plan.StartAddress, plan.Count, cancellationToken)
                            .ConfigureAwait(false);
                        RaiseLayoutPlanValueList(plan, Bit, null);
                        break;
                    }

                    case ModbusArea.DiscreteInput:
                    {
                        var Bit = await _client.ReadDiscreteInputAsync(plan.StartAddress, plan.Count, cancellationToken)
                            .ConfigureAwait(false);
                        RaiseLayoutPlanValueList(plan, Bit, null);
                        break;
                    }

                    case ModbusArea.InputRegister:
                    {
                        var registerList = await _client.ReadInputRegisterAsync(plan.StartAddress, plan.Count, cancellationToken)
                            .ConfigureAwait(false);
                        RaiseLayoutPlanValueList(plan, null, registerList);
                        break;
                    }

                    default:
                    {
                        var registerList = await _client.ReadHoldingRegisterAsync(plan.StartAddress, plan.Count, cancellationToken)
                            .ConfigureAwait(false);
                        RaiseLayoutPlanValueList(plan, null, registerList);
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // 单个读取计划失败只跳过它自己：曾用裸 foreach，任一计划抛异常
                // （如某段地址 PLC 回异常码 03）会中断整组本轮采集，同组后续
                // 全部点位本周期不刷新。IO 路径早已按此模式隔离，标签路径对齐。
                _log.LogWarning(exception, "读取计划 {Region} D{Start}x{Count} 执行失败，本周期跳过该计划",
                    plan.Area, plan.StartAddress, plan.Count);
            }
        }
    }

    // 解码值物理范围诊断与点位值发布见 PollingEngine.Publish.cs（partial）。

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
