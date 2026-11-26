using Microsoft.Extensions.Logging.Abstractions;
using 上位机.业务.配置;
using 上位机.业务.服务;
using 上位机.通信.汇川;
using 上位机.通信.Modbus;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;
using 上位机.上位机测试.替身;

namespace 上位机.上位机测试;

/// <summary>
/// 辅助地址「批量合并读取」回归（2026-11-19 性能改造）。
///
/// <para><b>守的是什么：</b>辅助地址上限提到 30 行后，周期回读不能退化成"一行一次请求"。
/// 合并读取必须与逐行读取 <see cref="页面地址服务.异步读取辅助地址"/> 得到完全相同的值，
/// 且把连续地址压成少量请求；开关项与无效项按 null 跳过，不影响其它项。</para>
/// </summary>
public class 辅助地址批量读取测试
{
    private static 页面地址服务 新服务(Modbus客户端接口 连接)
    {
        var 解析器 = new 汇川地址解析器(PLC系列.小型PLC);

        return new 页面地址服务(
            连接,
            new 标签写入器(连接, new 标签仓储(NullLogger<标签仓储>.Instance), 解析器,
                NullLogger<标签写入器>.Instance),
            配方配置加载器.加载(out _),
            new 允许全部权限守卫(),
            NullLogger<页面地址服务>.Instance);
    }

    [Fact]
    public async Task 批量读取应与逐行读取逐项一致()
    {
        await using var 客户端 = new 替身Modbus客户端();
        await 客户端.异步连接();

        var 服务实例 = 新服务(客户端);

        // 用 8 个离散的 D 区固定地址（Float32 占 2 字），预设可区分解码值。
        // 不用虚拟 PLC：它的状态机会周期性改写部分 D 区，写入值不保证原样留下。
        var 项列表 = Enumerable.Range(0, 8)
            .Select(序号 => new 辅助地址项
            {
                键 = $"一致{序号}",
                名称 = $"一致{序号}",
                地址 = $"D{2000 + 序号 * 8}",   // 间隔 8 字，确保互不重叠
                数据类型 = 标签数据类型.Float32,
                分组 = "锁附",
            })
            .ToList();

        for (var 序号 = 0; 序号 < 项列表.Count; 序号++)
        {
            var 值 = 10.5f + 序号;
            var 字 = 寄存器编解码器.编码(标签数据类型.Float32, 值);
            客户端.预设保持寄存器(2000 + 序号 * 8, 字[0]);
            客户端.预设保持寄存器(2000 + 序号 * 8 + 1, 字[1]);
        }

        // 逐行读取的基准
        var 基准 = new double[项列表.Count];
        for (var 序号 = 0; 序号 < 项列表.Count; 序号++)
        {
            基准[序号] = await 服务实例.异步读取辅助地址(项列表[序号]);
        }

        // 批量读取：值与顺序都必须对齐
        var 批量 = await 服务实例.异步批量读取辅助地址(项列表);

        Assert.Equal(项列表.Count, 批量.Count);
        for (var 序号 = 0; 序号 < 项列表.Count; 序号++)
        {
            Assert.NotNull(批量[序号]);
            Assert.Equal(基准[序号], 批量[序号]!.Value, 2);
            Assert.Equal(10.5 + 序号, 批量[序号]!.Value, 2);
        }
    }

    [Fact]
    public async Task 批量读取应把连续地址合并成少量请求()
    {
        await using var 客户端 = new 替身Modbus客户端();
        await 客户端.异步连接();
        var 服务实例 = 新服务(客户端);

        // 人为构造 30 个连续 D 区项（模拟扩到 30 行后的最坏情况）
        var 项列表 = Enumerable.Range(0, 30)
            .Select(序号 => new 辅助地址项
            {
                键 = $"批量{序号}",
                名称 = $"批量{序号}",
                地址 = $"D{3000 + 序号 * 2}",
                数据类型 = 标签数据类型.Float32,
                分组 = "锁附",
            })
            .ToList();

        var 结果 = await 服务实例.异步批量读取辅助地址(项列表);

        Assert.Equal(30, 结果.Count);
        // D3000–D3058 跨度 59 字 < 上限 100，应合成 1 次读
        Assert.Equal(1, 客户端.保持寄存器读取次数);
        Assert.All(结果, 值 => Assert.NotNull(值));
    }

    [Fact]
    public async Task 未连接时批量读取应全部跳过()
    {
        await using var 客户端 = new 替身Modbus客户端();
        var 服务实例 = 新服务(客户端);

        var 结果 = await 服务实例.异步批量读取辅助地址(
        [
            new 辅助地址项 { 键 = "甲", 地址 = "D100", 数据类型 = 标签数据类型.Float32 },
        ]);

        Assert.Single(结果);
        Assert.Null(结果[0]);
        Assert.Equal(0, 客户端.保持寄存器读取次数);
    }

    [Fact]
    public async Task 开关项应返回null且不干扰其它项()
    {
        await using var 客户端 = new 替身Modbus客户端();
        await 客户端.异步连接();

        var 服务实例 = 新服务(客户端);

        var 数值项 = new 辅助地址项
        {
            键 = "数值", 名称 = "数值", 地址 = "D100",
            数据类型 = 标签数据类型.Float32, 分组 = "锁附",
        };
        var 开关项 = new 辅助地址项
        {
            键 = "开关", 名称 = "开关", 地址 = "M200",
            数据类型 = 标签数据类型.布尔, 分组 = "锁附",
        };

        var 字 = 寄存器编解码器.编码(标签数据类型.Float32, 7.25);
        客户端.预设保持寄存器(100, 字[0]);
        客户端.预设保持寄存器(101, 字[1]);

        var 结果 = await 服务实例.异步批量读取辅助地址([开关项, 数值项]);

        Assert.Equal(2, 结果.Count);
        Assert.Null(结果[0]);                 // 开关不参与 D 区合并
        Assert.NotNull(结果[1]);
        Assert.Equal(7.25, 结果[1]!.Value, 2);
    }
}
