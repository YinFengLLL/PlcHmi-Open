using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using 上位机.业务;
using 上位机.业务.配置;
using 上位机.业务.服务;
using 上位机.通信.Modbus;
using 上位机.通信.仿真;
using 上位机.核心.抽象;
using 上位机.核心.枚举;

namespace 上位机.上位机测试;

/// <summary>
/// 「从 PLC 读取参数」功能测试。
///
/// <para><b>守的是什么</b>：这是参数页与 PLC 之间第二条数据通路（与「下发」相反方向）。
/// 关键约束：<b>任一项超出允许范围必须整体拒绝</b>——绝不允许"读回一半"，
/// 否则本地与 PLC 各错一半，之后无论往哪个方向同步都是错的。</para>
///
/// <para>读取会写测试输出目录里的 <c>recipes.json</c>（留底），归入 config-files
/// 集合禁用并行，并在用例内备份 / 还原。</para>
/// </summary>
[Collection("config-files")]
public class 从PLC读取参数测试
{
    private static async Task<ServiceProvider> 异步启动运行期()
    {
        var 设置 = 应用设置加载器.加载(out var 错误);
        Assert.Null(错误);

        // 仓库配置处于真实 PLC 模式（现场部署）；测试强制模拟器，保持密封
        设置.PLC连接.使用模拟器 = true;
        设置.轮询.快间隔毫秒 = 50;
        设置.轮询.慢间隔毫秒 = 100;
        设置.轮询.事件间隔毫秒 = 100;

        var 服务集合 = new ServiceCollection();
        服务集合.AddLogging();
        服务集合.添加上位机服务(设置.PLC连接, 设置.轮询, 配方配置加载器.加载(out _));

        // 本组用例不验证权限边界（那由 PermissionGuardTests 覆盖），以受信任身份运行
        服务集合.AddSingleton<权限守卫接口, 允许全部权限守卫>();

        var 提供者 = 服务集合.BuildServiceProvider(validateScopes: true);
        await 提供者.GetRequiredService<IHostedService>().StartAsync(CancellationToken.None);
        return 提供者;
    }

    /// <summary>把一个 Float32 值按 CDAB 写进模拟器的指定参数地址。</summary>
    private static void 写入浮点(虚拟PLC设备 设备, int 地址值, float 值)
    {
        var 寄存器列表 = 寄存器编解码器.编码(标签数据类型.Float32, 值, 字顺序.小字节序);
        设备.内存.运行独占(() =>
        {
            设备.内存.不安全设置保持寄存器(地址值, 寄存器列表[0]);
            设备.内存.不安全设置保持寄存器(地址值 + 1, 寄存器列表[1]);
        });
    }

    /// <summary>
    /// 给六轴定位速度各写一个范围内的值。
    /// <para><b>为什么必须补齐</b>：模拟器未初始化的寄存器是 0，而速度下限是 1 ——
    /// 只要有一项越界，「整体拒绝」就会让本用例变成"什么都没读到"。
    /// 六轴（含 ZZ / 点胶 X / 点胶 Z）都要有值。</para>
    /// </summary>
    private static void 写入全部轴合法值(虚拟PLC设备 设备)
    {
        // 2026-10-21 现场配方：六轴速度集中在 D1400 起
        写入浮点(设备, 1400, 300f);   // 锁附 X
        写入浮点(设备, 1402, 300f);   // 锁附 Y
        写入浮点(设备, 1404, 60f);    // 锁附 Z
        写入浮点(设备, 1406, 60f);    // 锁附 ZZ
        写入浮点(设备, 1410, 200f);   // 点胶 X
        写入浮点(设备, 1408, 100f);   // 点胶 Z
    }

    [Fact]
    public async Task 未连接时读取应被拒绝()
    {
        var 设置 = 应用设置加载器.加载(out _);
        设置.PLC连接.使用模拟器 = true;

        var 服务集合 = new ServiceCollection();
        服务集合.AddLogging();
        服务集合.添加上位机服务(设置.PLC连接, 设置.轮询, 配方配置加载器.加载(out _));

        // 本组用例不验证权限边界（那由 PermissionGuardTests 覆盖），以受信任身份运行
        服务集合.AddSingleton<权限守卫接口, 允许全部权限守卫>();

        await using var 提供者 = 服务集合.BuildServiceProvider(validateScopes: true);
        var 配方项 = 提供者.GetRequiredService<配方服务接口>();

        // 未 StartAsync → 模拟客户端未连接
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => 配方项.异步读取PLC参数());
    }

    [Fact]
    public async Task 读取应覆盖本地并留底_并返回差异清单()
    {
        var 原始 = await File.ReadAllTextAsync(配置路径.参数集合);

        try
        {
            await using var 提供者 = await 异步启动运行期();
            var 配方项 = 提供者.GetRequiredService<配方服务接口>();
            var 设备 = 提供者.GetRequiredService<虚拟PLC设备>();

            写入全部轴合法值(设备);

            // 模拟现场：有人直接在 PLC 侧把 X 轴定位速度改成了 300（合法值，范围内）
            // 锁附 X 定位速度 = D1400
            写入浮点(设备, 1400, 300f);

            var 变更 = await 配方项.异步读取PLC参数();

            // 差异清单应指出该项变化（本地原值不假设——其它测试可能改过 recipes.json）
            Assert.Contains(变更, 客户端 => 客户端.Contains("X 轴定位速度") && 客户端.Contains("300"));

            // 内存参数已被覆盖
            Assert.Equal(300.0, 配方项.当前参数["X.LocateSpeed"], precision: 3);
        }
        finally
        {
            await File.WriteAllTextAsync(配置路径.参数集合, 原始);
        }
    }

    [Fact]
    public async Task PLC值超范围应整体拒绝且本地保持不变()
    {
        var 原始 = await File.ReadAllTextAsync(配置路径.参数集合);

        try
        {
            await using var 提供者 = await 异步启动运行期();
            var 配方项 = 提供者.GetRequiredService<配方服务接口>();
            var 设备 = 提供者.GetRequiredService<虚拟PLC设备>();

            写入全部轴合法值(设备);

            // 先成功读回一个合法值，建立"本地已被覆盖"的基线（X 轴 D1400）
            写入浮点(设备, 1400, 300f);
            var 变更 = await 配方项.异步读取PLC参数();
            Assert.Equal(300.0, 配方项.当前参数["X.LocateSpeed"], precision: 3);

            // 再把 Z 轴定位速度改成 5000（超出允许范围 max 1000）；锁附 Z = D1404
            写入浮点(设备, 1404, 5000f);

            var 异常 = await Assert.ThrowsAsync<InvalidOperationException>(
                () => 配方项.异步读取PLC参数());

            // 错误清单指出越界项；不因一项越界而吞掉其它项的信息
            Assert.Contains("超出允许范围", 异常.Message);
            Assert.Contains("Z 轴定位速度", 异常.Message);

            // 关键：整体拒绝——合法项（X=300）也不允许生效，本地保持上一次读取的结果
            Assert.Equal(300.0, 配方项.当前参数["X.LocateSpeed"], precision: 3);
        }
        finally
        {
            await File.WriteAllTextAsync(配置路径.参数集合, 原始);
        }
    }

    // ── 启动同步（开机从 PLC 刷新全局参数） ──────────────────────────────
    //
    // 与上面「从 PLC 读取参数」的区别：那是操作员按按钮的显式动作，越界要整体拒绝；
    // 启动同步是"机种区之外的 D 区以 PLC 为准，开机读一次刷新"，单项容错、逐项覆盖。

    [Fact]
    public async Task 启动同步应按PLC值覆盖本地缓存()
    {
        var 原始参数 = await File.ReadAllTextAsync(配置路径.参数集合);
        var 原始机种 = await File.ReadAllTextAsync(配置路径.配方集合);

        try
        {
            await using var 提供者 = await 异步启动运行期();
            var 配方项 = 提供者.GetRequiredService<配方服务接口>();
            var 设备 = 提供者.GetRequiredService<虚拟PLC设备>();

            写入全部轴合法值(设备);
            写入浮点(设备, 1400, 250f);

            // 本地先改成一个明显不同的值（模拟上次运行遗留的 parameters.json）
            配方项.设置参数("X.LocateSpeed", 111);
            Assert.Equal(111.0, 配方项.当前参数["X.LocateSpeed"], precision: 3);

            // 连接建立后补做的那次同步：PLC 是权威 → 覆盖为 250
            await 配方项.异步初始化();

            Assert.Equal(250.0, 配方项.当前参数["X.LocateSpeed"], precision: 3);
            Assert.Equal(6, 配方项.当前参数.Count);
        }
        finally
        {
            await File.WriteAllTextAsync(配置路径.参数集合, 原始参数);
            await File.WriteAllTextAsync(配置路径.配方集合, 原始机种);
        }
    }

    [Fact]
    public async Task 启动同步应跳过PLC里的越界值并保留本地值()
    {
        var 原始参数 = await File.ReadAllTextAsync(配置路径.参数集合);
        var 原始机种 = await File.ReadAllTextAsync(配置路径.配方集合);

        try
        {
            await using var 提供者 = await 异步启动运行期();
            var 配方项 = 提供者.GetRequiredService<配方服务接口>();
            var 设备 = 提供者.GetRequiredService<虚拟PLC设备>();

            写入全部轴合法值(设备);

            // PLC 侧 X 轴被写成 5000（超出 1000 上限）——半个字错位或残留数据都可能这样
            写入浮点(设备, 1400, 5000f);

            配方项.设置参数("X.LocateSpeed", 111);

            await 配方项.异步初始化();

            // 单项容错：越界项保留本地值（不让天文数字进内存），其余项照常刷新
            Assert.Equal(111.0, 配方项.当前参数["X.LocateSpeed"], precision: 3);
            Assert.Equal(300.0, 配方项.当前参数["Y.LocateSpeed"], precision: 3);
        }
        finally
        {
            await File.WriteAllTextAsync(配置路径.参数集合, 原始参数);
            await File.WriteAllTextAsync(配置路径.配方集合, 原始机种);
        }
    }
}
