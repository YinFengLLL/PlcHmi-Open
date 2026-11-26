using Microsoft.Extensions.Logging.Abstractions;
using 上位机.业务.配置;
using 上位机.业务.服务;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.上位机测试;

/// <summary>
/// IO 点位「新增一栏」的服务层测试。
///
/// <para><b>守的是什么</b>：新增一栏做的是三件事 —— 算编号、改采集范围、写配置。
/// 编号算错（把八进制当十进制）会让点位指到错误的 PLC 地址，属于"看着正常、读的是别处"；
/// 落盘失败却返回成功，重启后新增的栏会凭空消失。</para>
///
/// <para>测试会真实写 <c>config/io-points.json</c>，但那是**测试输出目录**下的
/// 那份（<see cref="配置路径"/> 以 AppContext.BaseDirectory 为根），不碰仓库里的 config/。
/// 与其它写配置的测试共用同一集合，禁用并行。</para>
/// </summary>
[Collection("config-files")]
public sealed class IO点位新增一栏测试 : IDisposable
{
    /// <summary>
    /// 原始 <c>config/io-points.json</c> 快照。本类用例会把测试输出目录里的这份文件写成"只剩几栏"
    /// 的状态，而<b>其它测试类（如端到端仿真测试）会读同一份文件</b>——不还原就构成跨类污染：
    /// 端到端测试曾因此在「X10 变为 ON」上超时（点位表被改小，X10 根本不在表里）。
    /// 故每个用例结束后还原，保证并行读取的其它类看到的仍是仓库配置。
    /// </summary>
    private readonly string? _原始内容;

    public IO点位新增一栏测试()
    {
        _原始内容 = File.Exists(配置路径.IO点位集合)
            ? File.ReadAllText(配置路径.IO点位集合)
            : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_原始内容 is not null)
        {
            File.WriteAllText(配置路径.IO点位集合, _原始内容);
        }
    }

    /// <summary>清空两侧点位，让每个用例都从已知状态出发（配置文件可能残留上一次运行的产物）。</summary>
    private static IO点位服务 新服务(权限守卫接口? 权限 = null)
    {
        var 服务实例 = new IO点位服务(
            权限 ?? new 允许全部权限守卫(), NullLogger<IO点位服务>.Instance);

        服务实例.表格.输入.Clear();
        服务实例.表格.输出.Clear();
        服务实例.表格.元 = new IO点位表格元数据 { 分组大小 = 8 };

        return 服务实例;
    }

    /// <summary>未登录 → 操作员（最低权限）→ 一切写操作都应被拒。走的是生产用的真实守卫。</summary>
    private static 权限守卫接口 操作员权限() =>
        new 用户权限守卫(new 用户服务(NullLogger<用户服务>.Instance),
            NullLogger<用户权限守卫>.Instance);

    private static IEnumerable<IO点位> 生成点位(string 前缀, params int[] 序号列表) =>
        序号列表.Select(序号 => new IO点位 { 代码 = 前缀 + Convert.ToString(序号, 8), 启用 = true });

    private static void 填充(IO点位服务 服务实例, string 前缀, params int[] 序号列表)
    {
        if (前缀 == "X")
        {
            服务实例.表格.输入.AddRange(生成点位(前缀, 序号列表));
        }
        else
        {
            服务实例.表格.输出.AddRange(生成点位(前缀, 序号列表));
        }
    }

    [Fact]
    public async Task 空表新增一栏应从X0开始()
    {
        var 服务实例 = 新服务();

        var 新增 = await 服务实例.异步新增一栏("X");

        Assert.Equal(8, 新增.Count);
        Assert.Equal("X0", 新增[0].代码);
        Assert.Equal("X7", 新增[^1].代码);
        Assert.All(新增, 点位项 => Assert.True(点位项.启用));
        Assert.Equal(8, 服务实例.表格.元.输入数量);
    }

    [Fact]
    public async Task 新增一栏应按八进制对齐到下一个分组()
    {
        // 这是本功能最容易写错的地方：X27 的八进制序号是 23，
        // 下一栏起点是 24（八进制 30），而不是十进制的 28。
        var 服务实例 = 新服务();
        填充(服务实例, "X", 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23);

        var 新增 = await 服务实例.异步新增一栏("X");

        Assert.Equal(8, 新增.Count);
        Assert.Equal("X30", 新增[0].代码);
        Assert.Equal("X37", 新增[^1].代码);
        Assert.Equal(32, 服务实例.表格.输入.Count);

        // 已落盘的编号必须能被地址解析器接受 —— 否则轮询编译时会被静默跳过
        var 解析器 = new 上位机.通信.汇川.汇川地址解析器(PLC系列.小型PLC);
        Assert.All(新增, 点位项 => Assert.True(解析器.尝试解析(点位项.代码, out _)));
    }

    [Fact]
    public async Task Y侧新增不应影响X侧()
    {
        var 服务实例 = 新服务();
        填充(服务实例, "X", 0, 1, 2, 3, 4, 5, 6, 7);
        填充(服务实例, "Y", 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15);

        var 新增 = await 服务实例.异步新增一栏("Y");

        Assert.Equal("Y20", 新增[0].代码);
        Assert.Equal("Y27", 新增[^1].代码);
        Assert.Equal(8, 新增.Count);
        Assert.Equal(24, 服务实例.表格.输出.Count);
        Assert.Equal(8, 服务实例.表格.输入.Count);   // X 侧未被牵动
        Assert.Equal(24, 服务实例.表格.元.输出数量);
        Assert.Equal(0, 服务实例.表格.元.输入数量);
    }

    [Fact]
    public async Task 预览下一栏不应改动数据()
    {
        var 服务实例 = 新服务();
        填充(服务实例, "X", 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23);

        var 预览 = 服务实例.预览下一栏("X");

        Assert.Equal("X30 — X37", 预览);
        Assert.Equal(24, 服务实例.表格.输入.Count);
    }

    [Fact]
    public async Task 编号越出编址上限应整栏拒绝()
    {
        var 服务实例 = 新服务();
        // 八进制 1777 = 十进制 1023，正好是汇川 X 区编址上限
        填充(服务实例, "X", Convert.ToInt32("1777", 8));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => 服务实例.异步新增一栏("X"));

        // 不允许"加到一半失败"的部分写入
        Assert.Single(服务实例.表格.输入);
    }

    /// <summary>
    /// 服务层权限边界：界面隐藏按钮只是防误触，绕过它的成本极低（审查标准 R5）。
    /// 被拒的调用必须<b>不留痕迹</b>——表未变、也未落盘。
    /// </summary>
    [Fact]
    public async Task 无权限新增一栏应被拒绝且不改动点位表()
    {
        var 服务实例 = 新服务(操作员权限());
        填充(服务实例, "X", 0, 1, 2, 3, 4, 5, 6, 7);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => 服务实例.异步新增一栏("X"));

        Assert.Equal(8, 服务实例.表格.输入.Count);
        Assert.Equal(0, 服务实例.表格.元.输入数量);   // 元数据也没被改
    }
}
