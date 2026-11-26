using Microsoft.Extensions.Logging.Abstractions;
using PlcHmi.Business.Services;
using PlcHmi.Communication.Inovance;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;
using PlcHmi.Tests.Fake;

namespace PlcHmi.Tests;

/// <summary>
/// X / Y 点真机采集（位区选择）回归测试。
///
/// <para><b>守的是什么</b>：X 是物理输入 → <b>离散输入区（FC02）</b>；Y 是输出 → <b>线圈区（FC01）</b>
/// （2026-10-17 修正，此前 X 误配在线圈区，现场表现为"Y 一直对、X 一直错"）。
/// 本组钉住三件事：① 默认功能码分工正确；② 个别把 X 放在线圈表的机型仍能自愈
/// （读不通抛异常 → 回退；读全 0 而另一位区有信号 → 切换，且只切一次）；
/// ③ 主位区证明过活性后，绝不被另一位区的无关位带错（2026-10-15 现场回归）。</para>
///
/// <para>本组用替身客户端把真机特征固定下来，断言：<b>值跟随物理状态</b>、
/// <b>位区切换只发生一次并记住</b>、<b>标准固件不被误切</b>、<b>其它采集不被牵连</b>。
/// 读的是测试输出目录的 <c>config/io-points.json</c>，与写该文件的用例同集合，禁用并行。</para>
/// </summary>
[Collection("config-files")]
public sealed class IoBitAreaFallbackTest
{
    /// <summary>Y 区 Modbus 基址（Y0 = 64512），与 X 区相差 1024。</summary>
    private const int YBaseAddress = 64512;

    private static async Task WaitUntilAsync(Func<bool> condition, string Description, int TimeoutMilliseconds = 8000)
    {
        var truncateStopTime = Environment.TickCount64 + TimeoutMilliseconds;

        while (Environment.TickCount64 < truncateStopTime)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"等待超时：{Description}");
    }

    /// <summary>构建采集环境并启动轮询（快组 50ms；含一个 M 点标签用于验证其它采集不受牵连）。</summary>
    private static async Task<(PollingEngine engine, FakeBitAreaClient Client, IoPointService ioTable, TagValueService tagValue)> BuildSamplingEnvironment()
    {
        var Client = new FakeBitAreaClient();
        var ioTable = new IoPointService(new AllowAllPermissionGuard(), NullLogger<IoPointService>.Instance);

        // io-points.json 里的启用点必须还在 —— 否则本测试失去意义（防止读到被别的用例改小的表）
        Assert.NotNull(ioTable.FindPoint("X0"));
        Assert.NotNull(ioTable.FindPoint("X2"));
        Assert.NotNull(ioTable.FindPoint("Y0"));
        Assert.NotNull(ioTable.FindPoint("Y10"));

        // 一个非物理 IO 段的快组标签（M200）：用于证明 X/Y 块的失败不牵连其它采集
        var Repository = new FakeTagRepository(new TagDefinition
        {
            Name = "测试M200",
            Address = "M200",
            DataType = TagDataType.Boolean,
            AccessQuery = TagAccessQuery.Read,
            Group = TagGroup.Fast,
        });

        var tagValue = new TagValueService(NullLogger<TagValueService>.Instance);

        var engine = new PollingEngine(
            Client,
            Repository,
            ioTable,
            new InovanceAddressParser(PlcSeries.SmallPlc),
            tagValue,
            new PollingOptions { FastIntervalMilliseconds = 50, SlowIntervalMilliseconds = 100, EventIntervalMilliseconds = 100 },
            new PlcConnectionOptions(),
            NullLogger<PollingEngine>.Instance);

        await engine.StartAsync();

        return (engine, Client, ioTable, tagValue);
    }

    // ── 形态①：X 的首位区（离散输入）读不通 → 异常回退到线圈区 ──────────

    [Fact]
    public async Task RealDevice_DiscreteInputAreaNoneShouldRespondWhenShouldFallbackCoilAreaAndFollowPhysicalState()
    {
        var (engine, Client, ioTable, _) = await BuildSamplingEnvironment();

        await using (engine)
        {
            Client.XYDiscreteRowAs = BitAreaRowAs.ThrowException;    // X 的首位区读不通（该机型没有这块离散输入）

            // 物理状态：X0 上料到位、X2 安全门常闭、X7 急停常闭、Y0 气缸、Y10 蜂鸣器
            Client.SetPhysicalState(FakeBitAreaClient.XBaseAddress + 0, true);
            Client.SetPhysicalState(FakeBitAreaClient.XBaseAddress + 2, true);
            Client.SetPhysicalState(FakeBitAreaClient.XBaseAddress + 7, true);
            Client.SetPhysicalState(YBaseAddress + 0, true);
            Client.SetPhysicalState(YBaseAddress + 8, true);

            await WaitUntilAsync(() => ioTable.GetState("X0").timeStamp != DateTime.MinValue, "X0 首轮采集");
            await WaitUntilAsync(() => ioTable.GetState("Y0").timeStamp != DateTime.MinValue, "Y0 首轮采集");

            // 展示值 = 真机物理状态（常闭点按配置反相）
            Assert.Equal(!ioTable.FindPoint("X0")!.Negate, ioTable.GetState("X0").IsOpenStart);
            Assert.Equal(!ioTable.FindPoint("X2")!.Negate, ioTable.GetState("X2").IsOpenStart);
            Assert.Equal(!ioTable.FindPoint("X7")!.Negate, ioTable.GetState("X7").IsOpenStart);
            Assert.True(ioTable.GetState("Y0").IsOpenStart, "Y0 气缸物理闭合时应为 ON");
            Assert.True(ioTable.GetState("Y10").IsOpenStart, "Y10 蜂鸣器物理闭合时应为 ON");

            // 对比项：未闭合的点必须为 OFF（防止"读出全 1"这类假成功）
            Assert.False(ioTable.GetState("X1").IsOpenStart, "X1 物理断开时应为 OFF");
            Assert.False(ioTable.GetState("X10").IsOpenStart, "X10 物理断开时应为 OFF");

            // 实时性：物理状态变化后展示值必须跟着变（不是停在首轮的固定值）
            Client.SetPhysicalState(FakeBitAreaClient.XBaseAddress + 0, false);
            await WaitUntilAsync(() => !ioTable.GetState("X0").IsOpenStart, "X0 物理断开后应变为 OFF");

            Client.SetPhysicalState(FakeBitAreaClient.XBaseAddress + 0, true);
            await WaitUntilAsync(() => ioTable.GetState("X0").IsOpenStart, "X0 物理闭合后应恢复 ON");
        }
    }

    // ── 形态②：X 的首位区读取成功但恒 0、另一位区有物理信号 → 自愈切换 ──

    [Fact]
    public async Task RealDevice_DiscreteInputAreaAlwaysReadZeroWhenShouldSwitchToHasSignalCoilAreaAndFollowPhysicalState()
    {
        var (engine, Client, ioTable, _) = await BuildSamplingEnvironment();

        await using (engine)
        {
            Client.XYDiscreteRowAs = BitAreaRowAs.AlwaysReadZero;    // 离散区读取"成功"但恒 0（该机型 X 在线圈表）
            Client.XYCoilRowAs = BitAreaRowAs.ByPhysicalState; // 物理状态在线圈区

            // 初始全部断开（两个位区都是 0）：此时不应有值，也不该误切
            await WaitUntilAsync(() => ioTable.GetState("X0").timeStamp != DateTime.MinValue, "X0 首轮采集");
            Assert.False(ioTable.GetState("X0").IsOpenStart, "物理断开时应为 OFF");

            // 任一点闭合 → 下一周期探测到线圈区的信号 → 切换并展示
            Client.SetPhysicalState(FakeBitAreaClient.XBaseAddress + 0, true);
            await WaitUntilAsync(() => ioTable.GetState("X0").IsOpenStart, "X0 闭合后应经线圈区读到 ON");

            // Y 一直在线圈区（默认），同样应读到物理状态
            Client.SetPhysicalState(YBaseAddress + 0, true);
            await WaitUntilAsync(() => ioTable.GetState("Y0").IsOpenStart, "Y0 闭合后应读到 ON");

            // 切换之后持续跟随物理变化（100ms 周期实时性）
            Client.SetPhysicalState(FakeBitAreaClient.XBaseAddress + 0, false);
            await WaitUntilAsync(() => !ioTable.GetState("X0").IsOpenStart, "X0 断开后应回到 OFF");

            Client.SetPhysicalState(FakeBitAreaClient.XBaseAddress + 0, true);
            await WaitUntilAsync(() => ioTable.GetState("X0").IsOpenStart, "X0 再次闭合后应恢复 ON");

            // 常闭点经切换后采集仍按配置反相（X2 取反：原始闭合 → 逻辑 OFF）
            var x2RefreshBefore = ioTable.GetState("X2").timeStamp;
            var x2ExpectedValue = !ioTable.FindPoint("X2")!.Negate;
            Client.SetPhysicalState(FakeBitAreaClient.XBaseAddress + 2, true);
            await WaitUntilAsync(
                () => ioTable.GetState("X2").timeStamp > x2RefreshBefore
                      && ioTable.GetState("X2").IsOpenStart == x2ExpectedValue,
                "X2 闭合后应刷新为反相逻辑值");
            Assert.Equal(x2ExpectedValue, ioTable.GetState("X2").IsOpenStart);

            // 切换成功后走的是线圈区：应随周期持续读取（取样后再比较，避免与用例耗时耦合）
            var switchAfterCoilReadCount = Client.XSectionCoilReadCount;
            await Task.Delay(300);

            Assert.True(Client.XSectionCoilReadCount > switchAfterCoilReadCount,
                $"线圈区应持续读取，实际 {Client.XSectionCoilReadCount}");
        }
    }

    // ── 标准固件：X 就在离散输入区 → 不得误切，空闲时零探测开销 ────────

    [Fact]
    public async Task StandardFirmItem_DiscreteInputAreaHasSignalWhenMustNotSwitchToCoilArea()
    {
        var (engine, Client, ioTable, _) = await BuildSamplingEnvironment();

        await using (engine)
        {
            Client.XYDiscreteRowAs = BitAreaRowAs.ByPhysicalState;  // 离散区就是物理 IO（当前口径的默认）
            Client.XYCoilRowAs = BitAreaRowAs.ThrowException;       // 备用位区不可用（不该被探到）

            Client.SetPhysicalState(FakeBitAreaClient.XBaseAddress + 0, true);
            Client.SetPhysicalState(YBaseAddress + 0, true);     // 两个块都有信号：都不该探测备用位区

            await WaitUntilAsync(() => ioTable.GetState("X0").IsOpenStart, "X0 应从离散输入区读到 ON");

            // 有信号时不探测备用位区（零开销）——Y 走线圈区，故只断言 X 地址段
            Assert.Equal(0, Client.XSectionCoilReadCount);

            // 全部断开（全 0）→ 离散区已证明是活的（X0 曾闭合）→ 不再探测备用位区。
            // 2026-10-15 修复前：此处会探测备用区，连续 3 次失败后停——而一旦备用区
            // 返回无关位（失效形态③），整块就被永久切到错误位区（现场 X 区错乱的真因）。
            Client.SetPhysicalState(FakeBitAreaClient.XBaseAddress + 0, false);
            await WaitUntilAsync(() => !ioTable.GetState("X0").IsOpenStart, "X0 断开后应为 OFF");

            var xTimeStamp = ioTable.GetState("X0").timeStamp;

            await Task.Delay(350);

            // ① 主区被证明是活的之后，任何全 0 都不再触发探测（零开销、无误切风险）
            Assert.Equal(0, Client.XSectionCoilReadCount);

            // ② 主通道不受影响：离散输入区仍每周期读取、时间戳推进
            Assert.True(Client.XSectionDiscreteReadCount > 6,
                $"离散输入区应持续读取，实际 {Client.XSectionDiscreteReadCount}");
            Assert.True(ioTable.GetState("X0").timeStamp > xTimeStamp, "X0 时间戳应随周期推进");

            // ③ 物理信号恢复时主通道立即恢复展示（没有误切到线圈区）
            Client.SetPhysicalState(FakeBitAreaClient.XBaseAddress + 0, true);
            await WaitUntilAsync(() => ioTable.GetState("X0").IsOpenStart, "X0 闭合后应恢复 ON");
        }
    }

    // ── 2026-10-15 现场回归：主区活着 + 备用区有无关位 → 绝不被误导 ────

    /// <summary>
    /// 用户现场问题（2026-10-15）：X0–X7 / X10–X17 显示与物理对不上，Y 与其余 X 正常。
    /// <para>形态：主位区本来就是权威位区（X 曾闭合过），机器空闲时输入全部物理断开 →
    /// 主位区读全 0 → 旧逻辑探测备用区 → 备用区同地址段放着无关数据 → 整块被永久切到
    /// 错误位区。始终有信号的块（Y、其余 X）从未探测所以正常——正是报障的分布特征。</para>
    /// </summary>
    [Fact]
    public async Task RealDevice_MasterAreaEverHasSignalWhenMustNotBeSpareAreaNoneCloseBitWrongDerive()
    {
        var (engine, Client, ioTable, _) = await BuildSamplingEnvironment();

        await using (engine)
        {
            Client.XYDiscreteRowAs = BitAreaRowAs.ByPhysicalState;  // 离散区就是物理 IO（权威主区）
            Client.XYCoilRowAs = BitAreaRowAs.ByPhysicalState;  // 备用区也有数据（无关/杂散）

            // X0 先闭合一次：证明离散区是活的；Y0 常闭：Y 块同样证明活性、不参与探测
            Client.SetPhysicalState(FakeBitAreaClient.XBaseAddress + 0, true);
            Client.SetPhysicalState(YBaseAddress + 0, true);
            await WaitUntilAsync(() => ioTable.GetState("X0").IsOpenStart, "X0 首轮应读到 ON");

            // 机器空闲：全部输入物理断开；同时备用区（线圈区）出现无关位
            Client.SetPhysicalState(FakeBitAreaClient.XBaseAddress + 0, false);
            Client.SetCoilAdditionalBit(FakeBitAreaClient.XBaseAddress + 5, true);   // X6 的杂散位
            Client.SetCoilAdditionalBit(FakeBitAreaClient.XBaseAddress + 11, true);  // X13 的杂散位

            await WaitUntilAsync(() => !ioTable.GetState("X0").IsOpenStart, "X0 物理断开后应为 OFF");

            var xTimeStamp = ioTable.GetState("X0").timeStamp;

            await Task.Delay(350);

            // ① 绝不被误导：主区证明过活性后，即使备用区有无关位也不探测、不切换
            Assert.Equal(0, Client.XSectionCoilReadCount);

            // ② 展示值始终跟随物理状态（杂散位不得漏到任何 X 点上）
            Assert.False(ioTable.GetState("X0").IsOpenStart, "X0 应保持 OFF");
            Assert.False(ioTable.GetState("X6").IsOpenStart, "X6 未启用也不得显示杂散位的 ON");

            // ③ 时间戳持续推进（采集没有停）
            Assert.True(ioTable.GetState("X0").timeStamp > xTimeStamp, "X0 时间戳应随周期推进");

            // ④ 物理信号恢复后立即正确展示
            Client.SetPhysicalState(FakeBitAreaClient.XBaseAddress + 0, true);
            await WaitUntilAsync(() => ioTable.GetState("X0").IsOpenStart, "X0 闭合后应恢复 ON");
        }
    }

    // ── 配置覆盖：io-points.json 元 X区域/Y区域 显式指定位区 ────

    /// <summary>
    /// 机型把 X 放在线圈表（老固件 / 非标准映射）时，自动探测分不清"位区失效"与"无关数据"，
    /// 用配置显式指定 <c>X区域=线圈</c> 直接走 FC01，不猜、不切。
    /// </summary>
    [Fact]
    public async Task ConfigOverwrite_XAreaCoilWhenShouldDirectConnectUseFC01Read()
    {
        var (engine, Client, ioTable, _) = await BuildSamplingEnvironment();

        // 显式覆盖：X 区位区 = 线圈（与默认相反，验证覆盖在两个方向都有效）
        ioTable.DataGrid.Element.XArea = "线圈";

        // 覆盖后重建采集计划才会生效（与现场改 io-points.json 后重启同路径）
        await engine.RebuildPlanListAsync();

        await using (engine)
        {
            Client.XYCoilRowAs = BitAreaRowAs.ByPhysicalState;
            Client.XYDiscreteRowAs = BitAreaRowAs.ByPhysicalState;

            Client.SetPhysicalState(FakeBitAreaClient.XBaseAddress + 0, true);

            await WaitUntilAsync(() => ioTable.GetState("X0").IsOpenStart, "X0 应经线圈区读到 ON");

            var overwriteAfterXSectionCoilReadCount = Client.XSectionCoilReadCount;
            var overwriteAfterXSectionDiscreteReadCount = Client.XSectionDiscreteReadCount;

            await Task.Delay(350);

            // X 块只走线圈区：线圈读次数持续增长，离散读次数不再变化
            Assert.True(Client.XSectionCoilReadCount > overwriteAfterXSectionCoilReadCount,
                $"线圈区应持续读取，实际 {Client.XSectionCoilReadCount}");
            Assert.Equal(overwriteAfterXSectionDiscreteReadCount, Client.XSectionDiscreteReadCount);
        }
    }

    // ── 共同约束：单块故障不牵连同周期其它采集 ───────────────────────────

    [Fact]
    public async Task RealDevice_BitAreaSwitchOnlyProbeOnceAndNotDisconnectOtherSampling()
    {
        var (engine, Client, ioTable, tagValue) = await BuildSamplingEnvironment();

        await using (engine)
        {
            // X 的首位区（离散输入）读不通：抛异常 → 回退线圈区并记住
            Client.XYDiscreteRowAs = BitAreaRowAs.ThrowException;

            // 等首轮把 X / Y 两块都跑完
            await WaitUntilAsync(
                () => ioTable.GetState("X0").timeStamp != DateTime.MinValue
                      && ioTable.GetState("Y0").timeStamp != DateTime.MinValue
                      && tagValue.GetNewValue("测试M200").IsValid,
                "X / Y 首轮采集");

            // 回退后的空闲探测也要收敛（连续 3 次失败后停止）后再取样
            await Task.Delay(400);

            var xTimeStamp = ioTable.GetState("X0").timeStamp;
            var tagTimeStamp = tagValue.GetNewValue("测试M200").timeStamp;
            var discreteTryCount = Client.PhysicalIoDiscreteReadCount;

            // 再跑若干个快组周期（50ms × 6 以上）
            await Task.Delay(400);

            // ① 位区选择已收敛：不再回到读不通的离散区反复试错
            Assert.Equal(discreteTryCount, Client.PhysicalIoDiscreteReadCount);

            // ② X / Y 持续刷新（不是首轮之后冻结在默认值）
            Assert.True(ioTable.GetState("X0").timeStamp > xTimeStamp, "X0 时间戳应随周期推进");
            Assert.True(ioTable.GetState("Y0").timeStamp != DateTime.MinValue, "Y0 应被持续采集");

            // ③ 其它采集不受牵连：同周期的标签轮询照常运行
            Assert.True(tagValue.GetNewValue("测试M200").timeStamp > tagTimeStamp, "标签时间戳应随周期推进");
            Assert.True(Client.TagCoilReadCount > 6,
                $"非物理 IO 段的线圈读取应持续进行，实际 {Client.TagCoilReadCount}");
        }
    }
}
