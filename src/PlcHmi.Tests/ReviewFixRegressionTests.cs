using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PlcHmi.Business.Configuration;
using PlcHmi.Business.Services;
using PlcHmi.Communication.Inovance;
using PlcHmi.Communication.Simulation;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;
using PlcHmi.Tests.Fake;

namespace PlcHmi.Tests;

/// <summary>
/// 2026-10-15 算法优化与缺陷修复的回归测试。
///
/// <para><b>守的是什么：</b>本轮修掉的四类问题各有一条"改回去就会红"的断言——
/// R1（模拟器 32 位写入绕过编解码器）、R4（按点位名写线圈绕过 M 区校验）、
/// CSV 导入缺重名/引号校验、延时换算的 double 域边界。都是"注释声称安全、
/// 实际路径没走到"型的缺陷，普通读写用例碰不到。</para>
/// </summary>
public class ReviewFixRegressionTest
{
    // ── R1：模拟器 Float32 写入必须按 CDAB（低字在低地址）─────────────

    /// <summary>
    /// 虚拟 PLC 的 32 位写入必须走 <c>寄存器编解码器</c>：定位速度 80.00f 的
    /// 低字应在低地址（D1400）、高字在高地址（D1401）。
    /// <para>若有人把编解码器调用改回手写字序且写反（大端在前），这里立刻红——
    /// 模拟器与真实客户端字序一旦分歧，全部自测结论在真机上失效。</para>
    /// </summary>
    [Fact]
    public async Task Simulator_PositioningSpeedShouldByCDABLowWordAtBeforeWrite()
    {
        await using var Device = new VirtualPlcDevice(NullLogger<VirtualPlcDevice>.Instance);

        // 独立复算期望字序（不经过寄存器编解码器，防止循环论证）
        var bitMode = BitConverter.SingleToUInt32Bits(80.00f);
        var expectedLowWord = (ushort)(bitMode & 0xFFFF);
        var expectedHighWord = (ushort)(bitMode >> 16);

        var lowWord = Device.memory.UnsafeGetHoldingRegister(1400);   // D1400 锁附 X 轴定位速度
        var highWord = Device.memory.UnsafeGetHoldingRegister(1401);

        Assert.Equal(expectedLowWord, lowWord);
        Assert.Equal(expectedHighWord, highWord);

        // 反序（大端在前）必须与正序不同，否则本用例失去区分度
        Assert.NotEqual(lowWord, highWord);
    }

    // ── R4：按点位名写线圈必须校验 M 区 ──────────────────────────────

    /// <summary>
    /// tags.json 里的点位被标为可写且地址是 Y 区时，按点位名写布尔必须被拒绝。
    /// <para>曾经只有"按地址写线圈"有 M 区校验，按点位名的路径没有——
    /// 配置文件被手工改错即可越权驱动物理输出（上位机永不越权的最后一道防线）。</para>
    /// </summary>
    [Fact]
    public async Task ByPointNameWriteCoil_NonMAreaShouldBeRejected()
    {
        await using var Client = new FakeModbusClient();

        var Writer = new TagWriter(
            Client,
            new FakeTagRepository(new TagDefinition
            {
                Name = "Y输出点",
                Address = "Y0",
                DataType = TagDataType.Boolean,
                AccessQuery = TagAccessQuery.Write,
                Group = TagGroup.Command,
            }),
            new InovanceAddressParser(PlcSeries.SmallPlc),
            NullLogger<TagWriter>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Writer.WriteBooleanAsync("Y输出点", true));

        Assert.Contains("M 区", exception.Message);
        Assert.Empty(Client.CoilWrite);   // 拒绝时必须没有任何写发生
    }

    /// <summary>正对照：M 区点位的按名写入应照常放行（守卫不误伤合法路径）。</summary>
    [Fact]
    public async Task ByPointNameWriteCoil_MAreaShouldAllow()
    {
        await using var Client = new FakeModbusClient();

        var Writer = new TagWriter(
            Client,
            new FakeTagRepository(new TagDefinition
            {
                Name = "M命令点",
                Address = "M200",
                DataType = TagDataType.Boolean,
                AccessQuery = TagAccessQuery.Write,
                Group = TagGroup.Command,
            }),
            new InovanceAddressParser(PlcSeries.SmallPlc),
            NullLogger<TagWriter>.Instance);

        await Writer.WriteBooleanAsync("M命令点", true);

        Assert.Single(Client.CoilWrite);
    }

    // ── CSV 导入：重名与空名必须整体拒绝 ──────────────────────────────

    /// <summary>
    /// 其它点 CSV 导入出现重名时必须整体拒绝：名称是"按名查找 / 删除 / CSV 交换"
    /// 的唯一依据，导入后出现同名点会让后续操作指向错误的点。
    /// <para>曾经 异步新增/异步编辑 都查重，唯独导入路径不查。</para>
    /// </summary>
    [Fact]
    public async Task OtherPointCsvImport_ReNameShouldWholeBodyReject()
    {
        var temporaryFile = Path.Combine(Path.GetTempPath(), $"aux-dup-{Guid.NewGuid():N}.csv");

        try
        {
            await File.WriteAllTextAsync(temporaryFile,
                "# 测试\r\n名称,X基址,Y基址,Z基址\r\n点甲,2500,2502,2504\r\n点甲,2600,2602,2604\r\n",
                new UTF8Encoding(true));

            await using var Client = new FakeModbusClient();
            var serviceInstance = NewAuxPointService(Client);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => serviceInstance.FromFileImportCsvAsync(temporaryFile));

            Assert.Contains("重复", exception.Message);
        }
        finally
        {
            if (File.Exists(temporaryFile))
            {
                File.Delete(temporaryFile);
            }
        }
    }

    /// <summary>
    /// 名称含逗号必须能原样往返：导出用引号包裹（<c>退出()</c>），
    /// 导入曾经裸 <c>Split(',')</c> 把 "A,B" 切成 5 段——往返失真。
    /// </summary>
    [Fact]
    public async Task OtherPointCsvImport_WithQuoteNumberNameShouldOriginalSampleRestore()
    {
        var temporaryFile = Path.Combine(Path.GetTempPath(), $"aux-quote-{Guid.NewGuid():N}.csv");

        try
        {
            // 与导出口径一致：含逗号的名称加双引号
            await File.WriteAllTextAsync(temporaryFile,
                "名称,X基址,Y基址,Z基址\r\n\"带,逗号\",2500,2502,2504\r\n普通点,2600,2602,2604\r\n",
                new UTF8Encoding(true));

            await using var Client = new FakeModbusClient();
            var serviceInstance = NewAuxPointService(Client);

            var Count = await serviceInstance.FromFileImportCsvAsync(temporaryFile);

            Assert.Equal(2, Count);

            var withCommaNumberPoint = serviceInstance.PointCollection.Single(PointEntry => PointEntry.Name == "带,逗号");
            Assert.Equal(2500, withCommaNumberPoint.XBaseAddress);   // 列不能错位
            Assert.Equal(2504, withCommaNumberPoint.ZBaseAddress);
        }
        finally
        {
            if (File.Exists(temporaryFile))
            {
                File.Delete(temporaryFile);
            }
        }
    }

    // ── 延时换算：超范围值必须在 double 域被拒 ────────────────────────

    /// <summary>
    /// 延时 ×1000 超出 32 位整数（DINT）范围时必须明确报错。
    /// <para>换算曾经先转 int 再判范围：double 超界时的转换结果依赖运行时
    /// （.NET Core 3.0+ 饱和），换运行时或改一行代码就可能变成静默写错值。
    /// 在 double 域先判范围，任何运行时下行为一致（2026-10-16 起口径为 32 位）。</para>
    /// </summary>
    [Fact]
    public async Task DelayConvert_ExceedOut32BitRangeShouldExplicitConfirmReportWrong()
    {
        var Parser = new InovanceAddressParser(PlcSeries.SmallPlc);

        TagWriter Writer = null!;   // 校验先于写 PLC，用不到
        var serviceInstance = new ParameterTableService(
            Parser, Writer, NullLogger<ParameterTableService>.Instance, new AllowAllPermissionGuard());

        var Entry = new ParameterEntryDefinition
        {
            Id = "test-delay",
            Name = "测试延时",
            Address = "D1000",
            Unit = "s",
            Min = 0,
            Max = 5_000_000_000,   // 故意放大上限：让"值合法但毫秒超界"的场景可达
            Enabled = true,
            Value = 4294967.296,      // ×1000 = 4294967296，超出 Int32 上限（2147483647）
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => serviceInstance.WriteDelayAsync(Entry));

        Assert.Contains("32 位整数（DINT）范围", exception.Message);
    }

    /// <summary>
    /// 延时按 32 位整数（DINT）下发：占 2 个字、低字在前（CDAB）。
    /// <para>16 位时代只有 65.535 S 上限（2026-10-16 现场要求改 DINT）；字序必须与
    /// 轴速度 Float32 / 机种快照同源（红线 R1），所以这里直接按 IEEE 位模式独立复算。</para>
    /// </summary>
    [Fact]
    public async Task DelaySend_ShouldByDintOccupyTwoWordAndLowWordAtBefore()
    {
        // 下发成功会静默落盘（保证"PLC 里的值 = 配置文件里的值"）：本用例改的是测试输出目录的
        // param-tables.json，跑完必须还原，否则同集合的其它用例会读到被清空的延时表
        var tablePath = ConfigPath.ParameterTable;
        var Original = await File.ReadAllTextAsync(tablePath);

        try
        {
            var Parser = new InovanceAddressParser(PlcSeries.SmallPlc);

            await using var Client = new FakeModbusClient();
            var Writer = new TagWriter(
                Client, new FakeTagRepository(), Parser, NullLogger<TagWriter>.Instance);

            var serviceInstance = new ParameterTableService(
                Parser, Writer, NullLogger<ParameterTableService>.Instance, new AllowAllPermissionGuard());

            // 先初始化：这样"静默落盘"写回的是与磁盘等价的内容，不会把配置写成空表
            // （本用例与读取同一份配置的 延迟地址范围测试 并行运行）
            await serviceInstance.InitializeAsync();

            // 30.000 S → 30000 ms：16 位装得下，用于验证字数与字序。
            // D1090 落在延时专属区尾部且不与配置里的 D1000/D1002/D1004 冲突。
            var Entry = new ParameterEntryDefinition
            {
                Id = "test-delay",
                Name = "测试延时",
                Address = "D1090",
                Unit = "s",
                Min = 0,
                Max = 2147483.647,
                Enabled = true,
                Value = 30.0,
            };

            await serviceInstance.WriteDelayAsync(Entry);

            var block = Assert.Single(Client.RegisterBlockWrite);
            Assert.Equal(1090, block.Address);          // D1090 的 Modbus 地址
            Assert.Equal(2, block.Value.Count);         // DINT 占 2 字

            var bitMode = (uint)30000;
            Assert.Equal((ushort)(bitMode & 0xFFFF), block.Value[0]);   // 低字在前（CDAB）
            Assert.Equal((ushort)(bitMode >> 16), block.Value[1]);

            // 65.536 S（16 位装不下）仍能正确下发：高字为 1 —— 这正是本次改 DINT 的目的
            var longDelay = Entry.Clone();
            longDelay.Value = 65.536;                    // 65536 ms

            await serviceInstance.WriteDelayAsync(longDelay);

            var noTwoBlock = Client.RegisterBlockWrite[^1];
            Assert.Equal((ushort)0, noTwoBlock.Value[0]);
            Assert.Equal((ushort)1, noTwoBlock.Value[1]);
        }
        finally
        {
            await File.WriteAllTextAsync(tablePath, Original);
        }
    }

    // ── 辅助 ───────────────────────────────────────────────────────────

    private static AuxPointService NewAuxPointService(FakeModbusClient Client) =>
        new(
            Client,
            RecipeConfigLoader.Load(out _),
            new AllowAllPermissionGuard(),
            NullLogger<AuxPointService>.Instance);
}
