using Microsoft.Extensions.Logging.Abstractions;
using 上位机.业务.配置;
using 上位机.业务.服务;
using 上位机.通信.汇川;
using 上位机.通信.仿真;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;
using 上位机.上位机测试.替身;

namespace 上位机.上位机测试;

/// <summary>
/// 页面地址编辑的<b>结构体成员</b>部分（2026-10-21）：勾选"结构体成员"后保存，
/// 成员偏移与结构体长度必须按成员顺序与数据类型重排，页面上的地址提示随之更新。
///
/// <para>单独一个文件：<see cref="页面地址服务测试"/> 已接近 400 行规模门禁。</para>
/// </summary>
[Collection("config-files")]
public class 页面地址结构体成员测试
{
    private static 页面地址服务 新服务(Modbus客户端接口 连接) =>
        new(连接,
            new 标签写入器(连接, new 标签仓储(NullLogger<标签仓储>.Instance),
                new 汇川地址解析器(PLC系列.小型PLC), NullLogger<标签写入器>.Instance),
            配方配置加载器.加载(out _),
            new 允许全部权限守卫(),
            NullLogger<页面地址服务>.Instance);

    /// <summary>
    /// 现场要求：点胶动作区首个按钮固定为「参数模式」（M5006，0 默认参数 / 1 独立参数），
    /// 与其他动作按钮同风格，地址可在「编辑地址」里改，但不能删除。
    /// </summary>
    [Fact]
    public async Task 收集点胶页应把参数模式固定按钮排在动作区首位()
    {
        await using var 客户端 = new 替身Modbus客户端();
        var 服务实例 = 新服务(客户端);

        var 动作 = 服务实例.收集点表页("点胶").Where(项 => 项.类别 == "动作按钮").ToList();

        Assert.NotEmpty(动作);
        Assert.Equal("点胶-参数模式", 动作[0].键);
        Assert.Equal("M5008", 动作[0].地址文本);   // 现场稳定版（2026-11-10）把参数模式挪到 M5008
        Assert.False(动作[0].可删除);                        // 固定元件：不可删
        Assert.All(动作.Skip(1), 项 => Assert.True(项.可删除));

        // 服务层排序同样把固定按钮放最前
        Assert.Equal("点胶-参数模式", 服务实例.动作按钮("点胶")[0].键);

        // 锁附页没有这个按钮
        Assert.DoesNotContain(
            服务实例.收集点表页("锁附"),
            项 => 项.类别 == "动作按钮" && 项.键 == "点胶-参数模式");
    }

    /// <summary>
    /// 现场报障：把「抬针高度」数据类型改成 INT16 后，后面的「点胶速度」必须从 D3506 前移到 D3505，
    /// 结构体长度从 8 变 7；页面重新收集时显示的地址同样要跟上。
    /// </summary>
    [Fact]
    public async Task 保存点表页应重排结构体成员偏移与长度()
    {
        var 配方路径 = 配置路径.配方配置;
        var 原始 = await File.ReadAllTextAsync(配方路径);

        try
        {
            await using var 客户端 = new 替身Modbus客户端();
            var 服务实例 = 新服务(客户端);

            var 项集合 = 服务实例.收集点表页("点胶").ToList();
            var 抬针 = 项集合.First(项 => 项.类别 == "辅助地址" && 项.键 == "点胶-抬针高度");

            Assert.True(抬针.结构体成员);
            Assert.True(抬针.可设结构体成员);
            Assert.Equal("D3504", 抬针.地址文本);

            抬针.数据类型文本 = "Int16";   // REAL(2 字) → INT(1 字)

            await 服务实例.异步保存点表页("点胶", new 地址编辑请求
            {
                标题 = "点胶示教",
                默认分组 = "点胶",
                项集合 = 项集合,
            });

            var 配置 = 配方配置加载器.加载(out _);
            var 抬针高度 = 配置.辅助地址.First(项 => 项.键 == "点胶-抬针高度");
            var 点胶速度 = 配置.辅助地址.First(项 => 项.键 == "点胶-点胶速度");

            Assert.Equal(标签数据类型.Int16, 抬针高度.数据类型);
            Assert.Equal(4, 抬针高度.结构体内偏移);
            Assert.Equal("D3504", 抬针高度.地址);

            Assert.Equal(5, 点胶速度.结构体内偏移);   // ← 前移一个字
            Assert.Equal("D3505", 点胶速度.地址);     // ← 界面必须显示这个
            Assert.Equal(7, 点胶速度.结构体长度);     // ← 长度从 8 变 7

            // 页面重新收集（保存后刷新依据）也要拿到新地址
            var 重新收集 = 服务实例.收集点表页("点胶").First(项 => 项.键 == "点胶-点胶速度");
            Assert.Equal("D3505", 重新收集.地址文本);
        }
        finally
        {
            await File.WriteAllTextAsync(配方路径, 原始);
        }
    }

    /// <summary>
    /// 取消勾选「结构体成员」后：结构体三参数清零、其余成员重排；
    /// 重新加载时不得被"老配置补齐"逻辑又认回成员（否则勾选框自己变回去）。
    /// </summary>
    [Fact]
    public async Task 取消结构体成员保存后不应被老配置补齐恢复()
    {
        var 配方路径 = 配置路径.配方配置;
        var 原始 = await File.ReadAllTextAsync(配方路径);

        try
        {
            await using var 客户端 = new 替身Modbus客户端();
            var 服务实例 = 新服务(客户端);

            var 项集合 = 服务实例.收集点表页("点胶").ToList();
            var 抬针 = 项集合.First(项 => 项.类别 == "辅助地址" && 项.键 == "点胶-抬针高度");
            抬针.结构体成员 = false;
            抬针.地址文本 = "D3760";   // 退出结构体后用自己的固定地址

            await 服务实例.异步保存点表页("点胶", new 地址编辑请求
            {
                标题 = "点胶示教",
                默认分组 = "点胶",
                项集合 = 项集合,
            });

            var 配置 = 配方配置加载器.加载(out _);   // 走一遍加载（含老配置补齐）
            var 抬针高度 = 配置.辅助地址.First(项 => 项.键 == "点胶-抬针高度");
            var 点胶速度 = 配置.辅助地址.First(项 => 项.键 == "点胶-点胶速度");

            Assert.False(抬针高度.结构体成员);
            Assert.Equal(0, 抬针高度.结构体基址);
            Assert.Equal(0, 抬针高度.结构体长度);
            Assert.Equal("D3760", 抬针高度.地址);

            // 少了一个 2 字成员：长度 2+1+1+2 = 6，点胶速度回到偏移 4
            Assert.Equal(4, 点胶速度.结构体内偏移);
            Assert.Equal(6, 点胶速度.结构体长度);
        }
        finally
        {
            await File.WriteAllTextAsync(配方路径, 原始);
        }
    }
}
