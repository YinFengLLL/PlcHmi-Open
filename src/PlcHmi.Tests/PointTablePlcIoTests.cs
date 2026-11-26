using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PlcHmi.Business;
using PlcHmi.Business.Configuration;
using PlcHmi.Business.Services;
using PlcHmi.Communication.Modbus;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Tests;

/// <summary>
/// 点表的 PLC 侧 IO：2026-10-21 的两次现场改动。
///
/// <para><b>①「读取点表」（按需读取）：</b>机种区段 D2500–D3999 不再参与周期采集，
/// 要看 PLC 现状必须显式读取；读取只刷新内存，不写 PLC、不写机种快照。</para>
///
/// <para><b>②「参数写入」（原「下发点表」）：</b>把上位机当前 D2500–D3999 整段写进 PLC，
/// 并同时触发机种保存（等同「下发机种 + 保存机种」）。</para>
///
/// <para>单独成文件：<see cref="PointTableServiceIntegrationTest"/> 已接近 400 行规模门禁。</para>
/// </summary>
[Collection("config-files")]
public class PointTablePlcReadWriteTest
{
    private const int dispenseInstructionCodeBaseAddress = 3400;
    private const int dispenseXBaseAddress = 2700;
    private const int dispenseStartZBaseAddress = 2900;

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

    private static async Task<float> ReadFloatAsync(IModbusClient Client, int addressValue)
    {
        var registerList = await Client.ReadHoldingRegisterAsync(addressValue, 2);
        return (float)RegisterCodec.Decode(
            TagDataType.Float32, registerList.AsSpan(0, 2), WordOrder.LittleEndian)!;
    }

    /// <summary>
    /// 机种区段不再自动回读之后，点表改为按「读取点表」显式读取：
    /// 要能拿到 PLC 里的真实值（含指令码），并且<b>只读不写</b>。
    /// </summary>
    [Fact]
    public async Task ReadPointTableShouldReadBackPlcCurrentValueAndNotWritePlc()
    {
        await using var provider = BuildRuntime();
        var hosting = provider.GetRequiredService<IHostedService>();
        await hosting.StartAsync(CancellationToken.None);

        try
        {
            var Client = provider.GetRequiredService<IModbusClient>();
            var PointTable = provider.GetRequiredService<IPointTableService>();

            // PLC 里放一组"现场实际值"（与机种快照无关）
            await Client.WriteMultiRegisterAsync(
                dispenseXBaseAddress, RegisterCodec.Encode(TagDataType.Float32, 12.34, WordOrder.LittleEndian));
            await Client.WriteMultiRegisterAsync(
                dispenseStartZBaseAddress, RegisterCodec.Encode(TagDataType.Float32, 56.78, WordOrder.LittleEndian));
            await Client.WriteMultiRegisterAsync(dispenseInstructionCodeBaseAddress, [3]);

            // 内存里先放点别的值，读取后必须被 PLC 值覆盖
            PointTable.SetPointValue("点胶", 0, "X", 1);
            PointTable.SetPointValue("点胶", 0, "指令码", 0);

            await PointTable.ReadPointTableAsync("点胶");

            var rowItem = PointTable.CurrentPointTable("点胶")[0];
            Assert.Equal(12.34, rowItem.Take("X"), 2);
            Assert.Equal(56.78, rowItem.Take("Z"), 2);
            Assert.Equal(3d, rowItem.Take("指令码"));

            // 只读不写：PLC 侧的值原封不动
            Assert.Equal(12.34f, await ReadFloatAsync(Client, dispenseXBaseAddress), 2);
            Assert.Equal(3, (await Client.ReadHoldingRegisterAsync(dispenseInstructionCodeBaseAddress, 1))[0]);
        }
        finally
        {
            await hosting.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// 示教页「下发点表」改为「参数写入」：把上位机当前的 D2500–D3999 整段写进 PLC，
    /// 并同时触发机种保存。
    ///
    /// <para>要点：① 界面上刚改（还没下发）的坐标也要跟着写下去；
    /// ② 整段写入会先清零，PLC 里不属于当前机种的残留字必须消失；
    /// ③ 机种号写入 PLC；④ 写完后机种快照已保存（切页/重载仍能看到）。</para>
    /// </summary>
    [Fact]
    public async Task ParameterWriteShouldWholeSectionWritePlcAndSaveMachineModel()
    {
        await using var provider = BuildRuntime();
        var hosting = provider.GetRequiredService<IHostedService>();
        await hosting.StartAsync(CancellationToken.None);

        try
        {
            var Client = provider.GetRequiredService<IModbusClient>();
            var PointTable = provider.GetRequiredService<IPointTableService>();
            var Recipe = provider.GetRequiredService<IRecipeService>();

            // PLC 里放一段"机种区段内的残留数据"（点表之外的地址）：整段写入前会清零
            await Client.WriteSingleRegisterAsync(3600, 999);

            // 只在界面上改内存，不点任何下发
            PointTable.SetPointValue("点胶", 1, "X", 111.5);

            await PointTable.ParameterWriteAsync("点胶");

            // ① 点表值整段写进 PLC（第 2 点 X = D2702）
            Assert.Equal(111.5f, await ReadFloatAsync(Client, dispenseXBaseAddress + 2), 2);

            // ② 机种区段内的残留字被清零（整段下发，而不是只写点表列）
            Assert.Equal(0, (await Client.ReadHoldingRegisterAsync(3600, 1))[0]);

            // ③ 机种号一并写入
            Assert.Equal(Recipe.CurrentRecipeNumber, (await Client.ReadHoldingRegisterAsync(3330, 1))[0]);

            // ④ 已触发机种保存：重新按机种快照装载仍能看到刚写的点
            await PointTable.LoadPointTableAsync("点胶");
            Assert.Equal(111.5, PointTable.CurrentPointTable("点胶")[1].Take("X"), 2);
        }
        finally
        {
            await hosting.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// 2026-11-12 现场口径钉死：「参数写入」之后触发的保存，顺序必须是
    /// <b>① 先写入配方（整段下发）→ ② 再把 PLC 里的 D2500–D3999 读回来存成机种</b>。
    ///
    /// <para><b>怎么证明是"读回来的"：</b>拿机种快照里的非 0 项逐个回 PLC 核对。
    /// 若实现退回"只存内存快照"或"先读后写"，两边就会对不上（PLC 里会有快照没有的残留、
    /// 或快照里有 PLC 没有的值）。</para>
    /// </summary>
    [Fact]
    public async Task ParameterWriteAfterMachineModelSnapshotShouldAndPlcReadBackPerItemConsistent()
    {
        await using var provider = BuildRuntime();
        var hosting = provider.GetRequiredService<IHostedService>();
        await hosting.StartAsync(CancellationToken.None);

        try
        {
            var Client = provider.GetRequiredService<IModbusClient>();
            var PointTable = provider.GetRequiredService<IPointTableService>();
            var Recipe = provider.GetRequiredService<IRecipeService>();

            // 机种区段里先放一段"不属于本机种"的残留：整段下发会清零，快照里就不该有它
            await Client.WriteSingleRegisterAsync(3900, 777);

            PointTable.SetPointValue("点胶", 2, "X", 222.25);

            await PointTable.ParameterWriteAsync("点胶");

            var Number = Recipe.CurrentRecipeNumber;
            var Snapshot = Recipe.SectionSnapshot(Number);

            Assert.NotEmpty(Snapshot);
            Assert.False(Snapshot.ContainsKey(3900 - MachineModelSection.StartAt), "整段下发应先清零残留字");

            // 抽样核对：快照里的每一项都是 PLC 里真实存在的值
            var sampling = Snapshot.OrderBy(Item => Item.Key).Take(12).ToList();

            foreach (var (Offset, Value) in sampling)
            {
                var plcValue = await Client.ReadHoldingRegisterAsync(MachineModelSection.StartAt + Offset, 1);
                Assert.Equal((ushort)Value, plcValue[0]);
            }

            // 刚写的坐标在两边都到位（第 3 点 X = D2704）
            Assert.Equal(222.25f, await ReadFloatAsync(Client, dispenseXBaseAddress + 4), 2);
        }
        finally
        {
            await hosting.StopAsync(CancellationToken.None);
        }
    }
}
