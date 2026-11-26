using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using 上位机.业务;
using 上位机.业务.配置;
using 上位机.业务.服务;
using 上位机.通信.Modbus;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.上位机测试;

/// <summary>
/// 历史记录采集的端到端测试（虚拟 PLC）。
///
/// <para>验证「PLC 的触发位置位 → 读回配置里的各地址 → 按字序与小数位落成一条记录」这条完整链路。
/// 单元用例无法覆盖它：地址合并读取、上升沿判定、字序解码、按天落盘 都在真实服务里发生。</para>
///
/// <para>与 <c>config-files</c> 集合串行：本用例读的是真实 <c>config/history-record.json</c>，
/// 而 <see cref="历史记录测试"/> 会临时改写它。</para>
/// </summary>
[Collection("config-files")]
public class 历史记录集成测试
{
    [Fact]
    public async Task 触发位上升沿应落一条记录且按小数位显示()
    {
        var 设置 = 应用设置加载器.加载(out _);
        var 配方配置 = 配方配置加载器.加载(out _);

        设置.PLC连接.使用模拟器 = true;   // 虚拟 PLC：可写 D / M 后由采集服务读回

        var 服务集合 = new ServiceCollection();
        服务集合.AddLogging();
        服务集合.添加上位机服务(设置.PLC连接, 设置.轮询, 配方配置);
        服务集合.AddSingleton<权限守卫接口, 允许全部权限守卫>();

        await using var 提供者 = 服务集合.BuildServiceProvider(validateScopes: true);
        var 托管 = 提供者.GetServices<IHostedService>().ToList();

        foreach (var 服务实例 in 托管)
        {
            await 服务实例.StartAsync(CancellationToken.None);
        }

        try
        {
            var 历史 = 提供者.GetRequiredService<历史记录服务接口>();
            var 客户端 = 提供者.GetRequiredService<Modbus客户端接口>();
            var 今天 = DateTime.Today;

            Assert.True(历史.配置.启用, "默认配置应启用采集");
            Assert.True(历史.配置.数据项集合.Count > 0, "默认配置应有数据项");

            var 之前条数 = (await 历史.异步读取日(今天)).Count;

            // 地址一律取自配置（现场版：触发 M8、实际扭力 D218 INT16 小数位 3），
            // 避免"工厂配置换一次地址就红一片"。
            var 触发地址 = 历史.配置.触发地址.Trim();
            var 触发位 = int.Parse(触发地址.AsSpan(1), CultureInfo.InvariantCulture);

            var 扭力序号 = 历史.配置.数据项集合.FindIndex(项 => 项.名称 == "实际扭力");
            Assert.True(扭力序号 >= 0, "出厂配置里应有「实际扭力」列");

            var 扭力项 = 历史.配置.数据项集合[扭力序号];
            var 扭力地址 = int.Parse(扭力项.地址.AsSpan(1), CultureInfo.InvariantCulture);

            // INT16 单字：600 × 10⁻³ = 0.600
            await 客户端.异步写入单个寄存器(扭力地址, 600);

            // 触发位上升沿：只有 0 → 1 的沿才记录
            await 客户端.异步写入单个线圈(触发位, true);

            await 异步等待直到(
                async () => (await 历史.异步读取日(今天)).Count > 之前条数,
                "触发位上升沿后应新增一条历史记录");

            var 记录 = (await 历史.异步读取日(今天))[^1];

            Assert.True(历史.配置.读取后复位触发位, "默认应开启读取后复位握手");
            Assert.Equal(历史.配置.数据项集合.Count, 记录.数值集合.Count);
            Assert.Equal(600d, 记录.数值集合[扭力序号]);
            Assert.Equal("0.600", 扭力项.显示文本(记录.数值集合[扭力序号]));

            // 握手闭环：读取完成后触发位应被上位机写回 0，
            // PLC 程序据此知道"数据已被取走"，可以开始下一个动作
            await 异步等待直到(
                async () =>
                {
                    var 线圈 = await 客户端.异步读取线圈(触发位, 1);
                    return 线圈.Length > 0 && !线圈[0];
                },
                "读取完成后触发位应被复位为 0");
        }
        finally
        {
            foreach (var 服务实例 in 托管)
            {
                await 服务实例.StopAsync(CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// 2026-10-24 现场要求：数据项支持 REAL（32 位浮点）。
    /// <para>把配置临时换成「一列 REAL + 一列 INT」，验证 PLC 侧两个字按配置字序解码后
    /// 直接得到工程量（3.75 就是 3.75，不再需要 /1000 之类的换算）。</para>
    /// </summary>
    [Fact]
    public async Task REAL数据项应按32位浮点读到工程量()
    {
        var 设置 = 应用设置加载器.加载(out _);
        var 配方配置 = 配方配置加载器.加载(out _);

        设置.PLC连接.使用模拟器 = true;
        设置.轮询.快间隔毫秒 = 50;
        设置.轮询.慢间隔毫秒 = 100;
        设置.轮询.事件间隔毫秒 = 100;

        var 服务集合 = new ServiceCollection();
        服务集合.AddLogging();
        服务集合.添加上位机服务(设置.PLC连接, 设置.轮询, 配方配置);
        服务集合.AddSingleton<权限守卫接口, 允许全部权限守卫>();

        var 配置备份 = await File.ReadAllTextAsync(配置路径.历史记录配置);

        try
        {
            await using var 提供者 = 服务集合.BuildServiceProvider(validateScopes: true);
            var 托管 = 提供者.GetServices<IHostedService>().ToList();

            foreach (var 服务实例 in 托管)
            {
                await 服务实例.StartAsync(CancellationToken.None);
            }

            try
            {
                var 历史 = 提供者.GetRequiredService<历史记录服务接口>();
                var 客户端 = 提供者.GetRequiredService<Modbus客户端接口>();
                var 今天 = DateTime.Today;

                await 历史.异步保存配置(new 历史记录配置
                {
                    标题 = "浮点记录",
                    触发地址 = "M8",
                    启用 = true,
                    保留天数 = 90,
                    数据项集合 =
                    [
                        new() { 名称 = "胶量", 地址 = "D1420", 数据类型 = 标签数据类型.Float32, 小数位 = 3, 单元 = "mg" },
                        new() { 名称 = "颗数", 地址 = "D1422", 数据类型 = 标签数据类型.Int16, 小数位 = 0 },
                    ],
                });

                // 保存配置会重建"上次触发位"基线：先等一轮扫描把 M8=0 记成基线，
                // 再置位才有 0 → 1 的沿（否则第一轮看到的就已经是 1，不会记录）
                await Task.Delay(700);

                await 客户端.异步写入多个寄存器(
                    1420, 寄存器编解码器.编码(标签数据类型.Float32, 3.75, 字顺序.小字节序));
                await 客户端.异步写入单个寄存器(1422, 7);

                var 之前条数 = (await 历史.异步读取日(今天)).Count;

                await 客户端.异步写入单个线圈(8, true);

                await 异步等待直到(
                    async () => (await 历史.异步读取日(今天)).Count > 之前条数,
                    "浮点数据项的触发位上升沿应新增一条记录");

                var 记录 = (await 历史.异步读取日(今天))[^1];

                Assert.Equal(3.75, 记录.数值集合[0], 3);
                Assert.Equal("3.750", 历史.配置.数据项集合[0].显示文本(记录.数值集合[0]));
                Assert.Equal(7d, 记录.数值集合[1]);
            }
            finally
            {
                foreach (var 服务实例 in 托管)
                {
                    await 服务实例.StopAsync(CancellationToken.None);
                }
            }
        }
        finally
        {
            await File.WriteAllTextAsync(配置路径.历史记录配置, 配置备份);
        }
    }

    private static async Task 异步等待直到(Func<Task<bool>> 条件, string 描述, int 超时毫秒 = 8000)
    {
        var 截止时间 = Environment.TickCount64 + 超时毫秒;

        while (Environment.TickCount64 < 截止时间)
        {
            if (await 条件())
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"等待超时：{描述}");
    }
}
