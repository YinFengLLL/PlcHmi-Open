using Microsoft.Extensions.Logging.Abstractions;
using PlcHmi.Business.Configuration;
using PlcHmi.Business.Services;
using PlcHmi.Communication.Inovance;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;
using PlcHmi.Tests.Fake;

namespace PlcHmi.Tests;

/// <summary>
/// 参数页延时表 / 按钮表的 <b>PLC 回读</b>回归（2026-10-17 现场要求）。
///
/// <para><b>守的是什么：</b>延时（D 区）与按钮（M 区）的地址由管理员在界面上配置，
/// 不在 <c>tags.json</c> 里、不走轮询引擎；而 PLC 侧同样可能改写这些值
/// （切换机种、HMI 旁的操作面板、断电前的保持状态）。回读让界面显示 PLC 当下真实值，
/// 而不是本地配置值——否则现场会看到"改了的延时没生效"。</para>
///
/// <para><b>2026-10-21：</b>出厂配置换成现场版后，延时表是空的（现场只用了按钮表），
/// 因此本组用例<b>自带一份参数表</b>再跑——延时回读的语义与工厂里有没有延时无关。</para>
/// </summary>
[Collection("config-files")]
public class ParameterReadBackTest
{
    /// <summary>用例自带的参数表：两条 DINT 延时（D1000 / D1002）+ 一条按钮（M5000）。</summary>
    private const string testParameterTable = """
        {
          "delayCollection": [
            {
              "id": "t1",
              "name": "测试延时1",
              "address": "D1000",
              "unit": "s",
              "min": 0,
              "max": 2147483.647,
              "enabled": true,
              "order": 1
            },
            {
              "id": "t2",
              "name": "测试延时2",
              "address": "D1002",
              "unit": "s",
              "min": 0,
              "max": 2147483.647,
              "enabled": true,
              "order": 2
            }
          ],
          "buttonCollection": [
            {
              "id": "b1",
              "name": "测试按钮",
              "address": "M5000",
              "enabled": true,
              "order": 1
            }
          ]
        }
        """;

    /// <summary>
    /// 装上测试参数表并构造被测服务。返回的 <c>原始</c> 必须写回（<see cref="Recover"/>）。
    /// </summary>
    private static async Task<(string Original, ParameterTableService Service, FakeModbusClient Client)> NewBuildService()
    {
        var path = ConfigPath.ParameterTable;
        var Original = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, testParameterTable);

        var Client = new FakeModbusClient();
        var Parser = new InovanceAddressParser(PlcSeries.SmallPlc);
        var Writer = new TagWriter(
            Client, new FakeTagRepository(), Parser, NullLogger<TagWriter>.Instance);

        var Service = new ParameterTableService(
            Parser, Writer, NullLogger<ParameterTableService>.Instance, new AllowAllPermissionGuard());

        await Service.InitializeAsync();

        return (Original, Service, Client);
    }

    private static Task Recover(string Original) =>
        File.WriteAllTextAsync(ConfigPath.ParameterTable, Original);

    [Fact]
    public async Task ReadBack_ShouldReadOutDintDelayAndButtonState()
    {
        var (Original, Service, Client) = await NewBuildService();

        try
        {
            var delayItem = Service.DelayCollection[0];   // 测试参数表第一条：D1000
            var ButtonItem = Service.ButtonCollection[0];   // 测试参数表第一条：M5000

            Assert.Equal("D1000", delayItem.Address);
            Assert.Equal("M5000", ButtonItem.Address);

            // PLC 侧的值：D1000 = 2500 ms（2.5 S，占 D1000/D1001 两字，低字在前）、M5000 = ON
            Client.PreSetHoldingRegister(1000, 2500);
            Client.PreSetHoldingRegister(1001, 0);
            Client.PreSetCoil(5000, true);

            await Service.ReadBackAsync();

            Assert.Equal(2.5, Service.ReadBackDelayValue(delayItem.Id));
            Assert.True(Service.ReadBackButtonState(ButtonItem.Id));
        }
        finally
        {
            await Recover(Original);
            await Client.DisposeAsync();
        }
    }

    [Fact]
    public async Task ReadBack_LargeDelayShouldBy32BitPositiveConfirmConvert()
    {
        var (Original, Service, Client) = await NewBuildService();

        try
        {
            var delayItem = Service.DelayCollection[0];

            // 65536 ms = 65.536 S：16 位时代装不下的值，DINT 下低字 0、高字 1
            Client.PreSetHoldingRegister(1000, 0);
            Client.PreSetHoldingRegister(1001, 1);

            await Service.ReadBackAsync();

            Assert.Equal(65.536, Service.ReadBackDelayValue(delayItem.Id));
        }
        finally
        {
            await Recover(Original);
            await Client.DisposeAsync();
        }
    }

    /// <summary>读不到的项只跳过自己：其余项照常回读，且不抛异常打断页面。</summary>
    [Fact]
    public async Task ReadBack_SingleItemFailedShouldNotAffectImpactOtherItem()
    {
        var (Original, Service, Client) = await NewBuildService();

        try
        {
            var item = Service.DelayCollection[0];   // D1000
            var item2 = Service.DelayCollection[1];   // D1002

            // 只预设第一条：第二条查不到（按 0），正好验证"读不到也给出确定值、不抛异常"
            Client.PreSetHoldingRegister(1000, 3000);
            Client.PreSetHoldingRegister(1001, 0);

            await Service.ReadBackAsync();

            Assert.Equal(3.0, Service.ReadBackDelayValue(item.Id));
            Assert.Equal(0.0, Service.ReadBackDelayValue(item2.Id));
        }
        finally
        {
            await Recover(Original);
            await Client.DisposeAsync();
        }
    }

    /// <summary>从未回读过的项返回 <c>null</c>（界面据此显示"—"而不是 0，避免误导）。</summary>
    [Fact]
    public async Task ReadBackBefore_ShouldReturnEmptyInsteadOfZero()
    {
        var (Original, Service, Client) = await NewBuildService();

        try
        {
            Assert.Null(Service.ReadBackDelayValue(Service.DelayCollection[0].Id));
            Assert.Null(Service.ReadBackButtonState(Service.ButtonCollection[0].Id));
        }
        finally
        {
            await Recover(Original);
            await Client.DisposeAsync();
        }
    }

    /// <summary>停用的条目不回读（地址可能已被改成别的用途，读回来反而是噪声）。</summary>
    [Fact]
    public async Task ReadBack_StopUseEntryShouldSkip()
    {
        var (Original, Service, Client) = await NewBuildService();

        try
        {
            var Entry = Service.DelayCollection[0].Clone();
            Entry.Enabled = false;
            Service.Update(ParameterTableCategory.Delay, Entry);

            Client.PreSetHoldingRegister(1000, 3000);
            Client.PreSetHoldingRegister(1001, 0);

            await Service.ReadBackAsync();

            Assert.Null(Service.ReadBackDelayValue(Entry.Id));
        }
        finally
        {
            await Recover(Original);
            await Client.DisposeAsync();
        }
    }

    /// <summary>回读间隔必须是 2s（与动作按钮同节奏：够快又不挤占串行 Modbus 链路）。</summary>
    [Fact]
    public void ReadBackIntervalShouldAsTwoSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), ParameterTableService.readBackInterval);
    }
}
