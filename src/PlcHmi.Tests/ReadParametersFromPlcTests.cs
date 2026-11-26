using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PlcHmi.Business;
using PlcHmi.Business.Configuration;
using PlcHmi.Business.Services;
using PlcHmi.Communication.Modbus;
using PlcHmi.Communication.Simulation;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;

namespace PlcHmi.Tests;

/// <summary>
/// 「从 PLC 读取参数」功能测试。
///
/// <para><b>守的是什么</b>：这是参数页与 PLC 之间第二条数据通路（与「下发」相反方向）。
/// 关键约束：<b>任一项超出允许范围必须整体拒绝</b>——绝不允许"读回一半"，
/// 否则本地与 PLC 各错一半，之后无论往哪个方向同步都是错的。</para>
///
/// <para>读取会写测试输出目录里的 <c>recipes.json</c>（留底），归入 config-files
/// 集合禁用并行，并在用例内备份 / 还原。</para>
/// </summary>
[Collection("config-files")]
public class FromPlcReadParameterTest
{
    private static async Task<ServiceProvider> StartRuntimeAsync()
    {
        var Settings = AppSettingsLoader.Load(out var error);
        Assert.Null(error);

        // 仓库配置处于真实 PLC 模式（现场部署）；测试强制模拟器，保持密封
        Settings.PlcConnection.UseSimulator = true;
        Settings.Polling.FastIntervalMilliseconds = 50;
        Settings.Polling.SlowIntervalMilliseconds = 100;
        Settings.Polling.EventIntervalMilliseconds = 100;

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging();
        serviceCollection.AddPlcHmiService(Settings.PlcConnection, Settings.Polling, RecipeConfigLoader.Load(out _));

        // 本组用例不验证权限边界（那由 PermissionGuardTests 覆盖），以受信任身份运行
        serviceCollection.AddSingleton<IPermissionGuard, AllowAllPermissionGuard>();

        var provider = serviceCollection.BuildServiceProvider(validateScopes: true);
        await provider.GetRequiredService<IHostedService>().StartAsync(CancellationToken.None);
        return provider;
    }

    /// <summary>把一个 Float32 值按 CDAB 写进模拟器的指定参数地址。</summary>
    private static void WriteFloat(VirtualPlcDevice Device, int addressValue, float Value)
    {
        var registerList = RegisterCodec.Encode(TagDataType.Float32, Value, WordOrder.LittleEndian);
        Device.memory.RunAloneOccupy(() =>
        {
            Device.memory.UnsafeSetHoldingRegister(addressValue, registerList[0]);
            Device.memory.UnsafeSetHoldingRegister(addressValue + 1, registerList[1]);
        });
    }

    /// <summary>
    /// 给六轴定位速度各写一个范围内的值。
    /// <para><b>为什么必须补齐</b>：模拟器未初始化的寄存器是 0，而速度下限是 1 ——
    /// 只要有一项越界，「整体拒绝」就会让本用例变成"什么都没读到"。
    /// 六轴（含 ZZ / 点胶 X / 点胶 Z）都要有值。</para>
    /// </summary>
    private static void WriteAllAxisValidValue(VirtualPlcDevice Device)
    {
        // 2026-10-21 现场配方：六轴速度集中在 D1400 起
        WriteFloat(Device, 1400, 300f);   // 锁附 X
        WriteFloat(Device, 1402, 300f);   // 锁附 Y
        WriteFloat(Device, 1404, 60f);    // 锁附 Z
        WriteFloat(Device, 1406, 60f);    // 锁附 ZZ
        WriteFloat(Device, 1410, 200f);   // 点胶 X
        WriteFloat(Device, 1408, 100f);   // 点胶 Z
    }

    [Fact]
    public async Task DisconnectedWhenReadShouldBeRejected()
    {
        var Settings = AppSettingsLoader.Load(out _);
        Settings.PlcConnection.UseSimulator = true;

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging();
        serviceCollection.AddPlcHmiService(Settings.PlcConnection, Settings.Polling, RecipeConfigLoader.Load(out _));

        // 本组用例不验证权限边界（那由 PermissionGuardTests 覆盖），以受信任身份运行
        serviceCollection.AddSingleton<IPermissionGuard, AllowAllPermissionGuard>();

        await using var provider = serviceCollection.BuildServiceProvider(validateScopes: true);
        var recipeItem = provider.GetRequiredService<IRecipeService>();

        // 未 StartAsync → 模拟客户端未连接
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => recipeItem.ReadPlcParameterAsync());
    }

    [Fact]
    public async Task ReadShouldOverwriteThisGroundAndKeepBottom_AndReturnDifferenceClearSingle()
    {
        var Original = await File.ReadAllTextAsync(ConfigPath.ParameterCollection);

        try
        {
            await using var provider = await StartRuntimeAsync();
            var recipeItem = provider.GetRequiredService<IRecipeService>();
            var Device = provider.GetRequiredService<VirtualPlcDevice>();

            WriteAllAxisValidValue(Device);

            // 模拟现场：有人直接在 PLC 侧把 X 轴定位速度改成了 300（合法值，范围内）
            // 锁附 X 定位速度 = D1400
            WriteFloat(Device, 1400, 300f);

            var Change = await recipeItem.ReadPlcParameterAsync();

            // 差异清单应指出该项变化（本地原值不假设——其它测试可能改过 recipes.json）
            Assert.Contains(Change, Client => Client.Contains("X 轴定位速度") && Client.Contains("300"));

            // 内存参数已被覆盖
            Assert.Equal(300.0, recipeItem.CurrentParameter["X.LocateSpeed"], precision: 3);
        }
        finally
        {
            await File.WriteAllTextAsync(ConfigPath.ParameterCollection, Original);
        }
    }

    [Fact]
    public async Task PlcValueOutOfRangeShouldWholeBodyRejectAndThisGroundHoldNotChange()
    {
        var Original = await File.ReadAllTextAsync(ConfigPath.ParameterCollection);

        try
        {
            await using var provider = await StartRuntimeAsync();
            var recipeItem = provider.GetRequiredService<IRecipeService>();
            var Device = provider.GetRequiredService<VirtualPlcDevice>();

            WriteAllAxisValidValue(Device);

            // 先成功读回一个合法值，建立"本地已被覆盖"的基线（X 轴 D1400）
            WriteFloat(Device, 1400, 300f);
            var Change = await recipeItem.ReadPlcParameterAsync();
            Assert.Equal(300.0, recipeItem.CurrentParameter["X.LocateSpeed"], precision: 3);

            // 再把 Z 轴定位速度改成 5000（超出允许范围 max 1000）；锁附 Z = D1404
            WriteFloat(Device, 1404, 5000f);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => recipeItem.ReadPlcParameterAsync());

            // 错误清单指出越界项；不因一项越界而吞掉其它项的信息
            Assert.Contains("超出允许范围", exception.Message);
            Assert.Contains("Z 轴定位速度", exception.Message);

            // 关键：整体拒绝——合法项（X=300）也不允许生效，本地保持上一次读取的结果
            Assert.Equal(300.0, recipeItem.CurrentParameter["X.LocateSpeed"], precision: 3);
        }
        finally
        {
            await File.WriteAllTextAsync(ConfigPath.ParameterCollection, Original);
        }
    }

    // ── 启动同步（开机从 PLC 刷新全局参数） ──────────────────────────────
    //
    // 与上面「从 PLC 读取参数」的区别：那是操作员按按钮的显式动作，越界要整体拒绝；
    // 启动同步是"机种区之外的 D 区以 PLC 为准，开机读一次刷新"，单项容错、逐项覆盖。

    [Fact]
    public async Task StartSyncShouldByPlcValueOverwriteThisGroundBufferStore()
    {
        var originalParameter = await File.ReadAllTextAsync(ConfigPath.ParameterCollection);
        var originalMachineModel = await File.ReadAllTextAsync(ConfigPath.RecipeCollection);

        try
        {
            await using var provider = await StartRuntimeAsync();
            var recipeItem = provider.GetRequiredService<IRecipeService>();
            var Device = provider.GetRequiredService<VirtualPlcDevice>();

            WriteAllAxisValidValue(Device);
            WriteFloat(Device, 1400, 250f);

            // 本地先改成一个明显不同的值（模拟上次运行遗留的 parameters.json）
            recipeItem.SetParameter("X.LocateSpeed", 111);
            Assert.Equal(111.0, recipeItem.CurrentParameter["X.LocateSpeed"], precision: 3);

            // 连接建立后补做的那次同步：PLC 是权威 → 覆盖为 250
            await recipeItem.InitializeAsync();

            Assert.Equal(250.0, recipeItem.CurrentParameter["X.LocateSpeed"], precision: 3);
            Assert.Equal(6, recipeItem.CurrentParameter.Count);
        }
        finally
        {
            await File.WriteAllTextAsync(ConfigPath.ParameterCollection, originalParameter);
            await File.WriteAllTextAsync(ConfigPath.RecipeCollection, originalMachineModel);
        }
    }

    [Fact]
    public async Task StartSyncShouldSkipPlcInBeyondBoundaryValueAndRetainThisGroundValue()
    {
        var originalParameter = await File.ReadAllTextAsync(ConfigPath.ParameterCollection);
        var originalMachineModel = await File.ReadAllTextAsync(ConfigPath.RecipeCollection);

        try
        {
            await using var provider = await StartRuntimeAsync();
            var recipeItem = provider.GetRequiredService<IRecipeService>();
            var Device = provider.GetRequiredService<VirtualPlcDevice>();

            WriteAllAxisValidValue(Device);

            // PLC 侧 X 轴被写成 5000（超出 1000 上限）——半个字错位或残留数据都可能这样
            WriteFloat(Device, 1400, 5000f);

            recipeItem.SetParameter("X.LocateSpeed", 111);

            await recipeItem.InitializeAsync();

            // 单项容错：越界项保留本地值（不让天文数字进内存），其余项照常刷新
            Assert.Equal(111.0, recipeItem.CurrentParameter["X.LocateSpeed"], precision: 3);
            Assert.Equal(300.0, recipeItem.CurrentParameter["Y.LocateSpeed"], precision: 3);
        }
        finally
        {
            await File.WriteAllTextAsync(ConfigPath.ParameterCollection, originalParameter);
            await File.WriteAllTextAsync(ConfigPath.RecipeCollection, originalMachineModel);
        }
    }
}
