using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PlcHmi.Business;
using PlcHmi.Business.Configuration;
using PlcHmi.Business.Services;
using PlcHmi.Communication.Simulation;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;

namespace PlcHmi.Tests;

/// <summary>
/// 端到端集成测试：用虚拟 PLC 跑通 <b>配置加载 → DI 装配 → 连接 → 轮询 → 业务服务</b> 的完整链路。
///
/// <para>这一层测试的价值在于它读的是真实的 <c>config/*.json</c>——点位表改了、地址映射改错了、
/// 模拟器区写错了，这里都会立刻红。</para>
/// </summary>
public class EndToEndSimulationTest
{
    /// <summary>按真实配置文件装配一套模拟环境。</summary>
    private static ServiceProvider BuildRuntime()
    {
        var Settings = AppSettingsLoader.Load(out var error);
        Assert.Null(error);

        // 仓库配置已切到真实 PLC 模式（现场部署）；与 AlarmScanIntegrationTests 同款——
        // 强制模拟器，测试与仓库配置解耦，永远不可能碰到真实 PLC。
        Settings.PlcConnection.UseSimulator = true;

        // 测试里把周期调快，缩短等待
        Settings.Polling.FastIntervalMilliseconds = 50;
        Settings.Polling.SlowIntervalMilliseconds = 100;
        Settings.Polling.EventIntervalMilliseconds = 100;

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging();
        serviceCollection.AddPlcHmiService(Settings.PlcConnection, Settings.Polling, RecipeConfigLoader.Load(out _));

        // 本组用例不验证权限边界（那由 PermissionGuardTests 覆盖），以受信任身份运行
        serviceCollection.AddSingleton<IPermissionGuard, AllowAllPermissionGuard>();

        return serviceCollection.BuildServiceProvider(validateScopes: true);
    }

    /// <summary>轮询等待条件成立，超时则断言失败。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string Description, int TimeoutMilliseconds = 8000)
    {
        var truncateStopTime = Environment.TickCount64 + TimeoutMilliseconds;

        while (Environment.TickCount64 < truncateStopTime)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"等待超时：{Description}");
    }

    [Fact]
    public async Task EndToEnd_ShouldBeAbleToConnectVirtualPlcAndReadToPoint()
    {
        await using var provider = BuildRuntime();
        var hosting = provider.GetRequiredService<IHostedService>();

        await hosting.StartAsync(CancellationToken.None);

        try
        {
            var Connect = provider.GetRequiredService<IConnectManager>();
            var ValueList = provider.GetRequiredService<ITagValueService>();

            await WaitUntilAsync(() => Connect.State == ConnectionState.Connected, "连接虚拟 PLC");

            // tags.json 里的点位应该被采到
            await WaitUntilAsync(() => ValueList.GetNewValue("RunStatus").IsValid, "采到 RunStatus");
            Assert.True(ValueList.GetNewValue("RunStatus").IsValid);

            // 上电后状态字为 0（空闲中），映射后应为 空闲——这条断言把“状态字编号约定”钉住
            var Device = provider.GetRequiredService<IDeviceService>();
            await WaitUntilAsync(() => Device.State == DeviceState.Idle, "设备进入空闲中状态");
            Assert.Equal(DeviceState.Idle, Device.State);
        }
        finally
        {
            await hosting.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task EndToEnd_SendStartCommandAfterDeviceShouldEnterRunning()
    {
        await using var provider = BuildRuntime();
        var hosting = provider.GetRequiredService<IHostedService>();

        await hosting.StartAsync(CancellationToken.None);

        try
        {
            var Connect = provider.GetRequiredService<IConnectManager>();
            var Device = provider.GetRequiredService<IDeviceService>();

            await WaitUntilAsync(() => Connect.State == ConnectionState.Connected, "连接虚拟 PLC");

            await Device.StartAsync();

            // 命令脉冲 → 虚拟 PLC 置运行 → 状态字 1 → 映射为 Running
            await WaitUntilAsync(() => Device.State == DeviceState.Running, "设备进入运行中");
            Assert.Equal(DeviceState.Running, Device.State);

            // 运行后节拍与轴位置应开始更新
            await WaitUntilAsync(() => Device.WeekPeriodTimeSeconds > 0, "节拍开始更新");
            Assert.True(Device.WeekPeriodTimeSeconds > 0, "节拍应大于 0");
        }
        finally
        {
            await hosting.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task EndToEnd_EmergencyStopAfterDeviceStateShouldAsAlarm()
    {
        await using var provider = BuildRuntime();
        var hosting = provider.GetRequiredService<IHostedService>();

        await hosting.StartAsync(CancellationToken.None);

        try
        {
            var Connect = provider.GetRequiredService<IConnectManager>();
            var Device = provider.GetRequiredService<IDeviceService>();

            await WaitUntilAsync(() => Connect.State == ConnectionState.Connected, "连接虚拟 PLC");

            // 急停是物理按钮，上位机已不再提供急停命令（软件不能进安全回路）。
            // 这里直接置位 PLC 内部的 M204 急停信号位（电平保持），模拟“现场急停被按下”——
            // 模拟器状态机会把它锁存为报警态并持续输出状态字 4（报警中），
            // 借此验证上位机仍能正确读到并进入报警态（界面显示“报警中”）。
            var memory = provider.GetRequiredService<VirtualPlcDevice>().memory;
            memory.RunAloneOccupy(() => memory.UnsafeSetCoil(204, true));

            await WaitUntilAsync(() => Device.State == DeviceState.Alarming, "设备进入报警中状态");
            Assert.Equal(DeviceState.Alarming, Device.State);
        }
        finally
        {
            await hosting.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task EndToEnd_IOPointShouldBeSamplingAndApplyInvert()
    {
        await using var provider = BuildRuntime();
        var hosting = provider.GetRequiredService<IHostedService>();

        await hosting.StartAsync(CancellationToken.None);

        try
        {
            var Connect = provider.GetRequiredService<IConnectManager>();
            var IoPointTable = provider.GetRequiredService<IIoPointService>();

            await WaitUntilAsync(() => Connect.State == ConnectionState.Connected, "连接虚拟 PLC");

            // X10 料仓满料：模拟器写 0%10<7 → 原始位 1，未反相 → 逻辑 ON
            await WaitUntilAsync(() => IoPointTable.GetState("X10").IsOpenStart, "X10 变为 ON");

            // X7 急停按钮为常闭点（inverted=true）：未急停时原始位 1 → 逻辑 OFF
            await WaitUntilAsync(
                () => IoPointTable.GetState("X7").timeStamp != DateTime.MinValue,
                "X7 已被采集");

            Assert.False(IoPointTable.GetState("X7").IsOpenStart, "未急停时 X7 逻辑值应为 OFF");
            Assert.True(IoPointTable.GetState("X10").IsOpenStart, "X10 料仓满料应为 ON");
        }
        finally
        {
            await hosting.StopAsync(CancellationToken.None);
        }
    }
}
