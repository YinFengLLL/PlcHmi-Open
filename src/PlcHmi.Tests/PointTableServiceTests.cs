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
/// 点表服务的端到端集成测试（真实配置 + 虚拟 PLC）。
///
/// <para>覆盖：锁附/点胶点表按列分区写入 → 读回校验 → 指令码同步 → 越界拦截 →
/// 坐标示教只写选中点 → <b>下发/示教后自动并入机种快照</b>。</para>
///
/// <para>归入 <c>config-files</c> 集合：下发与示教现在会写 <c>recipes.json</c>（机种快照），
/// 与其它动配置文件（含备份/还原）的用例并行会互相踩。</para>
/// </summary>
[Collection("config-files")]
public class PointTableServiceIntegrationTest
{
    private const int screwXBaseAddress = 2500;
    private const int screwYBaseAddress = 2560;
    private const int dispenseInstructionCodeBaseAddress = 3400;
    private const int dispenseXBaseAddress = 2700;
    private const int dispenseStartZBaseAddress = 2900;

    private static float DecodeFloat(ushort[] registerList) =>
        (float)RegisterCodec.Decode(
            TagDataType.Float32, registerList.AsSpan(0, 2), WordOrder.LittleEndian)!;

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
        return DecodeFloat(registerList);
    }

    [Fact]
    public async Task ScrewPointTableShouldByAgreedAddressWriteAndCanReadBack()
    {
        await using var provider = BuildRuntime();
        var hosting = provider.GetRequiredService<IHostedService>();
        await hosting.StartAsync(CancellationToken.None);

        try
        {
            var Client = provider.GetRequiredService<IModbusClient>();
            var PointTable = provider.GetRequiredService<IPointTableService>();

            Assert.Equal(30, PointTable.PointCount("锁附"));

            // 2026-11-18 现场要求：点胶扩到 50 点（锁附仍 30 点）
            Assert.Equal(50, PointTable.PointCount("点胶"));

            PointTable.SetPointValue("锁附", 1, "X", 123.45);
            PointTable.SetPointValue("锁附", 1, "Y", -67.89);

            await PointTable.WritePointTableAsync("锁附");

            Assert.Equal(123.45f, await ReadFloatAsync(Client, screwXBaseAddress + 2), 2);
            Assert.Equal(-67.89f, await ReadFloatAsync(Client, screwYBaseAddress + 2), 2);

            // 未示教的第 1 点保持 0
            Assert.Equal(0f, await ReadFloatAsync(Client, screwXBaseAddress), 2);
        }
        finally
        {
            await hosting.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task DispensePointTableShouldContainInstructionCodeAndWriteToReachAgreedAddress()
    {
        await using var provider = BuildRuntime();
        var hosting = provider.GetRequiredService<IHostedService>();
        await hosting.StartAsync(CancellationToken.None);

        try
        {
            var Client = provider.GetRequiredService<IModbusClient>();
            var PointTable = provider.GetRequiredService<IPointTableService>();

            PointTable.SetPointValue("点胶", 0, "指令码", 7);
            PointTable.SetPointValue("点胶", 0, "X", 111.5);
            PointTable.SetPointValue("点胶", 0, "Y", 222.5);
            PointTable.SetPointValue("点胶", 0, "Z", 333.5);

            await PointTable.WritePointTableAsync("点胶");

            var instructionCode = await Client.ReadHoldingRegisterAsync(dispenseInstructionCodeBaseAddress, 1);
            Assert.Equal((ushort)7, instructionCode[0]);

            Assert.Equal(111.5f, await ReadFloatAsync(Client, dispenseXBaseAddress), 2);
            Assert.Equal(222.5f, await ReadFloatAsync(Client, dispenseXBaseAddress + 100), 2);    // Y = 2800
            Assert.Equal(333.5f, await ReadFloatAsync(Client, dispenseStartZBaseAddress), 2);

            // 第二点的指令码应落在 D3401（步长 1 字）
            PointTable.SetPointValue("点胶", 1, "指令码", 9);
            await PointTable.WritePointTableAsync("点胶");
            var noTwoInstructionCode = await Client.ReadHoldingRegisterAsync(dispenseInstructionCodeBaseAddress + 1, 1);
            Assert.Equal((ushort)9, noTwoInstructionCode[0]);
        }
        finally
        {
            await hosting.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// 2026-10-21 现场要求：点胶页指令码改为下拉选单，选中即写该点的指令码字
    /// （第 2 点选「直线」= 码值 2 → D3401），点表显示文字，且切页回来仍在。
    /// </summary>
    [Fact]
    public async Task SelectedInstructionCodeShouldStandImmediatelyWritePairShouldPointAddressAndSaveToMachineModel()
    {
        await using var provider = BuildRuntime();
        var hosting = provider.GetRequiredService<IHostedService>();
        await hosting.StartAsync(CancellationToken.None);

        try
        {
            var Client = provider.GetRequiredService<IModbusClient>();
            var PointTable = provider.GetRequiredService<IPointTableService>();

            await PointTable.WriteInstructionCodeAsync("点胶", 1, 2);

            // 第 2 点（0 基序号 1）→ D3401 = 2
            var register = await Client.ReadHoldingRegisterAsync(dispenseInstructionCodeBaseAddress + 1, 1);
            Assert.Equal((ushort)2, register[0]);

            // 内存点表同步（界面「指令码」列显示「直线」）
            var code = PointTable.CurrentPointTable("点胶")[1].Take("指令码");
            Assert.Equal(2d, code);
            Assert.Equal("直线", InstructionCodeDefinition.ShowText(code));

            // 只动指令码：第 2 点的坐标没被顺带写下去
            Assert.Equal(0f, await ReadFloatAsync(Client, dispenseXBaseAddress + 2), 2);

            // 并入机种快照：切页再进入仍能看到这个指令码
            await PointTable.LoadPointTableAsync("点胶");
            Assert.Equal(2d, PointTable.CurrentPointTable("点胶")[1].Take("指令码"));
        }
        finally
        {
            await hosting.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task CoordinateTeachShouldOnlyWriteSelectedPointAndRetainInstructionCode()
    {
        await using var provider = BuildRuntime();
        var hosting = provider.GetRequiredService<IHostedService>();
        await hosting.StartAsync(CancellationToken.None);

        try
        {
            var Client = provider.GetRequiredService<IModbusClient>();
            var PointTable = provider.GetRequiredService<IPointTableService>();

            // 第 6 点预置指令码 9；示教位置为 0，不应把指令码抹掉
            PointTable.SetPointValue("点胶", 5, "指令码", 9);
            await PointTable.TeachCurrentPositionAsync("点胶", 5);

            var instructionCode = await Client.ReadHoldingRegisterAsync(dispenseInstructionCodeBaseAddress + 5, 1);
            Assert.Equal((ushort)9, instructionCode[0]);

            // 第 1 点的指令码应保持 0（没被误写）
            var firstInstructionCode = await Client.ReadHoldingRegisterAsync(dispenseInstructionCodeBaseAddress, 1);
            Assert.Equal((ushort)0, firstInstructionCode[0]);
        }
        finally
        {
            await hosting.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// 2026-10-24 现场要求：锁附页「保存坐标点」写 X / Y 轴当前位置，<b>以及当前浮高</b>
    /// （浮高来自测高地址 D240，不是 Z 轴位置，也不是上一行的人工填写值）。
    /// </summary>
    [Fact]
    public async Task ScrewSaveCoordinatePointShouldWriteXYAndCurrentFloatHeight()
    {
        await using var provider = BuildRuntime();
        var hosting = provider.GetRequiredService<IHostedService>();
        await hosting.StartAsync(CancellationToken.None);

        try
        {
            var Client = provider.GetRequiredService<IModbusClient>();
            var PointTable = provider.GetRequiredService<IPointTableService>();

            // 旧值是 12.5：示教后必须被"当前浮高"覆盖，而不是保留
            PointTable.SetPointValue("锁附", 0, "浮高", 12.5);
            PointTable.SetPointValue("锁附", 0, "X", 99);

            // PLC 里的当前浮高 = 6.75（REAL 两字，CDAB）
            await Client.WriteMultiRegisterAsync(
                240, RegisterCodec.Encode(TagDataType.Float32, 6.75, WordOrder.LittleEndian));

            // 模拟器里各轴位置都是 0
            await PointTable.TeachCurrentPositionAsync("锁附", 0);

            var rowItem = PointTable.CurrentPointTable("锁附")[0];
            Assert.Equal(6.75, rowItem.Take("浮高"), 2);   // 当前浮高写进该点
            Assert.Equal(0, rowItem.Take("X"), 2);          // X 由当前位置覆盖

            // 浮高落到该点的浮高列（第 1 点 = D2620）
            Assert.Equal(6.75f, await ReadFloatAsync(Client, 2620), 2);
        }
        finally
        {
            await hosting.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task DispenseSaveCoordinatePointShouldWriteXYZAndRetainInstructionCode()
    {
        await using var provider = BuildRuntime();
        var hosting = provider.GetRequiredService<IHostedService>();
        await hosting.StartAsync(CancellationToken.None);

        try
        {
            var Client = provider.GetRequiredService<IModbusClient>();
            var PointTable = provider.GetRequiredService<IPointTableService>();

            PointTable.SetPointValue("点胶", 0, "指令码", 5);
            await PointTable.TeachCurrentPositionAsync("点胶", 0);

            // 指令码不受影响，X/Y/Z 三个坐标列都参与了写入
            Assert.Equal(5d, PointTable.CurrentPointTable("点胶")[0].Take("指令码"));

            var instructionCode = await Client.ReadHoldingRegisterAsync(dispenseInstructionCodeBaseAddress, 1);
            Assert.Equal((ushort)5, instructionCode[0]);

            Assert.Equal(0f, await ReadFloatAsync(Client, dispenseXBaseAddress), 2);
            Assert.Equal(0f, await ReadFloatAsync(Client, dispenseXBaseAddress + 100), 2);    // Y = 2800
            Assert.Equal(0f, await ReadFloatAsync(Client, dispenseStartZBaseAddress), 2);
        }
        finally
        {
            await hosting.StopAsync(CancellationToken.None);
        }
    }

    // ── 2026-10-16 现场问题回归：下发/示教后切页再进入数据必须还在 ──────

    /// <summary>
    /// 现场报障：锁附/点胶示教页「下发点表」后切换页面再回来，点表数据不见了，
    /// 必须先去机种管理页点「保存机种」才显示。
    ///
    /// <para><b>真因</b>：页面进入时是从<b>机种快照</b>（recipes.json）装载的，
    /// 而旧实现"下发点表"只写 PLC、不动机种快照——于是先切页再回来读到的还是旧快照。
    /// 现在下发/示教写完 PLC 后会把本表数据并入当前机种快照。</para>
    /// </summary>
    [Fact]
    public async Task SendPointTableAfterReenterPageShouldStillCanSeeToData()
    {
        await using var provider = BuildRuntime();
        var hosting = provider.GetRequiredService<IHostedService>();
        await hosting.StartAsync(CancellationToken.None);

        try
        {
            var PointTable = provider.GetRequiredService<IPointTableService>();

            PointTable.SetPointValue("锁附", 2, "X", 111.11);
            PointTable.SetPointValue("锁附", 2, "Y", -222.22);

            await PointTable.WritePointTableAsync("锁附");

            // 复刻"切页 → 再进入"：页面进入会按机种快照重新装载
            await PointTable.LoadPointTableAsync("锁附");

            var rowItem = PointTable.CurrentPointTable("锁附")[2];
            Assert.Equal(111.11, rowItem.Take("X"), 2);
            Assert.Equal(-222.22, rowItem.Take("Y"), 2);
        }
        finally
        {
            await hosting.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task CoordinateTeachAfterReenterPageAlsoShouldStillCanSeeToThePoint()
    {
        await using var provider = BuildRuntime();
        var hosting = provider.GetRequiredService<IHostedService>();
        await hosting.StartAsync(CancellationToken.None);

        try
        {
            var Client = provider.GetRequiredService<IModbusClient>();
            var PointTable = provider.GetRequiredService<IPointTableService>();

            // 用"当前浮高"验证这一点确实存进了快照（示教后会写浮高列）
            await Client.WriteMultiRegisterAsync(
                240, RegisterCodec.Encode(TagDataType.Float32, 7.5, WordOrder.LittleEndian));

            await PointTable.TeachCurrentPositionAsync("锁附", 3);

            await PointTable.LoadPointTableAsync("锁附");

            Assert.Equal(7.5, PointTable.CurrentPointTable("锁附")[3].Take("浮高"), 2);
        }
        finally
        {
            await hosting.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExceedOutRangeCoordinateShouldRejectSend()
    {
        await using var provider = BuildRuntime();
        var hosting = provider.GetRequiredService<IHostedService>();
        await hosting.StartAsync(CancellationToken.None);

        try
        {
            var PointTable = provider.GetRequiredService<IPointTableService>();

            PointTable.SetPointValue("锁附", 0, "X", 99999);
            PointTable.SetPointValue("锁附", 0, "Y", 10);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => PointTable.WritePointTableAsync("锁附"));
            Assert.Contains("超出坐标范围", exception.Message);
        }
        finally
        {
            await hosting.StopAsync(CancellationToken.None);
        }
    }
}
