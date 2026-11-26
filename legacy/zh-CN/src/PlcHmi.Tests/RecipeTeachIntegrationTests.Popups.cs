using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using 上位机.业务.配置;
using 上位机.业务.服务;
using 上位机.核心.抽象;

namespace 上位机.上位机测试;

/// <summary>
/// 可配置弹窗的端到端用例（<see cref="配方示教集成测试"/> 的 partial part）。
///
/// <para><b>为什么必须有这组用例：</b>弹窗"没弹出来"在上位机侧没有任何报错——
/// 现场只会看到"PLC 里 M 都闭合了，界面什么都不发生"。
/// 曾经的真实缺陷：监控服务等"连接状态已变更"事件才启动轮询，
/// 而服务第一次被创建时 PLC 往往已经连上，事件再也不会来，轮询永不启动。
/// 这组用例直接对着<b>虚拟 PLC 的线圈</b>跑完整链路（写线圈 → 轮询读到 → 上报触发），
/// 把这个次序陷阱钉死。</para>
/// </summary>
public partial class 配方示教集成测试
{
    [Fact]
    public async Task 触发线圈闭合应上报触发_断开应收起()
    {
        // 弹窗定义来自仓库真实配置 config/popups.json（演示弹窗：触发 M1100）
        var 定义 = 读取演示弹窗();

        await using var 提供者 = 构建运行期();
        var 托管 = 提供者.GetRequiredService<IHostedService>();
        await 托管.StartAsync(CancellationToken.None);

        try
        {
            var 客户端 = 提供者.GetRequiredService<Modbus客户端接口>();
            var 解析器 = 提供者.GetRequiredService<地址解析器接口>();
            var 监控 = 提供者.GetRequiredService<弹窗监控服务>();

            Assert.Contains(监控.弹窗集合, 条目 => 条目.键 == 定义.键);
            Assert.Null(监控.当前触发键);

            // 触发线圈地址必须能解析成 M 区线圈（写错地址的弹窗会被装载期警告跳过）
            Assert.True(解析器.尝试解析(定义.触发地址, out var 地址), "触发地址解析失败");
            Assert.NotNull(地址);

            // 1. 闭合触发线圈 → 应上报触发（真实关系：不做锁存）
            await 客户端.异步写入单个线圈(地址!.Modbus地址, true);

            await 异步等待直到(
                () => 监控.当前触发键 == 定义.键,
                $"{定义.触发地址} 闭合后上报弹窗 {定义.名称}");

            // 2. 断开触发线圈 → 应立即收起
            await 客户端.异步写入单个线圈(地址.Modbus地址, false);

            await 异步等待直到(
                () => 监控.当前触发键 is null,
                $"{定义.触发地址} 断开后收起弹窗");
        }
        finally
        {
            await 托管.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task 弹窗按钮应按住置位松开复位()
    {
        var 定义 = 读取演示弹窗();

        Assert.False(string.IsNullOrWhiteSpace(定义.按钮一地址), "演示弹窗必须配按钮一地址");

        await using var 提供者 = 构建运行期();
        var 托管 = 提供者.GetRequiredService<IHostedService>();
        await 托管.StartAsync(CancellationToken.None);

        try
        {
            var 客户端 = 提供者.GetRequiredService<Modbus客户端接口>();
            var 解析器 = 提供者.GetRequiredService<地址解析器接口>();
            var 监控 = 提供者.GetRequiredService<弹窗监控服务>();

            await 异步等待直到(() => 客户端.是否已连接, "虚拟 PLC 连接就绪");

            Assert.True(解析器.尝试解析(定义.按钮一地址, out var 按钮地址), "按钮地址解析失败");
            Assert.NotNull(按钮地址);

            // 按住 → 置位
            await 监控.异步按下(定义, 0);

            var 按下后 = await 客户端.异步读取线圈(按钮地址!.Modbus地址, 1);
            Assert.True(按下后[0], "按住按钮应置位对应线圈");

            // 松开 → 复位（复归型：不保持）
            await 监控.异步松开(定义, 0);

            var 松开后 = await 客户端.异步读取线圈(按钮地址.Modbus地址, 1);
            Assert.False(松开后[0], "松开按钮应复位对应线圈");
        }
        finally
        {
            await 托管.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>取仓库演示配置里的演示弹窗（config/popups.json 的第一个条目）。</summary>
    private static 弹窗定义 读取演示弹窗()
    {
        var 路径 = Path.Combine(配置路径.配置目录, "popups.json");

        Assert.True(File.Exists(路径), $"缺少演示弹窗配置：{路径}");

        var 文件 = System.Text.Json.JsonSerializer.Deserialize<弹窗文件>(
            File.ReadAllText(路径),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(文件);
        Assert.NotEmpty(文件!.弹窗集合);

        return 文件.弹窗集合[0];
    }

    // ── 2026-10-17：按钮三种工作模式 ────────────────────────────────────

    /// <summary>置位 ON：点一次即置位并保持——松开不能再写回去。</summary>
    [Fact]
    public async Task 弹窗按钮_置位ON模式应点一次即保持()
    {
        await using var 提供者 = 构建运行期();
        var 托管 = 提供者.GetRequiredService<IHostedService>();
        await 托管.StartAsync(CancellationToken.None);

        try
        {
            var 客户端 = 提供者.GetRequiredService<Modbus客户端接口>();
            var 监控 = 提供者.GetRequiredService<弹窗监控服务>();

            var 定义 = new 弹窗定义
            {
                键 = "test-on",
                名称 = "测试置位",
                触发地址 = "M1100",
                按钮一名称 = "启动输送",
                按钮一地址 = "M5000",
                按钮一模式 = 弹窗按钮模式.置位ON,
            };

            await 监控.异步按下(定义, 0);

            Assert.True((await 客户端.异步读取线圈(5000, 1))[0], "点一次应置位");

            await 监控.异步松开(定义, 0);

            Assert.True((await 客户端.异步读取线圈(5000, 1))[0],
                "置位 ON 模式：松开不得把刚置上的位抹掉（否则现场表现为'按钮按了没反应'）");
        }
        finally
        {
            await 托管.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>复位 OFF：点一次即复位并保持——松开不恢复。</summary>
    [Fact]
    public async Task 弹窗按钮_复位OFF模式应点一次即复位并保持()
    {
        await using var 提供者 = 构建运行期();
        var 托管 = 提供者.GetRequiredService<IHostedService>();
        await 托管.StartAsync(CancellationToken.None);

        try
        {
            var 客户端 = 提供者.GetRequiredService<Modbus客户端接口>();
            var 监控 = 提供者.GetRequiredService<弹窗监控服务>();

            // 先让线圈处于置位状态（模拟 PLC 侧 / 之前操作留下的开启状态）
            await 客户端.异步写入单个线圈(5001, true);

            var 定义 = new 弹窗定义
            {
                键 = "test-off",
                名称 = "测试复位",
                触发地址 = "M1100",
                按钮一名称 = "停止输送",
                按钮一地址 = "M5001",
                按钮一模式 = 弹窗按钮模式.复位OFF,
            };

            await 监控.异步按下(定义, 0);

            Assert.False((await 客户端.异步读取线圈(5001, 1))[0], "点一次应复位");

            await 监控.异步松开(定义, 0);

            Assert.False((await 客户端.异步读取线圈(5001, 1))[0], "复位 OFF 模式：松开不得恢复置位");
        }
        finally
        {
            await 托管.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>复归型（默认）与另两种模式的写入时序必须不同——这条把三者的分工钉死。</summary>
    [Fact]
    public void 弹窗按钮模式_三种模式的语义应互不相同()
    {
        Assert.Equal(3, 弹窗定义.模式选项.Count);
        Assert.Contains("复归型", 弹窗定义.模式选项);
        Assert.Contains("置位 ON", 弹窗定义.模式选项);
        Assert.Contains("复位 OFF", 弹窗定义.模式选项);

        // 默认必须是复归型：老配置（JSON 里没有模式字段）升级后行为不变
        Assert.Equal(弹窗按钮模式.复归型, new 弹窗定义().按钮一模式);
        Assert.Equal(弹窗按钮模式.复归型, new 弹窗定义().按钮二模式);

        // 每种模式都要有可读的说明（弹窗按钮的 ToolTip 绑的是它）
        foreach (var 模式 in (弹窗按钮模式[])[弹窗按钮模式.复归型, 弹窗按钮模式.置位ON, 弹窗按钮模式.复位OFF])
        {
            Assert.False(string.IsNullOrWhiteSpace(弹窗定义.模式说明(模式)));
        }
    }
}
