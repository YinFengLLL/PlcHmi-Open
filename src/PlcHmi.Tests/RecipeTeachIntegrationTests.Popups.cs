using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PlcHmi.Business.Configuration;
using PlcHmi.Business.Services;
using PlcHmi.Core.Abstractions;

namespace PlcHmi.Tests;

/// <summary>
/// 可配置弹窗的端到端用例（<see cref="RecipeTeachIntegrationTest"/> 的 partial part）。
///
/// <para><b>为什么必须有这组用例：</b>弹窗"没弹出来"在上位机侧没有任何报错——
/// 现场只会看到"PLC 里 M 都闭合了，界面什么都不发生"。
/// 曾经的真实缺陷：监控服务等"连接状态已变更"事件才启动轮询，
/// 而服务第一次被创建时 PLC 往往已经连上，事件再也不会来，轮询永不启动。
/// 这组用例直接对着<b>虚拟 PLC 的线圈</b>跑完整链路（写线圈 → 轮询读到 → 上报触发），
/// 把这个次序陷阱钉死。</para>
/// </summary>
public partial class RecipeTeachIntegrationTest
{
    [Fact]
    public async Task TriggerCoilCloseShouldReportTrigger_DisconnectShouldRetract()
    {
        // 弹窗定义来自仓库真实配置 config/popups.json（演示弹窗：触发 M1100）
        var Definition = ReadDemoPopup();

        await using var provider = BuildRuntime();
        var hosting = provider.GetRequiredService<IHostedService>();
        await hosting.StartAsync(CancellationToken.None);

        try
        {
            var Client = provider.GetRequiredService<IModbusClient>();
            var Parser = provider.GetRequiredService<IAddressParser>();
            var MonitorControl = provider.GetRequiredService<PopupMonitorService>();

            Assert.Contains(MonitorControl.PopupCollection, Entry => Entry.Key == Definition.Key);
            Assert.Null(MonitorControl.CurrentTriggerKey);

            // 触发线圈地址必须能解析成 M 区线圈（写错地址的弹窗会被装载期警告跳过）
            Assert.True(Parser.TryParse(Definition.TriggerAddress, out var Address), "触发地址解析失败");
            Assert.NotNull(Address);

            // 1. 闭合触发线圈 → 应上报触发（真实关系：不做锁存）
            await Client.WriteSingleCoilAsync(Address!.ModbusAddress, true);

            await WaitUntilAsync(
                () => MonitorControl.CurrentTriggerKey == Definition.Key,
                $"{Definition.TriggerAddress} 闭合后上报弹窗 {Definition.Name}");

            // 2. 断开触发线圈 → 应立即收起
            await Client.WriteSingleCoilAsync(Address.ModbusAddress, false);

            await WaitUntilAsync(
                () => MonitorControl.CurrentTriggerKey is null,
                $"{Definition.TriggerAddress} 断开后收起弹窗");
        }
        finally
        {
            await hosting.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task PopupButtonShouldHoldDownSetReleaseReset()
    {
        var Definition = ReadDemoPopup();

        Assert.False(string.IsNullOrWhiteSpace(Definition.Button1Address), "演示弹窗必须配按钮一地址");

        await using var provider = BuildRuntime();
        var hosting = provider.GetRequiredService<IHostedService>();
        await hosting.StartAsync(CancellationToken.None);

        try
        {
            var Client = provider.GetRequiredService<IModbusClient>();
            var Parser = provider.GetRequiredService<IAddressParser>();
            var MonitorControl = provider.GetRequiredService<PopupMonitorService>();

            await WaitUntilAsync(() => Client.IsConnected, "虚拟 PLC 连接就绪");

            Assert.True(Parser.TryParse(Definition.Button1Address, out var buttonAddress), "按钮地址解析失败");
            Assert.NotNull(buttonAddress);

            // 按住 → 置位
            await MonitorControl.PressAsync(Definition, 0);

            var pressAfter = await Client.ReadCoilAsync(buttonAddress!.ModbusAddress, 1);
            Assert.True(pressAfter[0], "按住按钮应置位对应线圈");

            // 松开 → 复位（复归型：不保持）
            await MonitorControl.ReleaseAsync(Definition, 0);

            var releaseAfter = await Client.ReadCoilAsync(buttonAddress.ModbusAddress, 1);
            Assert.False(releaseAfter[0], "松开按钮应复位对应线圈");
        }
        finally
        {
            await hosting.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>取仓库演示配置里的演示弹窗（config/popups.json 的第一个条目）。</summary>
    private static PopupDefinition ReadDemoPopup()
    {
        var path = Path.Combine(ConfigPath.ConfigDirectory, "popups.json");

        Assert.True(File.Exists(path), $"缺少演示弹窗配置：{path}");

        var file = System.Text.Json.JsonSerializer.Deserialize<PopupFile>(
            File.ReadAllText(path),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(file);
        Assert.NotEmpty(file!.PopupCollection);

        return file.PopupCollection[0];
    }

    // ── 2026-10-17：按钮三种工作模式 ────────────────────────────────────

    /// <summary>置位 ON：点一次即置位并保持——松开不能再写回去。</summary>
    [Fact]
    public async Task PopupButton_SetONModeShouldPointOnceImmediatelyHold()
    {
        await using var provider = BuildRuntime();
        var hosting = provider.GetRequiredService<IHostedService>();
        await hosting.StartAsync(CancellationToken.None);

        try
        {
            var Client = provider.GetRequiredService<IModbusClient>();
            var MonitorControl = provider.GetRequiredService<PopupMonitorService>();

            var Definition = new PopupDefinition
            {
                Key = "test-on",
                Name = "测试置位",
                TriggerAddress = "M1100",
                Button1Name = "启动输送",
                Button1Address = "M5000",
                Button1Mode = PopupButtonMode.SetON,
            };

            await MonitorControl.PressAsync(Definition, 0);

            Assert.True((await Client.ReadCoilAsync(5000, 1))[0], "点一次应置位");

            await MonitorControl.ReleaseAsync(Definition, 0);

            Assert.True((await Client.ReadCoilAsync(5000, 1))[0],
                "置位 ON 模式：松开不得把刚置上的位抹掉（否则现场表现为'按钮按了没反应'）");
        }
        finally
        {
            await hosting.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>复位 OFF：点一次即复位并保持——松开不恢复。</summary>
    [Fact]
    public async Task PopupButton_ResetOFFModeShouldPointOnceImmediatelyResetAndHold()
    {
        await using var provider = BuildRuntime();
        var hosting = provider.GetRequiredService<IHostedService>();
        await hosting.StartAsync(CancellationToken.None);

        try
        {
            var Client = provider.GetRequiredService<IModbusClient>();
            var MonitorControl = provider.GetRequiredService<PopupMonitorService>();

            // 先让线圈处于置位状态（模拟 PLC 侧 / 之前操作留下的开启状态）
            await Client.WriteSingleCoilAsync(5001, true);

            var Definition = new PopupDefinition
            {
                Key = "test-off",
                Name = "测试复位",
                TriggerAddress = "M1100",
                Button1Name = "停止输送",
                Button1Address = "M5001",
                Button1Mode = PopupButtonMode.ResetOFF,
            };

            await MonitorControl.PressAsync(Definition, 0);

            Assert.False((await Client.ReadCoilAsync(5001, 1))[0], "点一次应复位");

            await MonitorControl.ReleaseAsync(Definition, 0);

            Assert.False((await Client.ReadCoilAsync(5001, 1))[0], "复位 OFF 模式：松开不得恢复置位");
        }
        finally
        {
            await hosting.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>复归型（默认）与另两种模式的写入时序必须不同——这条把三者的分工钉死。</summary>
    [Fact]
    public void PopupButtonMode_ThreeKindModeLanguageMeaningShouldMutuallyNotSame()
    {
        Assert.Equal(3, PopupDefinition.ModeOptions.Count);
        Assert.Contains("复归型", PopupDefinition.ModeOptions);
        Assert.Contains("置位 ON", PopupDefinition.ModeOptions);
        Assert.Contains("复位 OFF", PopupDefinition.ModeOptions);

        // 默认必须是复归型：老配置（JSON 里没有模式字段）升级后行为不变
        Assert.Equal(PopupButtonMode.ReturnType, new PopupDefinition().Button1Mode);
        Assert.Equal(PopupButtonMode.ReturnType, new PopupDefinition().Button2Mode);

        // 每种模式都要有可读的说明（弹窗按钮的 ToolTip 绑的是它）
        foreach (var mode in (PopupButtonMode[])[PopupButtonMode.ReturnType, PopupButtonMode.SetON, PopupButtonMode.ResetOFF])
        {
            Assert.False(string.IsNullOrWhiteSpace(PopupDefinition.ModeDescription(mode)));
        }
    }
}
