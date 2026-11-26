using Microsoft.Extensions.Logging.Abstractions;
using 上位机.业务.服务;
using 上位机.通信.Modbus;
using 上位机.通信.仿真;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;
using 上位机.上位机测试.替身;

namespace 上位机.上位机测试;

/// <summary>
/// 运行模式管理器测试：真实 PLC ↔ 模拟 PLC 的运行期切换。
///
/// <para><b>守的是什么：</b>切换是"停连接 → 换客户端实现 → 起/停模拟从站 → 重连"的四步动作，
/// 任何一步顺序错、漏做或半途失败，现场表现都是"界面显示已切换，但数据还是旧的"或
/// "模拟从站没起来，工具连不上"。</para>
///
/// <para>用替身连接管理器记录启停次数，用真实的两套客户端实现与真实从站，
/// 使断言落在"到底换了谁、从站起没起"这些可观察事实上。</para>
/// </summary>
public class 运行模式切换测试
{
    /// <summary>每个用例一套完整夹具：真实/仿真客户端各一份 + 代理 + 从站 + 替身连接管理器。</summary>
    private sealed class 夹具 : IAsyncDisposable
    {
        public required PLC连接选项 选项 { get; init; }

        public required 替身连接管理器 连接 { get; init; }

        public required 虚拟PLC设备 设备 { get; init; }

        public required 虚拟PLC从站 从站 { get; init; }

        public required 仿真Modbus客户端 仿真 { get; init; }

        public required FluentModbusTCP客户端 真实 { get; init; }

        public required 可切换Modbus客户端 代理 { get; init; }

        public required Modbus运行模式管理器 管理器 { get; init; }

        public async ValueTask DisposeAsync()
        {
            await 管理器.DisposeAsync();
            await 真实.DisposeAsync();
            await 仿真.DisposeAsync();
            await 设备.DisposeAsync();
        }
    }

    /// <summary>未登录身份走生产用的真实守卫 —— 操作员不具备「切换运行模式」权限。</summary>
    private static 权限守卫接口 未登录权限() =>
        new 用户权限守卫(new 用户服务(NullLogger<用户服务>.Instance),
            NullLogger<用户权限守卫>.Instance);

    private static 夹具 构建夹具(
        bool 初始模拟 = false,
        string 监听地址 = "127.0.0.1",
        通讯协议 协议 = 通讯协议.ModbusTCP,
        权限守卫接口? 权限 = null)
    {
        var 选项 = new PLC连接选项
        {
            主机 = "127.0.0.1",
            端口 = 502,
            单元标识 = 1,
            使用模拟器 = 初始模拟,
            通讯协议 = 协议,
            模拟从站端口 = 0,
            模拟从站监听地址 = 监听地址,
            连接超时毫秒 = 1000,
            读取超时毫秒 = 1000,
        };

        var 设备 = new 虚拟PLC设备(NullLogger<虚拟PLC设备>.Instance);
        var 从站 = new 虚拟PLC从站(设备, 选项, NullLogger<虚拟PLC从站>.Instance);
        var 仿真 = new 仿真Modbus客户端(设备, NullLogger<仿真Modbus客户端>.Instance);
        var 真实 = new FluentModbusTCP客户端(选项, NullLogger<FluentModbusTCP客户端>.Instance);
        var 代理 = new 可切换Modbus客户端(初始模拟 ? 仿真 : 真实);
        var 连接 = new 替身连接管理器();
        var 管理器 = new Modbus运行模式管理器(
            代理, 真实, 仿真, 从站, 连接, 选项,
            权限 ?? new 允许全部权限守卫(),
            NullLogger<Modbus运行模式管理器>.Instance);

        return new 夹具
        {
            选项 = 选项,
            连接 = 连接,
            设备 = 设备,
            从站 = 从站,
            仿真 = 仿真,
            真实 = 真实,
            代理 = 代理,
            管理器 = 管理器,
        };
    }

    [Fact]
    public async Task 切到模拟模式_应换用仿真客户端并启动调试从站()
    {
        await using var 夹具实例 = 构建夹具();
        var 事件次数 = 0;
        夹具实例.管理器.模式已变更 += (_, _) => 事件次数++;

        await 夹具实例.管理器.异步切换(运行模式.模拟PLC);

        Assert.Equal(运行模式.模拟PLC, 夹具实例.管理器.当前模式);
        Assert.IsType<仿真Modbus客户端>(夹具实例.代理.当前实现);
        Assert.True(夹具实例.选项.使用模拟器);

        // 离线调试从站必须真的起来（否则"可用工具调试"是一句空话）
        Assert.True(夹具实例.从站.是否运行);
        Assert.True(夹具实例.管理器.模拟从站是否运行);
        Assert.True(夹具实例.从站.端口 > 0, "端口配 0 应由系统分配实际端口");
        Assert.Contains("监听中", 夹具实例.管理器.模拟从站状态文本, StringComparison.Ordinal);

        // 连接被"停一次、起一次"——换实现必须走断连重连，否则旧连接会继续用旧实现
        Assert.Equal(1, 夹具实例.连接.停止次数);
        Assert.Equal(1, 夹具实例.连接.启动次数);
        Assert.Equal(1, 事件次数);
    }

    [Fact]
    public async Task 从模拟切回真实_应换回真实客户端并停用调试从站()
    {
        await using var 夹具实例 = 构建夹具();
        await 夹具实例.管理器.异步切换(运行模式.模拟PLC);
        Assert.True(夹具实例.从站.是否运行);

        var 事件次数 = 0;
        夹具实例.管理器.模式已变更 += (_, _) => 事件次数++;

        await 夹具实例.管理器.异步切换(运行模式.真实PLC);

        Assert.Equal(运行模式.真实PLC, 夹具实例.管理器.当前模式);
        Assert.IsType<FluentModbusTCP客户端>(夹具实例.代理.当前实现);
        Assert.False(夹具实例.选项.使用模拟器);

        // 回到真实 PLC 后调试从站必须关闭：留着一个可写的虚拟 PLC 监听口是安全隐患
        Assert.False(夹具实例.从站.是否运行);
        Assert.Equal(2, 夹具实例.连接.停止次数);
        Assert.Equal(2, 夹具实例.连接.启动次数);
        Assert.Equal(1, 事件次数);
    }

    [Fact]
    public async Task 目标与当前一致时应幂等_不重启连接()
    {
        await using var 夹具实例 = 构建夹具();
        var 事件次数 = 0;
        夹具实例.管理器.模式已变更 += (_, _) => 事件次数++;

        await 夹具实例.管理器.异步切换(运行模式.真实PLC);

        Assert.Equal(0, 夹具实例.连接.停止次数);
        Assert.Equal(0, 夹具实例.连接.启动次数);
        Assert.Equal(0, 事件次数);
    }

    [Fact]
    public async Task 无切换权限时_应拒绝且不留下任何状态变更()
    {
        await using var 夹具实例 = 构建夹具(权限: 未登录权限());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => 夹具实例.管理器.异步切换(运行模式.模拟PLC));

        Assert.Equal(运行模式.真实PLC, 夹具实例.管理器.当前模式);
        Assert.IsType<FluentModbusTCP客户端>(夹具实例.代理.当前实现);
        Assert.False(夹具实例.从站.是否运行);
        Assert.Equal(0, 夹具实例.连接.停止次数);
        Assert.Equal(0, 夹具实例.连接.启动次数);
    }

    [Fact]
    public async Task 从站无法监听时_应中止切换且状态不变()
    {
        // 监听地址不是合法 IP → 从站在"停连接"之前就失败，属预期中的"什么都没发生"
        await using var 夹具实例 = 构建夹具(监听地址: "不是IP");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => 夹具实例.管理器.异步切换(运行模式.模拟PLC));

        Assert.Equal(运行模式.真实PLC, 夹具实例.管理器.当前模式);
        Assert.IsType<FluentModbusTCP客户端>(夹具实例.代理.当前实现);
        Assert.False(夹具实例.从站.是否运行);
        Assert.Equal(0, 夹具实例.连接.停止次数);
        Assert.Equal(0, 夹具实例.连接.启动次数);
    }

    [Fact]
    public async Task OPCUA模式_不支持运行期切换()
    {
        await using var 夹具实例 = 构建夹具(协议: 通讯协议.OPCUA);

        Assert.False(夹具实例.管理器.可在运行期切换);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => 夹具实例.管理器.异步切换(运行模式.模拟PLC));

        Assert.Equal(0, 夹具实例.连接.停止次数);
    }
}
