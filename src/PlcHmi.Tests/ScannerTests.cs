using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using PlcHmi.Business;
using PlcHmi.Business.Configuration;
using PlcHmi.Business.Services;
using PlcHmi.Communication.Modbus;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Models;
using PlcHmi.Tests.Fake;

namespace PlcHmi.Tests;

/// <summary>
/// 扫码枪功能的回归（2026-11-12）。
///
/// <para><b>守的是什么：</b>① 扫码枪按 CR / CRLF / LF 三种结尾都能切出完整帧（现场没人会去翻说明书）；
/// ② 手动型收到条码即写"扫码成功信号寄存器"、并把条码记为工单号；
/// ③ 工单号只存内存（不写进历史文件）；④ 两个寄存器地址由服务层校验，配错当场报错。</para>
/// </summary>
[Collection("config-files")]
public class ScannerTest
{
    private static ServiceProvider BuildRuntime()
    {
        var Settings = AppSettingsLoader.Load(out var SettingsError);
        var RecipeConfig = RecipeConfigLoader.Load(out var recipeError);

        Assert.Null(SettingsError);
        Assert.Null(recipeError);

        Settings.PlcConnection.UseSimulator = true;
        Settings.Polling.FastIntervalMilliseconds = 50;
        Settings.Polling.SlowIntervalMilliseconds = 100;
        Settings.Polling.EventIntervalMilliseconds = 100;

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging();
        serviceCollection.AddPlcHmiService(Settings.PlcConnection, Settings.Polling, RecipeConfig);
        serviceCollection.AddSingleton<IPermissionGuard, AllowAllPermissionGuard>();

        return serviceCollection.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public void ThreeKindLineTerminatorBothShouldSwitchOutBarcodeFrame()
    {
        var buffer = new StringBuilder();

        Assert.Equal(["AB12"], ScanFrameParser.Slice("AB12\r\n", buffer));
        Assert.Equal(["K7-9"], ScanFrameParser.Slice("K7-9\n", buffer));
        Assert.Equal(["X001"], ScanFrameParser.Slice("X001\r", buffer));

        // 半帧：先到的部分不吐出，等结尾符
        Assert.Empty(ScanFrameParser.Slice("PART", buffer));
        Assert.Equal(["PART-2"], ScanFrameParser.Slice("-2\r\n", buffer));

        // 一块里两条
        Assert.Equal(["A1", "B2"], ScanFrameParser.Slice("A1\r\nB2\r\n", buffer));
    }

    [Fact]
    public async Task ManualKindCollectToBarcodeShouldWriteSuccessSignalAndRecordHoldWorkOrder()
    {
        await using var provider = BuildRuntime();
        var hosting = provider.GetRequiredService<IHostedService>();
        await hosting.StartAsync(CancellationToken.None);

        try
        {
            var Client = provider.GetRequiredService<IModbusClient>();
            var Parser = provider.GetRequiredService<IAddressParser>();

            // 未启用：只测处理链，不去打开串口
            var Config = new ScannerConfig
            {
                Enabled = false,
                WorkMode = ScanWorkMode.ManualKind,
                ScanSuccessRegister = "D4050",
                ScanTriggerRegister = "D4010",
            };

            using var serviceInstance = new ScannerService(
                Config, Parser, Client, new AllowAllPermissionGuard(),
                NullLogger<ScannerService>.Instance);

            var eventTrigger = false;
            serviceInstance.workOrderChanged += (_, _) => eventTrigger = true;

            serviceInstance.InjectScanResult("  WO-2026-11-12-001  ");

            Assert.Equal("WO-2026-11-12-001", serviceInstance.WorkOrder);
            Assert.True(eventTrigger);

            // 写成功信号是后台任务：等它落进 PLC
            var truncateStop = Environment.TickCount64 + 3000;

            while (Environment.TickCount64 < truncateStop)
            {
                var Value = await Client.ReadHoldingRegisterAsync(4050, 1);

                if (Value.Length > 0 && Value[0] == 1)
                {
                    return;
                }

                await Task.Delay(50);
            }

            Assert.Fail("超时：扫码成功后应把 D4050 写成 1");
        }
        finally
        {
            await hosting.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task TriggerKindTimeoutNotScanToShouldWriteFailedSignalAndLateToBarcodeBeIgnored()
    {
        await using var provider = BuildRuntime();
        var hosting = provider.GetRequiredService<IHostedService>();
        await hosting.StartAsync(CancellationToken.None);

        try
        {
            var Client = provider.GetRequiredService<IModbusClient>();
            var Parser = provider.GetRequiredService<IAddressParser>();

            using var serviceInstance = BuildTriggerKindService(Parser, Client, TimeoutMilliseconds: 300);
            await serviceInstance.RestartAsync(CancellationToken.None);

            // PLC 请求扫一件：过了 300 ms 还没码，就该判失败
            await Client.WriteSingleRegisterAsync(4010, 1);

            Assert.True(
                await WaitAsync(async () => (await ReadValue(Client, 4050)) == 2),
                "超时未扫到时应把 D4050 写成 2");

            Assert.Equal(0, await ReadValue(Client, 4010));

            // 晚到（窗口已关）的条码不能翻案：PLC 那边已经按失败处理了
            serviceInstance.InjectScanResult("LATE-CODE");

            Assert.Equal(string.Empty, serviceInstance.WorkOrder);
            Assert.Equal(2, await ReadValue(Client, 4050));
        }
        finally
        {
            await hosting.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task TriggerKindWaitCodeWindowInnerScanToCodeShouldWriteSuccessSignal()
    {
        await using var provider = BuildRuntime();
        var hosting = provider.GetRequiredService<IHostedService>();
        await hosting.StartAsync(CancellationToken.None);

        try
        {
            var Client = provider.GetRequiredService<IModbusClient>();
            var Parser = provider.GetRequiredService<IAddressParser>();

            using var serviceInstance = BuildTriggerKindService(Parser, Client, TimeoutMilliseconds: 5000);
            await serviceInstance.RestartAsync(CancellationToken.None);

            await Client.WriteSingleRegisterAsync(4010, 1);

            // 触发被应答（写 0）之后窗口才开：等它开好再"扫到"
            Assert.True(
                await WaitAsync(async () => (await ReadValue(Client, 4010)) == 0),
                "触发寄存器应被上位机写 0 应答");

            await Task.Delay(100);

            serviceInstance.InjectScanResult("  WO-TRIGGER-001  ");

            Assert.Equal("WO-TRIGGER-001", serviceInstance.WorkOrder);
            Assert.True(
                await WaitAsync(async () => (await ReadValue(Client, 4050)) == 1),
                "窗口内扫到码应把 D4050 写成 1");
        }
        finally
        {
            await hosting.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// 触发型服务实例。
    /// <para>故意用不存在的串口：<c>异步重启</c> 会真的去开一次通道，打不开就只留日志，
    /// 触发监听照常起来（现场也是这个行为：扫码枪掉了不该让 PLC 握手跟着停）。</para>
    /// </summary>
    private static ScannerService BuildTriggerKindService(IAddressParser Parser, IModbusClient Client, int TimeoutMilliseconds)
    {
        var Config = new ScannerConfig
        {
            Enabled = true,
            CommunicationMode = ScanCommunicationMode.SerialPort,
            SerialPortName = "COM98",
            WorkMode = ScanWorkMode.TriggerKind,
            ScanSuccessRegister = "D4050",
            ScanTriggerRegister = "D4010",
            PollingIntervalMilliseconds = 100,
            TimeoutMilliseconds = TimeoutMilliseconds,
            TriggerCommand = string.Empty,
            ReleaseCommand = string.Empty,
        };

        return new ScannerService(Config, Parser, Client, new AllowAllPermissionGuard(),
            NullLogger<ScannerService>.Instance);
    }

    private static async Task<ushort> ReadValue(IModbusClient Client, int Address)
    {
        var Value = await Client.ReadHoldingRegisterAsync(Address, 1);
        return Value.Length > 0 ? Value[0] : (ushort)0;
    }

    private static async Task<bool> WaitAsync(Func<Task<bool>> condition, int TimeoutMilliseconds = 5000)
    {
        var truncateStop = Environment.TickCount64 + TimeoutMilliseconds;

        while (Environment.TickCount64 < truncateStop)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return false;
    }

    [Fact]
    public void WorkOrderShouldNotWriteEnterHistoryRecordFile()
    {
        var Entry = new HistoryRecordEntry
        {
            Timestamp = new DateTime(2026, 11, 12, 9, 0, 0),
            NumericValueCollection = [1, 2],
            WorkOrder = "WO-SECRET",
        };

        var JSON = JsonSerializer.Serialize(Entry, JsonConfig.Write);

        Assert.DoesNotContain("工单号", JSON, StringComparison.Ordinal);
        Assert.DoesNotContain("WO-SECRET", JSON, StringComparison.Ordinal);

        // 内存里仍在（界面用它显示列）
        Assert.Equal("WO-SECRET", Entry.Clone().WorkOrder);
    }

    [Fact]
    public async Task SaveSetShouldRejectInvalidRegisterAndNotPersist()
    {
        var setPath = ConfigPath.AppSettings;
        var Original = await File.ReadAllTextAsync(setPath);

        try
        {
            await using var provider = BuildRuntime();
            var serviceInstance = provider.GetRequiredService<ScannerService>();

            var candidate = serviceInstance.Config.Clone();
            candidate.ScanSuccessRegister = "M100";   // 位元件，不能当信号寄存器

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => serviceInstance.SaveSetAsync(candidate));

            // 文件没被改
            Assert.Equal(Original, await File.ReadAllTextAsync(setPath));

            // 两个寄存器填同一个地址也要拒绝
            candidate.ScanSuccessRegister = "D4050";
            candidate.ScanTriggerRegister = "D4050";

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => serviceInstance.SaveSetAsync(candidate));
        }
        finally
        {
            await File.WriteAllTextAsync(setPath, Original);
        }
    }

    [Fact]
    public async Task SaveValidSetShouldPersistAndCanReLoad()
    {
        var setPath = ConfigPath.AppSettings;
        var Original = await File.ReadAllTextAsync(setPath);

        try
        {
            await using var provider = BuildRuntime();
            var serviceInstance = provider.GetRequiredService<ScannerService>();

            var candidate = serviceInstance.Config.Clone();
            candidate.Enabled = true;

            // 故意用不存在的串口：保存后会真的去开一次通道，串口打不开立刻返回，
            // 不会像 TCP 那样等 3 秒，也不占用任何网络端口（避免和其它用例抢资源）
            candidate.CommunicationMode = ScanCommunicationMode.SerialPort;
            candidate.SerialPortName = "COM99";
            candidate.BaudRate = 19200;
            candidate.WorkMode = ScanWorkMode.TriggerKind;
            candidate.ScanSuccessRegister = "D4052";
            candidate.ScanTriggerRegister = "D4012";
            candidate.TriggerCommand = "TRIGGER";
            candidate.ReleaseCommand = "RELEASE";

            await serviceInstance.SaveSetAsync(candidate);

            var reloaded = AppSettingsLoader.Load(out var error);
            Assert.Null(error);
            Assert.True(reloaded.Scanner.Enabled);
            Assert.Equal(ScanCommunicationMode.SerialPort, reloaded.Scanner.CommunicationMode);
            Assert.Equal("COM99", reloaded.Scanner.SerialPortName);
            Assert.Equal(19200, reloaded.Scanner.BaudRate);
            Assert.Equal(ScanWorkMode.TriggerKind, reloaded.Scanner.WorkMode);
            Assert.Equal("D4052", reloaded.Scanner.ScanSuccessRegister);
            Assert.Equal("D4012", reloaded.Scanner.ScanTriggerRegister);
            Assert.Equal("TRIGGER", reloaded.Scanner.TriggerCommand);

            // 运行配置同步（后续写 PLC 用的就是这份快照）
            Assert.Equal("D4052", serviceInstance.Config.ScanSuccessRegister);
        }
        finally
        {
            await File.WriteAllTextAsync(setPath, Original);
        }
    }
}
