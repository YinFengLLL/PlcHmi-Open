using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using 上位机.业务.配置;
using 上位机.业务.服务;
using 上位机.通信.汇川;
using 上位机.通信.仿真;
using 上位机.核心.枚举;
using 上位机.核心.模型;
using 上位机.上位机测试.替身;

namespace 上位机.上位机测试;

/// <summary>
/// 2026-10-15 算法优化与缺陷修复的回归测试。
///
/// <para><b>守的是什么：</b>本轮修掉的四类问题各有一条"改回去就会红"的断言——
/// R1（模拟器 32 位写入绕过编解码器）、R4（按点位名写线圈绕过 M 区校验）、
/// CSV 导入缺重名/引号校验、延时换算的 double 域边界。都是"注释声称安全、
/// 实际路径没走到"型的缺陷，普通读写用例碰不到。</para>
/// </summary>
public class 审查修复回归测试
{
    // ── R1：模拟器 Float32 写入必须按 CDAB（低字在低地址）─────────────

    /// <summary>
    /// 虚拟 PLC 的 32 位写入必须走 <c>寄存器编解码器</c>：定位速度 80.00f 的
    /// 低字应在低地址（D1400）、高字在高地址（D1401）。
    /// <para>若有人把编解码器调用改回手写字序且写反（大端在前），这里立刻红——
    /// 模拟器与真实客户端字序一旦分歧，全部自测结论在真机上失效。</para>
    /// </summary>
    [Fact]
    public async Task 模拟器_定位速度应按CDAB低字在前写入()
    {
        await using var 设备 = new 虚拟PLC设备(NullLogger<虚拟PLC设备>.Instance);

        // 独立复算期望字序（不经过寄存器编解码器，防止循环论证）
        var 位模式 = BitConverter.SingleToUInt32Bits(80.00f);
        var 期望低字 = (ushort)(位模式 & 0xFFFF);
        var 期望高字 = (ushort)(位模式 >> 16);

        var 低字 = 设备.内存.不安全获取保持寄存器(1400);   // D1400 锁附 X 轴定位速度
        var 高字 = 设备.内存.不安全获取保持寄存器(1401);

        Assert.Equal(期望低字, 低字);
        Assert.Equal(期望高字, 高字);

        // 反序（大端在前）必须与正序不同，否则本用例失去区分度
        Assert.NotEqual(低字, 高字);
    }

    // ── R4：按点位名写线圈必须校验 M 区 ──────────────────────────────

    /// <summary>
    /// tags.json 里的点位被标为可写且地址是 Y 区时，按点位名写布尔必须被拒绝。
    /// <para>曾经只有"按地址写线圈"有 M 区校验，按点位名的路径没有——
    /// 配置文件被手工改错即可越权驱动物理输出（上位机永不越权的最后一道防线）。</para>
    /// </summary>
    [Fact]
    public async Task 按点位名写线圈_非M区应被拒绝()
    {
        await using var 客户端 = new 替身Modbus客户端();

        var 写入器 = new 标签写入器(
            客户端,
            new 替身标签仓储(new 标签定义
            {
                名称 = "Y输出点",
                地址 = "Y0",
                数据类型 = 标签数据类型.布尔,
                访问 = 标签访问.写入,
                分组 = 标签分组.命令,
            }),
            new 汇川地址解析器(PLC系列.小型PLC),
            NullLogger<标签写入器>.Instance);

        var 异常 = await Assert.ThrowsAsync<InvalidOperationException>(
            () => 写入器.异步写入布尔("Y输出点", true));

        Assert.Contains("M 区", 异常.Message);
        Assert.Empty(客户端.线圈写入);   // 拒绝时必须没有任何写发生
    }

    /// <summary>正对照：M 区点位的按名写入应照常放行（守卫不误伤合法路径）。</summary>
    [Fact]
    public async Task 按点位名写线圈_M区应放行()
    {
        await using var 客户端 = new 替身Modbus客户端();

        var 写入器 = new 标签写入器(
            客户端,
            new 替身标签仓储(new 标签定义
            {
                名称 = "M命令点",
                地址 = "M200",
                数据类型 = 标签数据类型.布尔,
                访问 = 标签访问.写入,
                分组 = 标签分组.命令,
            }),
            new 汇川地址解析器(PLC系列.小型PLC),
            NullLogger<标签写入器>.Instance);

        await 写入器.异步写入布尔("M命令点", true);

        Assert.Single(客户端.线圈写入);
    }

    // ── CSV 导入：重名与空名必须整体拒绝 ──────────────────────────────

    /// <summary>
    /// 其它点 CSV 导入出现重名时必须整体拒绝：名称是"按名查找 / 删除 / CSV 交换"
    /// 的唯一依据，导入后出现同名点会让后续操作指向错误的点。
    /// <para>曾经 异步新增/异步编辑 都查重，唯独导入路径不查。</para>
    /// </summary>
    [Fact]
    public async Task 其它点CSV导入_重名应整体拒绝()
    {
        var 临时文件 = Path.Combine(Path.GetTempPath(), $"aux-dup-{Guid.NewGuid():N}.csv");

        try
        {
            await File.WriteAllTextAsync(临时文件,
                "# 测试\r\n名称,X基址,Y基址,Z基址\r\n点甲,2500,2502,2504\r\n点甲,2600,2602,2604\r\n",
                new UTF8Encoding(true));

            await using var 客户端 = new 替身Modbus客户端();
            var 服务实例 = 新辅助点服务(客户端);

            var 异常 = await Assert.ThrowsAsync<InvalidOperationException>(
                () => 服务实例.异步从文件导入CSV(临时文件));

            Assert.Contains("重复", 异常.Message);
        }
        finally
        {
            if (File.Exists(临时文件))
            {
                File.Delete(临时文件);
            }
        }
    }

    /// <summary>
    /// 名称含逗号必须能原样往返：导出用引号包裹（<c>退出()</c>），
    /// 导入曾经裸 <c>Split(',')</c> 把 "A,B" 切成 5 段——往返失真。
    /// </summary>
    [Fact]
    public async Task 其它点CSV导入_带引号的名称应原样还原()
    {
        var 临时文件 = Path.Combine(Path.GetTempPath(), $"aux-quote-{Guid.NewGuid():N}.csv");

        try
        {
            // 与导出口径一致：含逗号的名称加双引号
            await File.WriteAllTextAsync(临时文件,
                "名称,X基址,Y基址,Z基址\r\n\"带,逗号\",2500,2502,2504\r\n普通点,2600,2602,2604\r\n",
                new UTF8Encoding(true));

            await using var 客户端 = new 替身Modbus客户端();
            var 服务实例 = 新辅助点服务(客户端);

            var 数量 = await 服务实例.异步从文件导入CSV(临时文件);

            Assert.Equal(2, 数量);

            var 带逗号点 = 服务实例.点位集合.Single(点项 => 点项.名称 == "带,逗号");
            Assert.Equal(2500, 带逗号点.X基址);   // 列不能错位
            Assert.Equal(2504, 带逗号点.Z基址);
        }
        finally
        {
            if (File.Exists(临时文件))
            {
                File.Delete(临时文件);
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
    public async Task 延时换算_超出32位范围应明确报错()
    {
        var 解析器 = new 汇川地址解析器(PLC系列.小型PLC);

        标签写入器 写入器 = null!;   // 校验先于写 PLC，用不到
        var 服务实例 = new 参数表格服务(
            解析器, 写入器, NullLogger<参数表格服务>.Instance, new 允许全部权限守卫());

        var 条目 = new 参数条目定义
        {
            标识 = "test-delay",
            名称 = "测试延时",
            地址 = "D1000",
            单元 = "s",
            最小 = 0,
            最大 = 5_000_000_000,   // 故意放大上限：让"值合法但毫秒超界"的场景可达
            启用 = true,
            值 = 4294967.296,      // ×1000 = 4294967296，超出 Int32 上限（2147483647）
        };

        var 异常 = await Assert.ThrowsAsync<InvalidOperationException>(
            () => 服务实例.异步写入延迟(条目));

        Assert.Contains("32 位整数（DINT）范围", 异常.Message);
    }

    /// <summary>
    /// 延时按 32 位整数（DINT）下发：占 2 个字、低字在前（CDAB）。
    /// <para>16 位时代只有 65.535 S 上限（2026-10-16 现场要求改 DINT）；字序必须与
    /// 轴速度 Float32 / 机种快照同源（红线 R1），所以这里直接按 IEEE 位模式独立复算。</para>
    /// </summary>
    [Fact]
    public async Task 延时下发_应按DINT占两字且低字在前()
    {
        // 下发成功会静默落盘（保证"PLC 里的值 = 配置文件里的值"）：本用例改的是测试输出目录的
        // param-tables.json，跑完必须还原，否则同集合的其它用例会读到被清空的延时表
        var 表格路径 = 配置路径.参数表格;
        var 原始 = await File.ReadAllTextAsync(表格路径);

        try
        {
            var 解析器 = new 汇川地址解析器(PLC系列.小型PLC);

            await using var 客户端 = new 替身Modbus客户端();
            var 写入器 = new 标签写入器(
                客户端, new 替身标签仓储(), 解析器, NullLogger<标签写入器>.Instance);

            var 服务实例 = new 参数表格服务(
                解析器, 写入器, NullLogger<参数表格服务>.Instance, new 允许全部权限守卫());

            // 先初始化：这样"静默落盘"写回的是与磁盘等价的内容，不会把配置写成空表
            // （本用例与读取同一份配置的 延迟地址范围测试 并行运行）
            await 服务实例.异步初始化();

            // 30.000 S → 30000 ms：16 位装得下，用于验证字数与字序。
            // D1090 落在延时专属区尾部且不与配置里的 D1000/D1002/D1004 冲突。
            var 条目 = new 参数条目定义
            {
                标识 = "test-delay",
                名称 = "测试延时",
                地址 = "D1090",
                单元 = "s",
                最小 = 0,
                最大 = 2147483.647,
                启用 = true,
                值 = 30.0,
            };

            await 服务实例.异步写入延迟(条目);

            var 块 = Assert.Single(客户端.寄存器块写入);
            Assert.Equal(1090, 块.地址);          // D1090 的 Modbus 地址
            Assert.Equal(2, 块.值.Count);         // DINT 占 2 字

            var 位模式 = (uint)30000;
            Assert.Equal((ushort)(位模式 & 0xFFFF), 块.值[0]);   // 低字在前（CDAB）
            Assert.Equal((ushort)(位模式 >> 16), 块.值[1]);

            // 65.536 S（16 位装不下）仍能正确下发：高字为 1 —— 这正是本次改 DINT 的目的
            var 长延时 = 条目.克隆();
            长延时.值 = 65.536;                    // 65536 ms

            await 服务实例.异步写入延迟(长延时);

            var 第二块 = 客户端.寄存器块写入[^1];
            Assert.Equal((ushort)0, 第二块.值[0]);
            Assert.Equal((ushort)1, 第二块.值[1]);
        }
        finally
        {
            await File.WriteAllTextAsync(表格路径, 原始);
        }
    }

    // ── 辅助 ───────────────────────────────────────────────────────────

    private static 辅助点服务 新辅助点服务(替身Modbus客户端 客户端) =>
        new(
            客户端,
            配方配置加载器.加载(out _),
            new 允许全部权限守卫(),
            NullLogger<辅助点服务>.Instance);
}
