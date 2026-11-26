using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using 上位机.业务;
using 上位机.业务.配置;
using 上位机.业务.服务;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.上位机测试;

/// <summary>
/// 报警扫描的端到端测试。
///
/// <para>验证「PLC 某个位为 1」→「界面显示配置好的报警文本」这条完整链路，
/// 以及定义校验是否拦住了不该用的地址。</para>
/// </summary>
public class 报警扫描集成测试
{
    private static ServiceProvider 构建运行期()
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

        // 本组用例不验证权限边界（那由 PermissionGuardTests 覆盖），以受信任身份运行
        服务集合.AddSingleton<权限守卫接口, 允许全部权限守卫>();

        return 服务集合.BuildServiceProvider(validateScopes: true);
    }

    private static async Task 异步等待直到(Func<bool> 条件, string 描述, int 超时毫秒 = 8000)
    {
        var 截止时间 = Environment.TickCount64 + 超时毫秒;

        while (Environment.TickCount64 < 截止时间)
        {
            if (条件())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"等待超时：{描述}");
    }

    /// <summary>
    /// 启动全部宿主服务。
    /// <para>注意：报警扫描是独立的 hosted service，只启动 <c>GetRequiredService&lt;IHostedService&gt;()</c>
    /// 拿到的那个（运行时服务）不会把它带起来。</para>
    /// </summary>
    private static async Task 异步启动全部(IReadOnlyList<IHostedService> 服务集合)
    {
        foreach (var 服务实例 in 服务集合)
        {
            await 服务实例.StartAsync(CancellationToken.None);
        }

        // 给扫描器留出首轮时间
        await Task.Delay(700);
    }

    private static async Task 异步停止全部(IReadOnlyList<IHostedService> 服务集合)
    {
        foreach (var 服务实例 in 服务集合)
        {
            await 服务实例.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task 报警扫描应把PLC的位映射成配置好的报警文本()
    {
        await using var 提供者 = 构建运行期();
        var 托管 = 提供者.GetServices<IHostedService>().ToList();
        await 异步启动全部(托管);

        try
        {
            var 报警列表 = 提供者.GetRequiredService<报警服务接口>();
            var 定义服务 = 提供者.GetRequiredService<报警定义服务接口>();
            var 客户端 = 提供者.GetRequiredService<Modbus客户端接口>();

            // 现场出厂配置里启用的三条报警都是 PLC 程序自己写的位（M501–M503），
            // 而模拟器每个周期都会重写这一段 —— 本用例自带一条 M600 的测试报警：
            // M600 不在模拟器的报警模拟区，写进去不会被覆盖，链路照样是"PLC 位置位 → 界面文本"。
            定义服务.添加(new 报警定义
            {
                标识 = "A900",
                消息 = "急停（测试）",
                等级 = 报警等级.紧急,
                地址 = "M600",
                位 = -1,
                启用 = true,
            });

            // 等扫描跑起来
            await 异步等待直到(() => true, "扫描启动");
            await Task.Delay(700);

            await 客户端.异步写入单个线圈(600, true);

            await 异步等待直到(
                () => 报警列表.活动报警.Any(甲 => 甲.代码 == "A900"),
                "测试报警产生");

            var 已触发 = 报警列表.活动报警.First(甲 => 甲.代码 == "A900");
            Assert.Contains("急停", 已触发.消息);
            Assert.Equal(报警等级.紧急, 已触发.等级);

            // 电平报警：位归 0 即消除，并进入历史
            await 客户端.异步写入单个线圈(600, false);

            await 异步等待直到(
                () => !报警列表.活动报警.Any(甲 => 甲.代码 == "A900"),
                "测试报警消除");

            Assert.Contains(报警列表.历史, 甲 => 甲.代码 == "A900");
        }
        finally
        {
            await 异步停止全部(托管);
        }
    }

    [Fact]
    public async Task 报警地址只能落在D550到D554或M500到M600()
    {
        await using var 提供者 = 构建运行期();
        var 托管 = 提供者.GetServices<IHostedService>().ToList();
        await 异步启动全部(托管);

        try
        {
            var 定义列表 = 提供者.GetRequiredService<报警定义服务接口>();

            // 区域外：应一律被拒绝
            foreach (var 地址值 in new[] { "X0", "Y0", "D100", "D2000", "M100", "M700" })
            {
                var 无效 = new 核心.模型.报警定义
                {
                    标识 = "T001", 消息 = "越界地址测试", 地址 = 地址值, 位 = -1,
                };

                var 结果值 = 定义列表.校验(无效);

                Assert.False(结果值.是否有效, $"地址 {地址值} 本应被拒绝");
                Assert.Contains(结果值.错误集合, 事件参数 => 事件参数.Contains("必须落在"));
            }

            // M500–M600：合法
            foreach (var 地址值 in new[] { "M500", "M550", "M600" })
            {
                var 有效 = new 核心.模型.报警定义
                {
                    标识 = "T001", 消息 = "合法地址测试", 地址 = 地址值, 位 = -1,
                };

                Assert.True(定义列表.校验(有效).是否有效, $"地址 {地址值} 本应通过");
            }

            // D550–D554 必须指定位号 —— 不然一个 16 位的字只能放一条报警
            var 不含位 = new 核心.模型.报警定义
            {
                标识 = "T001", 消息 = "D 区未指定位号", 地址 = "D550", 位 = -1,
            };

            var D结果 = 定义列表.校验(不含位);
            Assert.False(D结果.是否有效);
            Assert.Contains(D结果.错误集合, 事件参数 => 事件参数.Contains("必须指定位号"));

            // 指定位号后合法
            var 位 = 不含位.克隆();
            位.位 = 3;
            Assert.True(定义列表.校验(位).是否有效);

            // 边界值：D554.15 应合法，D555.0 应被拒
            var D上限 = 位.克隆();
            D上限.地址 = "D554";
            D上限.位 = 15;
            Assert.True(定义列表.校验(D上限).是否有效);

            var D越界 = 位.克隆();
            D越界.地址 = "D555";
            Assert.False(定义列表.校验(D越界).是否有效);
        }
        finally
        {
            await 异步停止全部(托管);
        }
    }

    [Fact]
    public async Task 报警定义应校验文本与位号范围()
    {
        await using var 提供者 = 构建运行期();
        var 托管 = 提供者.GetServices<IHostedService>().ToList();
        await 异步启动全部(托管);

        try
        {
            var 定义列表 = 提供者.GetRequiredService<报警定义服务接口>();

            var 坏值 = new 核心.模型.报警定义
            {
                标识 = "T002", 消息 = string.Empty, 地址 = "M500", 位 = 99,
            };

            var 结果值 = 定义列表.校验(坏值);

            Assert.False(结果值.是否有效);
            Assert.Contains(结果值.错误集合, 事件参数 => 事件参数.Contains("报警文本"));
            Assert.Contains(结果值.错误集合, 事件参数 => 事件参数.Contains("位号"));
        }
        finally
        {
            await 异步停止全部(托管);
        }
    }

    [Fact]
    public async Task 默认报警配置应随程序一起加载()
    {
        await using var 提供者 = 构建运行期();
        var 托管 = 提供者.GetServices<IHostedService>().ToList();
        await 异步启动全部(托管);

        try
        {
            var 定义列表 = 提供者.GetRequiredService<报警定义服务接口>();

            // alarms.json 由测试项目随 config 一起复制，应至少有一条
            Assert.NotEmpty(定义列表.定义集合);
            Assert.Contains(定义列表.定义集合, 条目项 => 条目项.标识 == "A001");
        }
        finally
        {
            await 异步停止全部(托管);
        }
    }
}
