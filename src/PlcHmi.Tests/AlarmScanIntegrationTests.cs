using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PlcHmi.Business;
using PlcHmi.Business.Configuration;
using PlcHmi.Business.Services;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Tests;

/// <summary>
/// 报警扫描的端到端测试。
///
/// <para>验证「PLC 某个位为 1」→「界面显示配置好的报警文本」这条完整链路，
/// 以及定义校验是否拦住了不该用的地址。</para>
/// </summary>
public class AlarmScanIntegrationTest
{
    private static ServiceProvider BuildRuntime()
    {
        var Settings = AppSettingsLoader.Load(out _);
        var RecipeConfig = RecipeConfigLoader.Load(out _);

        Settings.PlcConnection.UseSimulator = true;
        Settings.Polling.FastIntervalMilliseconds = 50;
        Settings.Polling.SlowIntervalMilliseconds = 100;
        Settings.Polling.EventIntervalMilliseconds = 100;

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging();
        serviceCollection.AddPlcHmiService(Settings.PlcConnection, Settings.Polling, RecipeConfig);

        // 本组用例不验证权限边界（那由 PermissionGuardTests 覆盖），以受信任身份运行
        serviceCollection.AddSingleton<IPermissionGuard, AllowAllPermissionGuard>();

        return serviceCollection.BuildServiceProvider(validateScopes: true);
    }

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

    /// <summary>
    /// 启动全部宿主服务。
    /// <para>注意：报警扫描是独立的 hosted service，只启动 <c>GetRequiredService&lt;IHostedService&gt;()</c>
    /// 拿到的那个（运行时服务）不会把它带起来。</para>
    /// </summary>
    private static async Task StartAllAsync(IReadOnlyList<IHostedService> serviceCollection)
    {
        foreach (var serviceInstance in serviceCollection)
        {
            await serviceInstance.StartAsync(CancellationToken.None);
        }

        // 给扫描器留出首轮时间
        await Task.Delay(700);
    }

    private static async Task StopAllAsync(IReadOnlyList<IHostedService> serviceCollection)
    {
        foreach (var serviceInstance in serviceCollection)
        {
            await serviceInstance.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AlarmScanShouldPlcBitMapAsConfigGoodAlarmText()
    {
        await using var provider = BuildRuntime();
        var hosting = provider.GetServices<IHostedService>().ToList();
        await StartAllAsync(hosting);

        try
        {
            var alarmList = provider.GetRequiredService<IAlarmService>();
            var definitionService = provider.GetRequiredService<IAlarmDefinitionService>();
            var Client = provider.GetRequiredService<IModbusClient>();

            // 现场出厂配置里启用的三条报警都是 PLC 程序自己写的位（M501–M503），
            // 而模拟器每个周期都会重写这一段 —— 本用例自带一条 M600 的测试报警：
            // M600 不在模拟器的报警模拟区，写进去不会被覆盖，链路照样是"PLC 位置位 → 界面文本"。
            definitionService.Add(new AlarmDefinition
            {
                Id = "A900",
                Message = "急停（测试）",
                Level = AlarmLevel.Critical,
                Address = "M600",
                Bit = -1,
                Enabled = true,
            });

            // 等扫描跑起来
            await WaitUntilAsync(() => true, "扫描启动");
            await Task.Delay(700);

            await Client.WriteSingleCoilAsync(600, true);

            await WaitUntilAsync(
                () => alarmList.ActiveAlarm.Any(item => item.Code == "A900"),
                "测试报警产生");

            var Triggered = alarmList.ActiveAlarm.First(item => item.Code == "A900");
            Assert.Contains("急停", Triggered.Message);
            Assert.Equal(AlarmLevel.Critical, Triggered.Level);

            // 电平报警：位归 0 即消除，并进入历史
            await Client.WriteSingleCoilAsync(600, false);

            await WaitUntilAsync(
                () => !alarmList.ActiveAlarm.Any(item => item.Code == "A900"),
                "测试报警消除");

            Assert.Contains(alarmList.History, item => item.Code == "A900");
        }
        finally
        {
            await StopAllAsync(hosting);
        }
    }

    [Fact]
    public async Task AlarmAddressOnlyCanAtD550ToD554OrM500ToM600()
    {
        await using var provider = BuildRuntime();
        var hosting = provider.GetServices<IHostedService>().ToList();
        await StartAllAsync(hosting);

        try
        {
            var definitionList = provider.GetRequiredService<IAlarmDefinitionService>();

            // 区域外：应一律被拒绝
            foreach (var addressValue in new[] { "X0", "Y0", "D100", "D2000", "M100", "M700" })
            {
                var Invalid = new Core.Models.AlarmDefinition
                {
                    Id = "T001", Message = "越界地址测试", Address = addressValue, Bit = -1,
                };

                var resultValue = definitionList.Validate(Invalid);

                Assert.False(resultValue.IsValid, $"地址 {addressValue} 本应被拒绝");
                Assert.Contains(resultValue.ErrorCollection, eventArgs => eventArgs.Contains("必须落在"));
            }

            // M500–M600：合法
            foreach (var addressValue in new[] { "M500", "M550", "M600" })
            {
                var Valid = new Core.Models.AlarmDefinition
                {
                    Id = "T001", Message = "合法地址测试", Address = addressValue, Bit = -1,
                };

                Assert.True(definitionList.Validate(Valid).IsValid, $"地址 {addressValue} 本应通过");
            }

            // D550–D554 必须指定位号 —— 不然一个 16 位的字只能放一条报警
            var excludeBit = new Core.Models.AlarmDefinition
            {
                Id = "T001", Message = "D 区未指定位号", Address = "D550", Bit = -1,
            };

            var dResult = definitionList.Validate(excludeBit);
            Assert.False(dResult.IsValid);
            Assert.Contains(dResult.ErrorCollection, eventArgs => eventArgs.Contains("必须指定位号"));

            // 指定位号后合法
            var Bit = excludeBit.Clone();
            Bit.Bit = 3;
            Assert.True(definitionList.Validate(Bit).IsValid);

            // 边界值：D554.15 应合法，D555.0 应被拒
            var dUpperLimit = Bit.Clone();
            dUpperLimit.Address = "D554";
            dUpperLimit.Bit = 15;
            Assert.True(definitionList.Validate(dUpperLimit).IsValid);

            var dBeyondBoundary = Bit.Clone();
            dBeyondBoundary.Address = "D555";
            Assert.False(definitionList.Validate(dBeyondBoundary).IsValid);
        }
        finally
        {
            await StopAllAsync(hosting);
        }
    }

    [Fact]
    public async Task AlarmDefinitionShouldValidateTextAndBitNumberRange()
    {
        await using var provider = BuildRuntime();
        var hosting = provider.GetServices<IHostedService>().ToList();
        await StartAllAsync(hosting);

        try
        {
            var definitionList = provider.GetRequiredService<IAlarmDefinitionService>();

            var badValue = new Core.Models.AlarmDefinition
            {
                Id = "T002", Message = string.Empty, Address = "M500", Bit = 99,
            };

            var resultValue = definitionList.Validate(badValue);

            Assert.False(resultValue.IsValid);
            Assert.Contains(resultValue.ErrorCollection, eventArgs => eventArgs.Contains("报警文本"));
            Assert.Contains(resultValue.ErrorCollection, eventArgs => eventArgs.Contains("位号"));
        }
        finally
        {
            await StopAllAsync(hosting);
        }
    }

    [Fact]
    public async Task DefaultAlarmConfigShouldFollowProcessIndexOneStartLoad()
    {
        await using var provider = BuildRuntime();
        var hosting = provider.GetServices<IHostedService>().ToList();
        await StartAllAsync(hosting);

        try
        {
            var definitionList = provider.GetRequiredService<IAlarmDefinitionService>();

            // alarms.json 由测试项目随 config 一起复制，应至少有一条
            Assert.NotEmpty(definitionList.DefinitionCollection);
            Assert.Contains(definitionList.DefinitionCollection, EntryItem => EntryItem.Id == "A001");
        }
        finally
        {
            await StopAllAsync(hosting);
        }
    }
}
