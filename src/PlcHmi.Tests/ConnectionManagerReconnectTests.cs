using Microsoft.Extensions.Logging.Abstractions;
using PlcHmi.Business.Services;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;
using PlcHmi.Tests.Fake;

namespace PlcHmi.Tests;

/// <summary>
/// 连接管理器断线重连的自动化验证。
///
/// <para><b>为什么补这一组：</b>指数退避逻辑此前只有人工推理，没有可执行断言
/// （见 <c>docs/01</c> 风险表的"断线后无法自愈 —— 待实测"）。
/// 断线自愈是无人值守工控现场的底线能力，必须由测试锁定，而不是靠"看着像对"。</para>
///
/// <para>退避间隔统一调小（20ms / 80ms），使测试在数百毫秒内完成，不真等 1s～30s。</para>
/// </summary>
public class ConnectManagerReconnectTest
{
    private static PlcConnectionOptions FastOptions(bool AutoReconnect, int initialMilliseconds = 20, int maxMilliseconds = 80) => new()
    {
        AutoReconnect = AutoReconnect,
        ReconnectInitialDelayMilliseconds = initialMilliseconds,
        ReconnectMaxDelayMilliseconds = maxMilliseconds,
        HeartbeatIntervalMilliseconds = 1000,   // 本组测试不走到心跳：连接始终失败或刚成功即结束
    };

    /// <summary>线程安全的状态记录器（StateChanged 在后台线程触发）。</summary>
    private sealed class StateRecorder
    {
        private readonly object _sync = new();
        private readonly List<ConnectionState> _state = new();

        public void Add(ConnectionState StateValue)
        {
            lock (_sync)
            {
                _state.Add(StateValue);
            }
        }

        public IReadOnlyList<ConnectionState> Snapshot()
        {
            lock (_sync)
            {
                return _state.ToList();
            }
        }
    }

    private static async Task<bool> WaitStateValueAsync(
        ConnectionManager Manager, ConnectionState expected, int TimeoutMilliseconds = 5000)
    {
        var truncateStopTime = Environment.TickCount64 + TimeoutMilliseconds;

        while (Environment.TickCount64 < truncateStopTime)
        {
            if (Manager.State == expected)
            {
                return true;
            }

            await Task.Delay(10);
        }

        return Manager.State == expected;
    }

    [Fact]
    public async Task DisconnectLineReconnect_FirstAsConnecting_FailedAfterConvertReconnectingAndBackoffPerItemStepPullLong()
    {
        await using var Client = new FakeModbusClient();
        Client.FailedNextConnect(int.MaxValue);   // 全程失败，只看退避节奏

        var recorder = new StateRecorder();
        var Options = FastOptions(AutoReconnect: true);

        await using var Manager = new ConnectionManager(
            Client, Options, NullLogger<ConnectionManager>.Instance);
        Manager.StateChanged += (_, StateValue) => recorder.Add(StateValue);

        await Manager.StartAsync();
        await Task.Delay(400);
        await Manager.StopAsync();

        var tryCount = Client.ConnectAttemptCount;
        Assert.True(tryCount.Count >= 3, $"应至少重试 3 次，实际 {tryCount.Count} 次");

        var State = recorder.Snapshot();
        Assert.Equal(ConnectionState.Connecting, State[0]);
        Assert.Contains(ConnectionState.Reconnecting, State);
        Assert.DoesNotContain(ConnectionState.Connected, State);

        var missingPort = new List<double>();
        for (var Entry = 1; Entry < tryCount.Count; Entry++)
        {
            missingPort.Add((tryCount[Entry] - tryCount[Entry - 1]).TotalMilliseconds);
        }

        var maxInterval = missingPort.Max();

        // 退避确实在拉长（固定间隔重试会让最大间隔停在初始值附近）
        Assert.True(maxInterval >= Options.ReconnectInitialDelayMilliseconds * 1.5,
            $"退避未生效：最大间隔仅 {maxInterval:F0}ms，初始 {Options.ReconnectInitialDelayMilliseconds}ms");

        // 且没有突破上限（上限 80ms 时不应出现几百毫秒的间隔）
        Assert.True(maxInterval <= Options.ReconnectMaxDelayMilliseconds * 2.0,
            $"退避超出上限：最大间隔 {maxInterval:F0}ms，上限 {Options.ReconnectMaxDelayMilliseconds}ms");
    }

    [Fact]
    public async Task DisconnectLineReconnect_ConnectContinueFailedAfterRestoreShouldBackToConnected()
    {
        await using var Client = new FakeModbusClient();
        Client.FailedNextConnect(2);   // 前两次失败，第三次成功

        var recorder = new StateRecorder();
        var Options = FastOptions(AutoReconnect: true, initialMilliseconds: 20, maxMilliseconds: 200);

        await using var Manager = new ConnectionManager(
            Client, Options, NullLogger<ConnectionManager>.Instance);
        Manager.StateChanged += (_, StateValue) => recorder.Add(StateValue);

        await Manager.StartAsync();

        var Connected = await WaitStateValueAsync(Manager, ConnectionState.Connected, 5000);
        Assert.True(Connected, "两次失败后第三次应连上，但始终未进入 Connected");

        Assert.Equal(3, Client.ConnectAttemptQuantity);

        var State = recorder.Snapshot();
        Assert.Equal(ConnectionState.Connecting, State[0]);
        Assert.Contains(ConnectionState.Reconnecting, State);
        Assert.Equal(ConnectionState.Connected, State[^1]);

        await Manager.StopAsync();
        Assert.Equal(ConnectionState.Disconnected, Manager.State);
    }

    [Fact]
    public async Task DisconnectLineReconnect_CloseAutoReconnectWhenShouldEnterFaultStateAndOnlyTryOnce()
    {
        await using var Client = new FakeModbusClient();
        Client.FailedNextConnect(5);

        var Options = FastOptions(AutoReconnect: false);

        await using var Manager = new ConnectionManager(
            Client, Options, NullLogger<ConnectionManager>.Instance);

        await Manager.StartAsync();

        var Fault = await WaitStateValueAsync(Manager, ConnectionState.Fault, 3000);
        Assert.True(Fault, "关闭自动重连后应进入 Faulted");

        Assert.Equal(1, Client.ConnectAttemptQuantity);

        // 再等一会，确认没有偷偷重试
        await Task.Delay(200);
        Assert.Equal(1, Client.ConnectAttemptQuantity);

        await Manager.StopAsync();
        Assert.Equal(ConnectionState.Disconnected, Manager.State);
    }

    [Fact]
    public async Task DisconnectLineReconnect_StopWhenShouldBeAbleToDryCleanExit()
    {
        await using var Client = new FakeModbusClient();
        Client.FailedNextConnect(int.MaxValue);

        var Options = FastOptions(AutoReconnect: true);

        await using var Manager = new ConnectionManager(
            Client, Options, NullLogger<ConnectionManager>.Instance);

        await Manager.StartAsync();
        await Task.Delay(60);

        using var CancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var Stop = Manager.StopAsync(CancellationTokenSource.Token);

        var completed = await Task.WhenAny(Stop, Task.Delay(TimeSpan.FromSeconds(4))) == Stop;
        Assert.True(completed, "StopAsync 未在预期时间内返回，重连循环可能未响应取消");

        await Stop;
        Assert.Equal(ConnectionState.Disconnected, Manager.State);
    }
}
