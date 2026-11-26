using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using 上位机.业务;
using 上位机.业务.配置;
using 上位机.业务.服务;
using 上位机.通信.Modbus;
using 上位机.核心.抽象;
using 上位机.核心.模型;
using 上位机.上位机测试.替身;

namespace 上位机.上位机测试;

/// <summary>
/// 扫码枪功能的回归（2026-11-12）。
///
/// <para><b>守的是什么：</b>① 扫码枪按 CR / CRLF / LF 三种结尾都能切出完整帧（现场没人会去翻说明书）；
/// ② 手动型收到条码即写"扫码成功信号寄存器"、并把条码记为工单号；
/// ③ 工单号只存内存（不写进历史文件）；④ 两个寄存器地址由服务层校验，配错当场报错。</para>
/// </summary>
[Collection("config-files")]
public class 扫码枪测试
{
    private static ServiceProvider 构建运行期()
    {
        var 设置 = 应用设置加载器.加载(out var 设置错误);
        var 配方配置 = 配方配置加载器.加载(out var 配方错误);

        Assert.Null(设置错误);
        Assert.Null(配方错误);

        设置.PLC连接.使用模拟器 = true;
        设置.轮询.快间隔毫秒 = 50;
        设置.轮询.慢间隔毫秒 = 100;
        设置.轮询.事件间隔毫秒 = 100;

        var 服务集合 = new ServiceCollection();
        服务集合.AddLogging();
        服务集合.添加上位机服务(设置.PLC连接, 设置.轮询, 配方配置);
        服务集合.AddSingleton<权限守卫接口, 允许全部权限守卫>();

        return 服务集合.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public void 三种行结束符都应切出条码帧()
    {
        var 缓冲 = new StringBuilder();

        Assert.Equal(["AB12"], 扫码帧解析器.切分("AB12\r\n", 缓冲));
        Assert.Equal(["K7-9"], 扫码帧解析器.切分("K7-9\n", 缓冲));
        Assert.Equal(["X001"], 扫码帧解析器.切分("X001\r", 缓冲));

        // 半帧：先到的部分不吐出，等结尾符
        Assert.Empty(扫码帧解析器.切分("PART", 缓冲));
        Assert.Equal(["PART-2"], 扫码帧解析器.切分("-2\r\n", 缓冲));

        // 一块里两条
        Assert.Equal(["A1", "B2"], 扫码帧解析器.切分("A1\r\nB2\r\n", 缓冲));
    }

    [Fact]
    public async Task 手动型收到条码应写成功信号并记住工单号()
    {
        await using var 提供者 = 构建运行期();
        var 托管 = 提供者.GetRequiredService<IHostedService>();
        await 托管.StartAsync(CancellationToken.None);

        try
        {
            var 客户端 = 提供者.GetRequiredService<Modbus客户端接口>();
            var 解析器 = 提供者.GetRequiredService<地址解析器接口>();

            // 未启用：只测处理链，不去打开串口
            var 配置 = new 扫码枪配置
            {
                启用 = false,
                工作模式 = 扫码工作模式.手动型,
                扫码成功寄存器 = "D4050",
                扫码触发寄存器 = "D4010",
            };

            using var 服务实例 = new 扫码枪服务(
                配置, 解析器, 客户端, new 允许全部权限守卫(),
                NullLogger<扫码枪服务>.Instance);

            var 事件触发 = false;
            服务实例.工单号已变更 += (_, _) => 事件触发 = true;

            服务实例.注入扫码结果("  WO-2026-11-12-001  ");

            Assert.Equal("WO-2026-11-12-001", 服务实例.工单号);
            Assert.True(事件触发);

            // 写成功信号是后台任务：等它落进 PLC
            var 截止 = Environment.TickCount64 + 3000;

            while (Environment.TickCount64 < 截止)
            {
                var 值 = await 客户端.异步读取保持寄存器(4050, 1);

                if (值.Length > 0 && 值[0] == 1)
                {
                    return;
                }

                await Task.Delay(50);
            }

            Assert.Fail("超时：扫码成功后应把 D4050 写成 1");
        }
        finally
        {
            await 托管.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task 触发型超时未扫到应写失败信号且晚到的条码被忽略()
    {
        await using var 提供者 = 构建运行期();
        var 托管 = 提供者.GetRequiredService<IHostedService>();
        await 托管.StartAsync(CancellationToken.None);

        try
        {
            var 客户端 = 提供者.GetRequiredService<Modbus客户端接口>();
            var 解析器 = 提供者.GetRequiredService<地址解析器接口>();

            using var 服务实例 = 构建触发型服务(解析器, 客户端, 超时毫秒: 300);
            await 服务实例.异步重启(CancellationToken.None);

            // PLC 请求扫一件：过了 300 ms 还没码，就该判失败
            await 客户端.异步写入单个寄存器(4010, 1);

            Assert.True(
                await 等待异步(async () => (await 读取值(客户端, 4050)) == 2),
                "超时未扫到时应把 D4050 写成 2");

            Assert.Equal(0, await 读取值(客户端, 4010));

            // 晚到（窗口已关）的条码不能翻案：PLC 那边已经按失败处理了
            服务实例.注入扫码结果("LATE-CODE");

            Assert.Equal(string.Empty, 服务实例.工单号);
            Assert.Equal(2, await 读取值(客户端, 4050));
        }
        finally
        {
            await 托管.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task 触发型等码窗口内扫到码应写成功信号()
    {
        await using var 提供者 = 构建运行期();
        var 托管 = 提供者.GetRequiredService<IHostedService>();
        await 托管.StartAsync(CancellationToken.None);

        try
        {
            var 客户端 = 提供者.GetRequiredService<Modbus客户端接口>();
            var 解析器 = 提供者.GetRequiredService<地址解析器接口>();

            using var 服务实例 = 构建触发型服务(解析器, 客户端, 超时毫秒: 5000);
            await 服务实例.异步重启(CancellationToken.None);

            await 客户端.异步写入单个寄存器(4010, 1);

            // 触发被应答（写 0）之后窗口才开：等它开好再"扫到"
            Assert.True(
                await 等待异步(async () => (await 读取值(客户端, 4010)) == 0),
                "触发寄存器应被上位机写 0 应答");

            await Task.Delay(100);

            服务实例.注入扫码结果("  WO-TRIGGER-001  ");

            Assert.Equal("WO-TRIGGER-001", 服务实例.工单号);
            Assert.True(
                await 等待异步(async () => (await 读取值(客户端, 4050)) == 1),
                "窗口内扫到码应把 D4050 写成 1");
        }
        finally
        {
            await 托管.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// 触发型服务实例。
    /// <para>故意用不存在的串口：<c>异步重启</c> 会真的去开一次通道，打不开就只留日志，
    /// 触发监听照常起来（现场也是这个行为：扫码枪掉了不该让 PLC 握手跟着停）。</para>
    /// </summary>
    private static 扫码枪服务 构建触发型服务(地址解析器接口 解析器, Modbus客户端接口 客户端, int 超时毫秒)
    {
        var 配置 = new 扫码枪配置
        {
            启用 = true,
            通讯模式 = 扫码通讯模式.串口,
            串口号 = "COM98",
            工作模式 = 扫码工作模式.触发型,
            扫码成功寄存器 = "D4050",
            扫码触发寄存器 = "D4010",
            轮询间隔毫秒 = 100,
            超时毫秒 = 超时毫秒,
            触发指令 = string.Empty,
            释放指令 = string.Empty,
        };

        return new 扫码枪服务(配置, 解析器, 客户端, new 允许全部权限守卫(),
            NullLogger<扫码枪服务>.Instance);
    }

    private static async Task<ushort> 读取值(Modbus客户端接口 客户端, int 地址)
    {
        var 值 = await 客户端.异步读取保持寄存器(地址, 1);
        return 值.Length > 0 ? 值[0] : (ushort)0;
    }

    private static async Task<bool> 等待异步(Func<Task<bool>> 条件, int 超时毫秒 = 5000)
    {
        var 截止 = Environment.TickCount64 + 超时毫秒;

        while (Environment.TickCount64 < 截止)
        {
            if (await 条件())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return false;
    }

    [Fact]
    public void 工单号不应写进历史记录文件()
    {
        var 条目 = new 历史记录条目
        {
            时刻 = new DateTime(2026, 11, 12, 9, 0, 0),
            数值集合 = [1, 2],
            工单号 = "WO-SECRET",
        };

        var JSON = JsonSerializer.Serialize(条目, JSON配置.写入);

        Assert.DoesNotContain("工单号", JSON, StringComparison.Ordinal);
        Assert.DoesNotContain("WO-SECRET", JSON, StringComparison.Ordinal);

        // 内存里仍在（界面用它显示列）
        Assert.Equal("WO-SECRET", 条目.克隆().工单号);
    }

    [Fact]
    public async Task 保存设置应拒绝无效寄存器且不落盘()
    {
        var 设置路径 = 配置路径.应用设置;
        var 原始 = await File.ReadAllTextAsync(设置路径);

        try
        {
            await using var 提供者 = 构建运行期();
            var 服务实例 = 提供者.GetRequiredService<扫码枪服务>();

            var 候选 = 服务实例.配置.克隆();
            候选.扫码成功寄存器 = "M100";   // 位元件，不能当信号寄存器

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => 服务实例.异步保存设置(候选));

            // 文件没被改
            Assert.Equal(原始, await File.ReadAllTextAsync(设置路径));

            // 两个寄存器填同一个地址也要拒绝
            候选.扫码成功寄存器 = "D4050";
            候选.扫码触发寄存器 = "D4050";

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => 服务实例.异步保存设置(候选));
        }
        finally
        {
            await File.WriteAllTextAsync(设置路径, 原始);
        }
    }

    [Fact]
    public async Task 保存合法设置应落盘并可重新加载()
    {
        var 设置路径 = 配置路径.应用设置;
        var 原始 = await File.ReadAllTextAsync(设置路径);

        try
        {
            await using var 提供者 = 构建运行期();
            var 服务实例 = 提供者.GetRequiredService<扫码枪服务>();

            var 候选 = 服务实例.配置.克隆();
            候选.启用 = true;

            // 故意用不存在的串口：保存后会真的去开一次通道，串口打不开立刻返回，
            // 不会像 TCP 那样等 3 秒，也不占用任何网络端口（避免和其它用例抢资源）
            候选.通讯模式 = 扫码通讯模式.串口;
            候选.串口号 = "COM99";
            候选.波特率 = 19200;
            候选.工作模式 = 扫码工作模式.触发型;
            候选.扫码成功寄存器 = "D4052";
            候选.扫码触发寄存器 = "D4012";
            候选.触发指令 = "TRIGGER";
            候选.释放指令 = "RELEASE";

            await 服务实例.异步保存设置(候选);

            var 已重载 = 应用设置加载器.加载(out var 错误);
            Assert.Null(错误);
            Assert.True(已重载.扫码枪.启用);
            Assert.Equal(扫码通讯模式.串口, 已重载.扫码枪.通讯模式);
            Assert.Equal("COM99", 已重载.扫码枪.串口号);
            Assert.Equal(19200, 已重载.扫码枪.波特率);
            Assert.Equal(扫码工作模式.触发型, 已重载.扫码枪.工作模式);
            Assert.Equal("D4052", 已重载.扫码枪.扫码成功寄存器);
            Assert.Equal("D4012", 已重载.扫码枪.扫码触发寄存器);
            Assert.Equal("TRIGGER", 已重载.扫码枪.触发指令);

            // 运行配置同步（后续写 PLC 用的就是这份快照）
            Assert.Equal("D4052", 服务实例.配置.扫码成功寄存器);
        }
        finally
        {
            await File.WriteAllTextAsync(设置路径, 原始);
        }
    }
}
