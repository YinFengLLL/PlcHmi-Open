using Microsoft.Extensions.Logging.Abstractions;
using 上位机.业务.配置;
using 上位机.业务.服务;
using 上位机.核心.枚举;
using 上位机.核心.模型;
using 上位机.上位机测试.替身;

namespace 上位机.上位机测试;

/// <summary>
/// PLC 连接参数管理的服务层测试。
///
/// <para><b>守的是什么</b>：这是系统设置页唯一的"改完立刻改变通信目标"的入口——
/// IP 填错程序就连不上 PLC，端口 / 从站号填错则读写打不到正确的从站；
/// 而保存动作会重启连接，如果校验被绕过，一次误操作就能把产线通信打断。
/// 所以范围校验必须落在服务层（界面只是隐藏入口），且"保存 = 落盘 + 更新运行参数 + 重启连接"
/// 这条链路必须有测试锁定。</para>
///
/// <para>保存会写测试输出目录里的 <c>config/appsettings.json</c>，归入 config-files
/// 集合禁用并行，并在用例内备份 / 还原原文件。</para>
/// </summary>
[Collection("config-files")]
public class PLC连接设置服务测试
{
    private static PLC连接选项 新选项() => new()
    {
        主机 = "192.168.1.10",
        端口 = 502,
        单元标识 = 1,
        系列 = PLC系列.小型PLC,
        使用模拟器 = true,
        连接超时毫秒 = 3000,
        读取超时毫秒 = 1000,
        心跳间隔毫秒 = 5000,
        重连初始延迟毫秒 = 1000,
        重连最大延迟毫秒 = 30000,
        每次请求最大寄存器数 = 100,
        自动重连 = true,
    };

    private static (PLC连接设置服务 服务, PLC连接选项 Options, 替身连接管理器 Connection) 新服务()
    {
        var 选项 = 新选项();
        var 连接 = new 替身连接管理器();
        var 服务实例 = new PLC连接设置服务(
            选项, 连接, new 允许全部权限守卫(),
            NullLogger<PLC连接设置服务>.Instance);
        return (服务实例, 选项, 连接);
    }

    [Fact]
    public async Task 合法参数保存后应更新运行参数并落盘并重启连接()
    {
        var (服务实例, 选项, 连接) = 新服务();
        var 原始 = await File.ReadAllTextAsync(配置路径.应用设置);

        try
        {
            var 编辑 = 服务实例.创建编辑模型();
            编辑.主机 = "192.168.2.77";
            编辑.端口 = 1502;
            编辑.单元标识 = 3;

            await 服务实例.异步保存(编辑);

            // 1) 共享运行参数被更新 —— 通信客户端下次连接就用它
            Assert.Equal("192.168.2.77", 选项.主机);
            Assert.Equal(1502, 选项.端口);
            Assert.Equal(3, 选项.单元标识);

            // 2) 连接被重启（Stop 断开旧连接 → Start 用新参数重连）
            Assert.Equal(1, 连接.停止次数);
            Assert.Equal(1, 连接.启动次数);

            // 3) 落盘：重新读文件确认新值，且不可编辑字段以运行态保留
            var 已重载 = 应用设置加载器.加载(out var 错误);
            Assert.Null(错误);
            Assert.Equal("192.168.2.77", 已重载.PLC连接.主机);
            Assert.Equal(1502, 已重载.PLC连接.端口);
            Assert.Equal(3, 已重载.PLC连接.单元标识);
            // 运行模式（模拟开关）是运行期状态，Modbus 模式下恒以 false 落盘：
            // 每次启动一律真实 PLC，避免生产机台重启后"记住"上次调试的模拟选择
            Assert.False(已重载.PLC连接.使用模拟器,
                "Modbus 运行模式不跨重启记忆：保存连接参数时模拟开关必须写回 false");
            Assert.Equal(PLC系列.小型PLC, 已重载.PLC连接.系列);
        }
        finally
        {
            // 还原配置文件：本用例改的是测试输出目录里的副本，不还原会污染后续读配置的用例
            await File.WriteAllTextAsync(配置路径.应用设置, 原始);
        }
    }

    [Theory]
    [InlineData("", 502, 1)]            // 空地址
    [InlineData("abc!", 502, 1)]        // 非法主机名
    [InlineData("192.168.1.10", 0, 1)]  // 端口下界
    [InlineData("192.168.1.10", 65536, 1)]  // 端口上界
    [InlineData("192.168.1.10", 502, 0)]    // 从站号下界
    [InlineData("192.168.1.10", 502, 248)]  // 从站号上界
    public void 非法地址端口从站号应被拒绝(string 主机, int 端口号, int 单元标识值)
    {
        var (服务实例, _, _) = 新服务();
        var 编辑 = 服务实例.创建编辑模型();
        编辑.主机 = 主机;
        编辑.端口 = 端口号;
        编辑.单元标识 = 单元标识值;

        Assert.NotEmpty(服务实例.校验(编辑));
    }

    [Theory]
    [InlineData(99, 1000, 1000, 100, 1000, 100)]   // 连接超时 < 100
    [InlineData(3000, 99, 1000, 100, 1000, 100)]   // 读写超时 < 100
    [InlineData(3000, 1000, 499, 100, 1000, 100)]  // 心跳 < 500（过密打挂 PLC）
    [InlineData(3000, 1000, 1000, 99, 1000, 100)]  // 重连初始 < 100
    [InlineData(3000, 1000, 1000, 100, 50, 100)]   // 退避上限 < 初始
    [InlineData(3000, 1000, 1000, 100, 1000, 0)]   // 寄存器数下界
    [InlineData(3000, 1000, 1000, 100, 1000, 124)] // 寄存器数超汇川读上限
    public void 超出范围的通信参数应被拒绝(
        int 连接超时, int 读取超时, int 心跳,
        int 重连初始, int 重连最大, int 最大寄存器)
    {
        var (服务实例, _, _) = 新服务();
        var 编辑 = 服务实例.创建编辑模型();
        编辑.连接超时毫秒 = 连接超时;
        编辑.读取超时毫秒 = 读取超时;
        编辑.心跳间隔毫秒 = 心跳;
        编辑.重连初始延迟毫秒 = 重连初始;
        编辑.重连最大延迟毫秒 = 重连最大;
        编辑.每次请求最大寄存器数 = 最大寄存器;

        Assert.NotEmpty(服务实例.校验(编辑));
    }

    [Fact]
    public void 默认参数应通过校验()
    {
        var (服务实例, _, _) = 新服务();
        var 编辑 = 服务实例.创建编辑模型();

        Assert.Empty(服务实例.校验(编辑));
    }

    [Theory]
    [InlineData(0, 2)]        // OPC UA 端口下界
    [InlineData(65536, 2)]    // OPC UA 端口上界
    [InlineData(4840, 0)]     // 命名空间索引下界（ns=0 是标准命名空间保留段）
    public void 非法OPCUA参数应被拒绝(int opcua端口, int 命名空间)
    {
        var (服务实例, _, _) = 新服务();
        var 编辑 = 服务实例.创建编辑模型();
        编辑.OPCUA端口 = opcua端口;
        编辑.OPCUA命名空间索引 = 命名空间;

        Assert.NotEmpty(服务实例.校验(编辑));
    }

    [Fact]
    public async Task 切换通讯协议应落盘并在下次启动生效()
    {
        var (服务实例, 选项, 连接) = 新服务();
        var 原始 = await File.ReadAllTextAsync(配置路径.应用设置);

        try
        {
            var 编辑 = 服务实例.创建编辑模型();
            Assert.Equal(通讯协议.ModbusTCP, 编辑.通讯协议);

            编辑.通讯协议 = 通讯协议.OPCUA;
            编辑.OPCUA端口 = 4840;
            编辑.OPCUA命名空间索引 = 3;

            await 服务实例.异步保存(编辑);

            // 1) 落盘：重启后 DI 按新协议装配
            var 已重载 = 应用设置加载器.加载(out var 错误);
            Assert.Null(错误);
            Assert.Equal(通讯协议.OPCUA, 已重载.PLC连接.通讯协议);
            Assert.Equal(4840, 已重载.PLC连接.OPCUA端口);
            Assert.Equal(3, 已重载.PLC连接.OPCUA命名空间索引);

            // 2) 共享运行参数同步（下次连接按新端口；协议本身仍需重启才切换客户端实现）
            Assert.Equal(通讯协议.OPCUA, 选项.通讯协议);
            Assert.Equal(3, 选项.OPCUA命名空间索引);

            // 3) 连接被重启
            Assert.Equal(1, 连接.停止次数);
            Assert.Equal(1, 连接.启动次数);
        }
        finally
        {
            await File.WriteAllTextAsync(配置路径.应用设置, 原始);
        }
    }

    [Fact]
    public async Task 校验不通过时保存应抛异常且不改动任何状态()
    {
        var (服务实例, 选项, 连接) = 新服务();
        var 原始 = await File.ReadAllTextAsync(配置路径.应用设置);

        // 初始值从配置读出（2026-10-12 真机 IP 改为 192.168.1.10，不能在这里硬编码）
        var 初始主机 = 选项.主机;
        var 初始端口 = 选项.端口;

        try
        {
            var 编辑 = 服务实例.创建编辑模型();
            编辑.主机 = "10.0.0.99";
            编辑.端口 = 0;   // 非法

            await Assert.ThrowsAsync<InvalidOperationException>(() => 服务实例.异步保存(编辑));

            // 内存、文件、连接都不应被触碰
            Assert.Equal(初始主机, 选项.主机);
            Assert.Equal(初始端口, 选项.端口);
            Assert.Equal(0, 连接.停止次数);
            Assert.Equal(0, 连接.启动次数);

            // 文件未被触碰：与用例开始时的原始内容逐字一致（不比对具体 IP，避免与现场配置耦合）
            Assert.Equal(原始,
                await File.ReadAllTextAsync(配置路径.应用设置));
        }
        finally
        {
            await File.WriteAllTextAsync(配置路径.应用设置, 原始);
        }
    }

    [Fact]
    public void 连接参数编辑权限应仅管理员拥有()
    {
        Assert.True(角色权限.有(用户角色.管理员, 权限.编辑PLC连接));
        Assert.False(角色权限.有(用户角色.工程师, 权限.编辑PLC连接));
        Assert.False(角色权限.有(用户角色.操作员, 权限.编辑PLC连接));
    }
}
