using Microsoft.Extensions.Logging.Abstractions;
using 上位机.业务.配置;
using 上位机.业务.服务;
using 上位机.通信.汇川;
using 上位机.核心.枚举;
using 上位机.核心.模型;
using 上位机.上位机测试.替身;

namespace 上位机.上位机测试;

/// <summary>
/// 参数页延时表 / 按钮表的 <b>PLC 回读</b>回归（2026-10-17 现场要求）。
///
/// <para><b>守的是什么：</b>延时（D 区）与按钮（M 区）的地址由管理员在界面上配置，
/// 不在 <c>tags.json</c> 里、不走轮询引擎；而 PLC 侧同样可能改写这些值
/// （切换机种、HMI 旁的操作面板、断电前的保持状态）。回读让界面显示 PLC 当下真实值，
/// 而不是本地配置值——否则现场会看到"改了的延时没生效"。</para>
///
/// <para><b>2026-10-21：</b>出厂配置换成现场版后，延时表是空的（现场只用了按钮表），
/// 因此本组用例<b>自带一份参数表</b>再跑——延时回读的语义与工厂里有没有延时无关。</para>
/// </summary>
[Collection("config-files")]
public class 参数回读测试
{
    /// <summary>用例自带的参数表：两条 DINT 延时（D1000 / D1002）+ 一条按钮（M5000）。</summary>
    private const string 测试参数表 = """
        {
          "延迟集合": [
            {
              "标识": "t1",
              "名称": "测试延时1",
              "地址": "D1000",
              "单元": "s",
              "最小": 0,
              "最大": 2147483.647,
              "启用": true,
              "顺序": 1
            },
            {
              "标识": "t2",
              "名称": "测试延时2",
              "地址": "D1002",
              "单元": "s",
              "最小": 0,
              "最大": 2147483.647,
              "启用": true,
              "顺序": 2
            }
          ],
          "按钮集合": [
            {
              "标识": "b1",
              "名称": "测试按钮",
              "地址": "M5000",
              "启用": true,
              "顺序": 1
            }
          ]
        }
        """;

    /// <summary>
    /// 装上测试参数表并构造被测服务。返回的 <c>原始</c> 必须写回（<see cref="恢复"/>）。
    /// </summary>
    private static async Task<(string 原始, 参数表格服务 服务, 替身Modbus客户端 客户端)> 新建服务()
    {
        var 路径 = 配置路径.参数表格;
        var 原始 = await File.ReadAllTextAsync(路径);
        await File.WriteAllTextAsync(路径, 测试参数表);

        var 客户端 = new 替身Modbus客户端();
        var 解析器 = new 汇川地址解析器(PLC系列.小型PLC);
        var 写入器 = new 标签写入器(
            客户端, new 替身标签仓储(), 解析器, NullLogger<标签写入器>.Instance);

        var 服务 = new 参数表格服务(
            解析器, 写入器, NullLogger<参数表格服务>.Instance, new 允许全部权限守卫());

        await 服务.异步初始化();

        return (原始, 服务, 客户端);
    }

    private static Task 恢复(string 原始) =>
        File.WriteAllTextAsync(配置路径.参数表格, 原始);

    [Fact]
    public async Task 回读_应读出DINT延时与按钮状态()
    {
        var (原始, 服务, 客户端) = await 新建服务();

        try
        {
            var 延时项 = 服务.延迟集合[0];   // 测试参数表第一条：D1000
            var 按钮项 = 服务.按钮集合[0];   // 测试参数表第一条：M5000

            Assert.Equal("D1000", 延时项.地址);
            Assert.Equal("M5000", 按钮项.地址);

            // PLC 侧的值：D1000 = 2500 ms（2.5 S，占 D1000/D1001 两字，低字在前）、M5000 = ON
            客户端.预设保持寄存器(1000, 2500);
            客户端.预设保持寄存器(1001, 0);
            客户端.预设线圈(5000, true);

            await 服务.异步回读();

            Assert.Equal(2.5, 服务.回读延时值(延时项.标识));
            Assert.True(服务.回读按钮状态(按钮项.标识));
        }
        finally
        {
            await 恢复(原始);
            await 客户端.DisposeAsync();
        }
    }

    [Fact]
    public async Task 回读_大延时应按32位正确换算()
    {
        var (原始, 服务, 客户端) = await 新建服务();

        try
        {
            var 延时项 = 服务.延迟集合[0];

            // 65536 ms = 65.536 S：16 位时代装不下的值，DINT 下低字 0、高字 1
            客户端.预设保持寄存器(1000, 0);
            客户端.预设保持寄存器(1001, 1);

            await 服务.异步回读();

            Assert.Equal(65.536, 服务.回读延时值(延时项.标识));
        }
        finally
        {
            await 恢复(原始);
            await 客户端.DisposeAsync();
        }
    }

    /// <summary>读不到的项只跳过自己：其余项照常回读，且不抛异常打断页面。</summary>
    [Fact]
    public async Task 回读_单项失败不应影响其它项()
    {
        var (原始, 服务, 客户端) = await 新建服务();

        try
        {
            var 甲 = 服务.延迟集合[0];   // D1000
            var 乙 = 服务.延迟集合[1];   // D1002

            // 只预设第一条：第二条查不到（按 0），正好验证"读不到也给出确定值、不抛异常"
            客户端.预设保持寄存器(1000, 3000);
            客户端.预设保持寄存器(1001, 0);

            await 服务.异步回读();

            Assert.Equal(3.0, 服务.回读延时值(甲.标识));
            Assert.Equal(0.0, 服务.回读延时值(乙.标识));
        }
        finally
        {
            await 恢复(原始);
            await 客户端.DisposeAsync();
        }
    }

    /// <summary>从未回读过的项返回 <c>null</c>（界面据此显示"—"而不是 0，避免误导）。</summary>
    [Fact]
    public async Task 回读前_应返回空而不是零值()
    {
        var (原始, 服务, 客户端) = await 新建服务();

        try
        {
            Assert.Null(服务.回读延时值(服务.延迟集合[0].标识));
            Assert.Null(服务.回读按钮状态(服务.按钮集合[0].标识));
        }
        finally
        {
            await 恢复(原始);
            await 客户端.DisposeAsync();
        }
    }

    /// <summary>停用的条目不回读（地址可能已被改成别的用途，读回来反而是噪声）。</summary>
    [Fact]
    public async Task 回读_停用条目应跳过()
    {
        var (原始, 服务, 客户端) = await 新建服务();

        try
        {
            var 条目 = 服务.延迟集合[0].克隆();
            条目.启用 = false;
            服务.更新(参数表格类别.延迟, 条目);

            客户端.预设保持寄存器(1000, 3000);
            客户端.预设保持寄存器(1001, 0);

            await 服务.异步回读();

            Assert.Null(服务.回读延时值(条目.标识));
        }
        finally
        {
            await 恢复(原始);
            await 客户端.DisposeAsync();
        }
    }

    /// <summary>回读间隔必须是 2s（与动作按钮同节奏：够快又不挤占串行 Modbus 链路）。</summary>
    [Fact]
    public void 回读间隔应为两秒()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), 参数表格服务.回读间隔);
    }
}
