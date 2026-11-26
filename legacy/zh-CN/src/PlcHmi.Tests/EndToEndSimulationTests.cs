using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using 上位机.业务;
using 上位机.业务.配置;
using 上位机.业务.服务;
using 上位机.通信.仿真;
using 上位机.核心.抽象;
using 上位机.核心.枚举;

namespace 上位机.上位机测试;

/// <summary>
/// 端到端集成测试：用虚拟 PLC 跑通 <b>配置加载 → DI 装配 → 连接 → 轮询 → 业务服务</b> 的完整链路。
///
/// <para>这一层测试的价值在于它读的是真实的 <c>config/*.json</c>——点位表改了、地址映射改错了、
/// 模拟器区写错了，这里都会立刻红。</para>
/// </summary>
public class 端到端仿真测试
{
    /// <summary>按真实配置文件装配一套模拟环境。</summary>
    private static ServiceProvider 构建运行期()
    {
        var 设置 = 应用设置加载器.加载(out var 错误);
        Assert.Null(错误);

        // 仓库配置已切到真实 PLC 模式（现场部署）；与 AlarmScanIntegrationTests 同款——
        // 强制模拟器，测试与仓库配置解耦，永远不可能碰到真实 PLC。
        设置.PLC连接.使用模拟器 = true;

        // 测试里把周期调快，缩短等待
        设置.轮询.快间隔毫秒 = 50;
        设置.轮询.慢间隔毫秒 = 100;
        设置.轮询.事件间隔毫秒 = 100;

        var 服务集合 = new ServiceCollection();
        服务集合.AddLogging();
        服务集合.添加上位机服务(设置.PLC连接, 设置.轮询, 配方配置加载器.加载(out _));

        // 本组用例不验证权限边界（那由 PermissionGuardTests 覆盖），以受信任身份运行
        服务集合.AddSingleton<权限守卫接口, 允许全部权限守卫>();

        return 服务集合.BuildServiceProvider(validateScopes: true);
    }

    /// <summary>轮询等待条件成立，超时则断言失败。</summary>
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

    [Fact]
    public async Task 端到端_应能连接虚拟PLC并读到点位()
    {
        await using var 提供者 = 构建运行期();
        var 托管 = 提供者.GetRequiredService<IHostedService>();

        await 托管.StartAsync(CancellationToken.None);

        try
        {
            var 连接 = 提供者.GetRequiredService<连接管理器接口>();
            var 值列表 = 提供者.GetRequiredService<标签值服务接口>();

            await 异步等待直到(() => 连接.状态 == 连接状态.已连接, "连接虚拟 PLC");

            // tags.json 里的点位应该被采到
            await 异步等待直到(() => 值列表.获取新值("RunStatus").是否有效, "采到 RunStatus");
            Assert.True(值列表.获取新值("RunStatus").是否有效);

            // 上电后状态字为 0（空闲中），映射后应为 空闲——这条断言把“状态字编号约定”钉住
            var 设备 = 提供者.GetRequiredService<设备服务接口>();
            await 异步等待直到(() => 设备.状态 == 设备状态.空闲, "设备进入空闲中状态");
            Assert.Equal(设备状态.空闲, 设备.状态);
        }
        finally
        {
            await 托管.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task 端到端_下发启动命令后设备应进入运行中()
    {
        await using var 提供者 = 构建运行期();
        var 托管 = 提供者.GetRequiredService<IHostedService>();

        await 托管.StartAsync(CancellationToken.None);

        try
        {
            var 连接 = 提供者.GetRequiredService<连接管理器接口>();
            var 设备 = 提供者.GetRequiredService<设备服务接口>();

            await 异步等待直到(() => 连接.状态 == 连接状态.已连接, "连接虚拟 PLC");

            await 设备.异步启动();

            // 命令脉冲 → 虚拟 PLC 置运行 → 状态字 1 → 映射为 Running
            await 异步等待直到(() => 设备.状态 == 设备状态.运行中, "设备进入运行中");
            Assert.Equal(设备状态.运行中, 设备.状态);

            // 运行后节拍与轴位置应开始更新
            await 异步等待直到(() => 设备.周期时间秒 > 0, "节拍开始更新");
            Assert.True(设备.周期时间秒 > 0, "节拍应大于 0");
        }
        finally
        {
            await 托管.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task 端到端_急停后设备状态应为报警()
    {
        await using var 提供者 = 构建运行期();
        var 托管 = 提供者.GetRequiredService<IHostedService>();

        await 托管.StartAsync(CancellationToken.None);

        try
        {
            var 连接 = 提供者.GetRequiredService<连接管理器接口>();
            var 设备 = 提供者.GetRequiredService<设备服务接口>();

            await 异步等待直到(() => 连接.状态 == 连接状态.已连接, "连接虚拟 PLC");

            // 急停是物理按钮，上位机已不再提供急停命令（软件不能进安全回路）。
            // 这里直接置位 PLC 内部的 M204 急停信号位（电平保持），模拟“现场急停被按下”——
            // 模拟器状态机会把它锁存为报警态并持续输出状态字 4（报警中），
            // 借此验证上位机仍能正确读到并进入报警态（界面显示“报警中”）。
            var 内存 = 提供者.GetRequiredService<虚拟PLC设备>().内存;
            内存.运行独占(() => 内存.不安全设置线圈(204, true));

            await 异步等待直到(() => 设备.状态 == 设备状态.报警中, "设备进入报警中状态");
            Assert.Equal(设备状态.报警中, 设备.状态);
        }
        finally
        {
            await 托管.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task 端到端_IO点位应被采集并应用反相()
    {
        await using var 提供者 = 构建运行期();
        var 托管 = 提供者.GetRequiredService<IHostedService>();

        await 托管.StartAsync(CancellationToken.None);

        try
        {
            var 连接 = 提供者.GetRequiredService<连接管理器接口>();
            var IO点位表 = 提供者.GetRequiredService<IO点位服务接口>();

            await 异步等待直到(() => 连接.状态 == 连接状态.已连接, "连接虚拟 PLC");

            // X10 料仓满料：模拟器写 0%10<7 → 原始位 1，未反相 → 逻辑 ON
            await 异步等待直到(() => IO点位表.获取状态("X10").是否开启, "X10 变为 ON");

            // X7 急停按钮为常闭点（inverted=true）：未急停时原始位 1 → 逻辑 OFF
            await 异步等待直到(
                () => IO点位表.获取状态("X7").时间戳 != DateTime.MinValue,
                "X7 已被采集");

            Assert.False(IO点位表.获取状态("X7").是否开启, "未急停时 X7 逻辑值应为 OFF");
            Assert.True(IO点位表.获取状态("X10").是否开启, "X10 料仓满料应为 ON");
        }
        finally
        {
            await 托管.StopAsync(CancellationToken.None);
        }
    }
}
