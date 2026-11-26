using Microsoft.Extensions.Logging.Abstractions;
using PlcHmi.Business.Configuration;
using PlcHmi.Business.Services;
using PlcHmi.Communication.Inovance;
using PlcHmi.Communication.Modbus;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;
using PlcHmi.Tests.Fake;

namespace PlcHmi.Tests;

/// <summary>
/// 辅助地址「批量合并读取」回归（2026-11-19 性能改造）。
///
/// <para><b>守的是什么：</b>辅助地址上限提到 30 行后，周期回读不能退化成"一行一次请求"。
/// 合并读取必须与逐行读取 <see cref="PageAddressService.ReadAuxAddressAsync"/> 得到完全相同的值，
/// 且把连续地址压成少量请求；开关项与无效项按 null 跳过，不影响其它项。</para>
/// </summary>
public class AuxAddressBatchReadTest
{
    private static PageAddressService NewService(IModbusClient Connect)
    {
        var Parser = new InovanceAddressParser(PlcSeries.SmallPlc);

        return new PageAddressService(
            Connect,
            new TagWriter(Connect, new TagRepository(NullLogger<TagRepository>.Instance), Parser,
                NullLogger<TagWriter>.Instance),
            RecipeConfigLoader.Load(out _),
            new AllowAllPermissionGuard(),
            NullLogger<PageAddressService>.Instance);
    }

    [Fact]
    public async Task BatchReadShouldAndPerItemRowReadPerItemConsistent()
    {
        await using var Client = new FakeModbusClient();
        await Client.ConnectAsync();

        var serviceInstance = NewService(Client);

        // 用 8 个离散的 D 区固定地址（Float32 占 2 字），预设可区分解码值。
        // 不用虚拟 PLC：它的状态机会周期性改写部分 D 区，写入值不保证原样留下。
        var itemList = Enumerable.Range(0, 8)
            .Select(Sequence => new AuxAddressItem
            {
                Key = $"一致{Sequence}",
                Name = $"一致{Sequence}",
                Address = $"D{2000 + Sequence * 8}",   // 间隔 8 字，确保互不重叠
                DataType = TagDataType.Float32,
                Group = "锁附",
            })
            .ToList();

        for (var Sequence = 0; Sequence < itemList.Count; Sequence++)
        {
            var Value = 10.5f + Sequence;
            var word = RegisterCodec.Encode(TagDataType.Float32, Value);
            Client.PreSetHoldingRegister(2000 + Sequence * 8, word[0]);
            Client.PreSetHoldingRegister(2000 + Sequence * 8 + 1, word[1]);
        }

        // 逐行读取的基准
        var Baseline = new double[itemList.Count];
        for (var Sequence = 0; Sequence < itemList.Count; Sequence++)
        {
            Baseline[Sequence] = await serviceInstance.ReadAuxAddressAsync(itemList[Sequence]);
        }

        // 批量读取：值与顺序都必须对齐
        var batch = await serviceInstance.BatchReadAuxAddressAsync(itemList);

        Assert.Equal(itemList.Count, batch.Count);
        for (var Sequence = 0; Sequence < itemList.Count; Sequence++)
        {
            Assert.NotNull(batch[Sequence]);
            Assert.Equal(Baseline[Sequence], batch[Sequence]!.Value, 2);
            Assert.Equal(10.5 + Sequence, batch[Sequence]!.Value, 2);
        }
    }

    [Fact]
    public async Task BatchReadShouldConnectContinueAddressMergeAsFewAmountRequest()
    {
        await using var Client = new FakeModbusClient();
        await Client.ConnectAsync();
        var serviceInstance = NewService(Client);

        // 人为构造 30 个连续 D 区项（模拟扩到 30 行后的最坏情况）
        var itemList = Enumerable.Range(0, 30)
            .Select(Sequence => new AuxAddressItem
            {
                Key = $"批量{Sequence}",
                Name = $"批量{Sequence}",
                Address = $"D{3000 + Sequence * 2}",
                DataType = TagDataType.Float32,
                Group = "锁附",
            })
            .ToList();

        var result = await serviceInstance.BatchReadAuxAddressAsync(itemList);

        Assert.Equal(30, result.Count);
        // D3000–D3058 跨度 59 字 < 上限 100，应合成 1 次读
        Assert.Equal(1, Client.HoldingRegisterReadCount);
        Assert.All(result, Value => Assert.NotNull(Value));
    }

    [Fact]
    public async Task DisconnectedWhenBatchReadShouldAllSkip()
    {
        await using var Client = new FakeModbusClient();
        var serviceInstance = NewService(Client);

        var result = await serviceInstance.BatchReadAuxAddressAsync(
        [
            new AuxAddressItem { Key = "甲", Address = "D100", DataType = TagDataType.Float32 },
        ]);

        Assert.Single(result);
        Assert.Null(result[0]);
        Assert.Equal(0, Client.HoldingRegisterReadCount);
    }

    [Fact]
    public async Task SwitchItemShouldReturnnullAndNotInterfereOtherItem()
    {
        await using var Client = new FakeModbusClient();
        await Client.ConnectAsync();

        var serviceInstance = NewService(Client);

        var numericValueItem = new AuxAddressItem
        {
            Key = "数值", Name = "数值", Address = "D100",
            DataType = TagDataType.Float32, Group = "锁附",
        };
        var switchItem = new AuxAddressItem
        {
            Key = "开关", Name = "开关", Address = "M200",
            DataType = TagDataType.Boolean, Group = "锁附",
        };

        var word = RegisterCodec.Encode(TagDataType.Float32, 7.25);
        Client.PreSetHoldingRegister(100, word[0]);
        Client.PreSetHoldingRegister(101, word[1]);

        var result = await serviceInstance.BatchReadAuxAddressAsync([switchItem, numericValueItem]);

        Assert.Equal(2, result.Count);
        Assert.Null(result[0]);                 // 开关不参与 D 区合并
        Assert.NotNull(result[1]);
        Assert.Equal(7.25, result[1]!.Value, 2);
    }
}
