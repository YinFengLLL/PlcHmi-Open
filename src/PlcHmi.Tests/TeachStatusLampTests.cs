using Microsoft.Extensions.Logging.Abstractions;
using 上位机.业务.配置;
using 上位机.业务.服务;
using 上位机.通信.汇川;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;
using 上位机.上位机测试.替身;

namespace 上位机.上位机测试;

/// <summary>
/// 示教页顶部固定状态灯（各轴回原状态）的配置与校验测试。
///
/// <para><b>2026-10-16 现场要求：</b>锁附示教 / 点胶示教页各放一盏固定状态灯，读指定 M 区位
/// （0 = 各轴未回原 → 闪烁；1 = 各轴回原完成 → 常亮），地址由管理员在「编辑地址」里改。
/// 本组钉住三件事：<b>老配置升级后灯仍在</b>（缺条目按缺省地址合成）、
/// <b>地址能被管理员改并落盘</b>、<b>写成 D 区必须被拒绝</b>（灯用 FC01 读位，D 区是字）。</para>
/// </summary>
[Collection("config-files")]
public class 状态灯配置测试
{
    private static 页面地址服务 新服务(配方配置 配置项, Modbus客户端接口 连接)
    {
        var 解析器 = new 汇川地址解析器(PLC系列.小型PLC);

        return new 页面地址服务(
            连接,
            new 标签写入器(连接, new 标签仓储(NullLogger<标签仓储>.Instance), 解析器,
                NullLogger<标签写入器>.Instance),
            配置项,
            new 允许全部权限守卫(),
            NullLogger<页面地址服务>.Instance);
    }

    [Fact]
    public async Task 配置缺条目时应按缺省地址合成状态灯()
    {
        await using var 客户端 = new 替身Modbus客户端();

        // 空配置 = 现场升级后 recipe-config.json 里还没有"状态灯"段
        var 服务实例 = 新服务(new 配方配置(), 客户端);

        var 列表 = 服务实例.状态灯("锁附");

        var 灯项 = Assert.Single(列表);
        Assert.Equal("回原状态灯", 灯项.键);
        Assert.Equal("锁附", 灯项.分组);
        Assert.Equal(状态灯项.缺省地址, 灯项.地址);
    }

    [Fact]
    public async Task 收集锁附页应含状态灯条目且不可删除()
    {
        await using var 客户端 = new 替身Modbus客户端();
        var 服务实例 = 新服务(配方配置加载器.加载(out _), 客户端);

        var 列表 = 服务实例.收集点表页("锁附");

        var 灯条目 = Assert.Single(列表, 项 => 项.类别 == "状态灯");
        Assert.Equal("M430", 灯条目.地址文本);
        Assert.Equal(标签数据类型.布尔.ToString(), 灯条目.数据类型文本);

        // 固定元件：可改地址不可删（删了页面就没有灯，现场也不知道该找谁）
        Assert.False(灯条目.可删除);
    }

    [Fact]
    public async Task 点胶页状态灯应与锁附页各自独立()
    {
        await using var 客户端 = new 替身Modbus客户端();
        var 服务实例 = 新服务(配方配置加载器.加载(out _), 客户端);

        Assert.Single(服务实例.状态灯("锁附"));
        Assert.Single(服务实例.状态灯("点胶"));

        // 点胶页不应混入锁附分组的条目（分组过滤生效）
        Assert.All(服务实例.状态灯("点胶"), 灯项 => Assert.Equal("点胶", 灯项.分组));
    }

    [Fact]
    public async Task 保存点表页应写回状态灯地址并按M区规范化()
    {
        var 配方路径 = 配置路径.配方配置;
        var 原始 = await File.ReadAllTextAsync(配方路径);

        try
        {
            await using var 客户端 = new 替身Modbus客户端();
            var 服务实例 = 新服务(配方配置加载器.加载(out _), 客户端);

            var 请求 = new 地址编辑请求
            {
                标题 = "锁附示教",
                默认分组 = "锁附",
                项集合 = 服务实例.收集点表页("锁附").ToList(),
            };

            // 小写 + 无前缀规范化：现场手输时大小写随意，落盘必须统一成 M 区大写
            请求.项集合.First(项 => 项.类别 == "状态灯").地址文本 = "m466";

            await 服务实例.异步保存点表页("锁附", 请求);

            var 配置 = 配方配置加载器.加载(out _);
            var 灯项 = 配置.状态灯.First(项 => 项.分组 == "锁附");
            Assert.Equal("M466", 灯项.地址);

            // 点胶分组不受影响（现场两页都用 M430）
            Assert.Equal("M430", 配置.状态灯.First(项 => 项.分组 == "点胶").地址);
        }
        finally
        {
            await File.WriteAllTextAsync(配方路径, 原始);
        }
    }

    [Fact]
    public async Task 保存点表页时状态灯写成D区应被拒绝()
    {
        var 配方路径 = 配置路径.配方配置;
        var 原始 = await File.ReadAllTextAsync(配方路径);

        try
        {
            await using var 客户端 = new 替身Modbus客户端();
            var 服务实例 = 新服务(配方配置加载器.加载(out _), 客户端);

            var 请求 = new 地址编辑请求
            {
                标题 = "锁附示教",
                默认分组 = "锁附",
                项集合 = 服务实例.收集点表页("锁附").ToList(),
            };

            // D 区是字元件：灯用 FC01 读位，写错区会"能存但读错"，必须当场拒绝
            请求.项集合.First(项 => 项.类别 == "状态灯").地址文本 = "D500";

            var 异常 = await Assert.ThrowsAsync<InvalidOperationException>(
                () => 服务实例.异步保存点表页("锁附", 请求));

            Assert.Contains("M 区", 异常.Message, StringComparison.Ordinal);

            // 拒绝时不得改到磁盘上的配置
            Assert.Equal(原始, await File.ReadAllTextAsync(配方路径));
        }
        finally
        {
            await File.WriteAllTextAsync(配方路径, 原始);
        }
    }
}
