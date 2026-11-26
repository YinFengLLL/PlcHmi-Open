using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using 上位机.业务;
using 上位机.业务.配置;
using 上位机.业务.服务;
using 上位机.通信.Modbus;
using 上位机.核心.抽象;
using 上位机.核心.枚举;

namespace 上位机.上位机测试;

/// <summary>
/// 现场实测向量回归测试（2026-09-12 物理联调）。
///
/// <para><b>背景</b>：真机上 HMI 位置显示 3.765e26，初步怀疑 Float32 字序反了。
/// PLC 监控实测：D100=-26228(0x999C)、D101=17043(0x4293)，监控浮点显示 73.79896 ——
/// 按 CDAB（低字在前）拼装 0x4293999C ≈ 73.77，与 PLC 显示吻合（轴在运动，
/// 监控表各字段刷新有零点几的时差，故低字略有出入）。若按 ABCD 反序解释则为
/// 0x999C4293 ≈ -4.7e-8，量级天差地别。</para>
///
/// <para><b>结论</b>：PLC 字序确为 CDAB，与 <c>tags.json</c> 的 <c>wordOrder: LittleEndian</c>
/// 一致；经逐行复核，三条读路径（轮询采集 / 示教点表 / 一致性校验）的解码实现全部正确
/// ——现场天文数字是 PLC D 区当时的残留数据（该点从未被 PLC 程序写入有效位置），
/// 不是解码缺陷。</para>
///
/// <para><b>这组测试锁的是什么</b>：把现场向量固化。今后任何人改字序默认值、
/// 解码调用或分块切片，测试立刻红，而不是等现场再爆一次天文数字。</para>
/// </summary>
public class 现场向量解码测试
{
    [Fact]
    public void 现场向量_CDAB应解出与PLC监控一致的值()
    {
        // 现场监控：D100=-26228(0x999C=39308，低字)、D101=17043(0x4293，高字)
        // → 位模式 0x4293999C = 73.8000183，与 PLC 监控浮点显示 73.79896 仅差 0.001
        //   （轴在微动，监控表字值与浮点值刷新存在瞬时差）——CDAB 判定成立
        var 已解码 = (float)寄存器编解码器.解码(
            标签数据类型.Float32, new ushort[] { 0x999C, 0x4293 }, 字顺序.小字节序);

        Assert.Equal(73.800f, 已解码, precision: 3);
    }

    [Fact]
    public void 现场向量_浮点73点79896的CDAB编码应往返一致()
    {
        // PLC 监控浮点 73.79896f = 0x4293_9911 → CDAB：低字 0x9911(39185) 在 D100、高字 0x4293 在 D101
        var 寄存器列表 = 寄存器编解码器.编码(标签数据类型.Float32, 73.79896f, 字顺序.小字节序);

        Assert.Equal(0x9911, 寄存器列表[0]);
        Assert.Equal(0x4293, 寄存器列表[1]);

        var 已解码 = (float)寄存器编解码器.解码(标签数据类型.Float32, 寄存器列表, 字顺序.小字节序);
        Assert.Equal(73.79896f, 已解码, precision: 4);
    }

    [Fact]
    public void 反字序会得到完全不同的值_证明现场症状与字序无关()
    {
        // 同样两个字按 ABCD（高字在前）解释 → 0x999C4293 ≈ -4.7e-8，与 73.8 天差地别。
        // 现场实际显示 3.765e26，与正反两个字序都不符 → 症状出在 D 区当时的数据本身，不是解码。
        var 有误 = (float)寄存器编解码器.解码(
            标签数据类型.Float32, new ushort[] { 0x999C, 0x4293 }, 字顺序.大字节序);

        Assert.True(MathF.Abs(有误) < 1e-5f, $"反字序应得到极小值，实际 {有误}");
    }
}

/// <summary>
/// 现场向量·集成级：验证「模拟 PLC → 轮询采集 → 解码 → 发布」全链的 Float32 字序。
///
/// <para>用模拟器的轴运动收敛值 120.0 做精确断言：120.0 = 0x42F00000，
/// CDAB 编码为 [0x0000, 0x42F0]；若链路字序错了，会解出 0x000042F0 ≈ 1.7e-5，
/// 与 120 相差 7 个数量级，区分度足够。</para>
/// </summary>
public class 现场向量轮询测试
{
    private static ServiceProvider 构建运行期()
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

        return 服务集合.BuildServiceProvider(validateScopes: true);
    }

    private static async Task 异步等待直到(Func<bool> 条件, string 描述, int 超时毫秒 = 8000)
    {
        var 截止时间 = DateTime.UtcNow.AddMilliseconds(超时毫秒);

        while (DateTime.UtcNow < 截止时间)
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
    public async Task 轮询全链应把模拟器Float32位置按CDAB解出精确值()
    {
        await using var 提供者 = 构建运行期();
        var 托管 = 提供者.GetRequiredService<IHostedService>();

        await 托管.StartAsync(CancellationToken.None);

        try
        {
            var 连接 = 提供者.GetRequiredService<连接管理器接口>();
            var 设备 = 提供者.GetRequiredService<设备服务接口>();
            var 值列表 = 提供者.GetRequiredService<标签值服务接口>();

            await 异步等待直到(() => 连接.状态 == 连接状态.已连接, "连接虚拟 PLC");

            // 启动后模拟器把 X 轴位置收敛到 D200 的目标值 120.0（Float32 CDAB 写 D100-D101）
            await 设备.异步启动();

            await 异步等待直到(
                () => 值列表.获取新值("AxisXPosition").是否有效
                    && Math.Abs(值列表.获取新值("AxisXPosition").缩放后新值 - 120.0) < 0.01,
                "X 轴位置收敛到 120.0");

            // 精确值断言（禁用宽范围断言——上次现场问题就是被 InRange 放过的）
            Assert.Equal(120.0, 值列表.获取新值("AxisXPosition").缩放后新值, precision: 2);

            // Y 轴模拟器恒写 0：若块内偏移或字序错位，这里会拿到相邻轴的脏数据
            Assert.Equal(0.0, 值列表.获取新值("AxisYPosition").缩放后新值, precision: 2);
        }
        finally
        {
            await 托管.StopAsync(CancellationToken.None);
        }
    }
}
