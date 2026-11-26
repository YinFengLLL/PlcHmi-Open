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
/// 页面地址服务测试。
///
/// <para><b>守的是什么</b>：现场要求"页面上所有与 PLC 交互的元件都能改地址"。
/// 这个服务是唯一的收集 / 保存入口，一旦漏收一类元件，现场就会出现
/// "这个按钮地址改不了，只能去改 JSON"的返工。同时它承担管理员权限边界。</para>
/// </summary>
[Collection("config-files")]
public class 页面地址服务测试
{
    /// <summary>全部拒绝的权限守卫（仅测试用）。</summary>
    private sealed class 拒绝全部权限守卫 : 权限守卫接口
    {
        public void 要求(权限 权限项) =>
            throw new UnauthorizedAccessException($"测试：缺少「{权限项}」权限。");
    }

    private static 页面地址服务 新服务(权限守卫接口 守卫, Modbus客户端接口 连接)
    {
        var 解析器 = new 汇川地址解析器(PLC系列.小型PLC);

        return new 页面地址服务(
            连接,
            new 标签写入器(连接, new 标签仓储(NullLogger<标签仓储>.Instance), 解析器,
                NullLogger<标签写入器>.Instance),
            配方配置加载器.加载(out _),
            守卫,
            NullLogger<页面地址服务>.Instance);
    }

    [Fact]
    public async Task 收集锁附页应覆盖全部五类元件()
    {
        await using var 客户端 = new 替身Modbus客户端();
        var 服务实例 = 新服务(new 允许全部权限守卫(), 客户端);
        var 列表 = 服务实例.收集点表页("锁附");

        // 点表列：X / Y / 浮高
        var 点表列 = 列表.Where(项 => 项.类别 == "点表列").ToList();
        Assert.Equal(3, 点表列.Count);
        Assert.Contains(点表列, 项 => 项.键 == "浮高" && 项.地址文本 == "D2620");

        // 动作按钮、点动、回零、辅助地址都要收齐
        Assert.Contains(列表, 项 => 项.类别 == "动作按钮" && 项.名称 == "电机复位");
        Assert.Contains(列表, 项 => 项.类别 == "点动" && 项.键 == "X|正转" && 项.地址文本 == "M1002");
        Assert.Contains(列表, 项 => 项.类别 == "回零" && 项.键 == "ZZ" && 项.地址文本 == "M443");
        Assert.Contains(列表, 项 => 项.类别 == "辅助地址" && 项.键 == "上料点Y" && 项.可删除);

        // 锁附页只收锁附分组的点动轴（X/Y/Z/ZZ），不收点胶轴
        Assert.DoesNotContain(列表, 项 => 项.类别 == "点动" && 项.键.StartsWith("GlueX", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 收集点胶页应只含点胶分组的轴()
    {
        await using var 客户端 = new 替身Modbus客户端();
        var 服务实例 = 新服务(new 允许全部权限守卫(), 客户端);
        var 列表 = 服务实例.收集点表页("点胶");

        var 点动轴 = 列表
            .Where(项 => 项.类别 == "点动")
            .Select(项 => 项.键.Split('|')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 点胶 X / Y（与锁附 Y 共轴）/ Z
        Assert.Equal(3, 点动轴.Count);
        Assert.Contains("GlueX", 点动轴);
        Assert.Contains("Y", 点动轴);
        Assert.Contains("GlueZ", 点动轴);

        // 指令码列也在点表列里
        Assert.Contains(列表, 项 => 项.类别 == "点表列" && 项.键 == "指令码" && 项.地址文本 == "D3400");
    }

    [Fact]
    public async Task 收集标定页应含中心点偏移与标定启动()
    {
        await using var 客户端 = new 替身Modbus客户端();
        var 服务实例 = 新服务(new 允许全部权限守卫(), 客户端);
        var 列表 = 服务实例.收集标定页();

        // 地址对齐现场稳定版（2026-11-10 现场配置为准）：上相机 X/Y/Z = D1650 / D1652 / D1554、
        // 偏移 D1654、启动 M84（现场把标定启动从 M82 挪到 M84，M83 留给下相机）
        Assert.Contains(列表, 项 => 项.类别 == "标定字段" && 项.键 == "X" && 项.地址文本 == "D1650");
        Assert.Contains(列表, 项 => 项.类别 == "标定字段" && 项.键 == "Y" && 项.地址文本 == "D1652");
        Assert.Contains(列表, 项 => 项.类别 == "标定字段" && 项.键 == "Z" && 项.地址文本 == "D1554");
        Assert.Contains(列表, 项 => 项.类别 == "标定字段" && 项.键 == "偏移量" && 项.地址文本 == "D1654");
        Assert.Contains(列表, 项 => 项.类别 == "标定按钮" && 项.键 == "启动" && 项.地址文本 == "M84");

        // 固定标定按钮不再有「移至指定位置」（现场改用「标定」分组的可配置动作按钮）
        Assert.DoesNotContain(列表, 项 => 项.类别 == "标定按钮" && 项.名称.Contains("移至", StringComparison.Ordinal));
    }

    /// <summary>
    /// 下相机（2026-10-17 新增）：地址处理模式与上相机一致，但**只有 X / Z，没有 Y**。
    /// <para>键必须带「下相机」前缀——编辑器的行按「类别 + 键」定位，
    /// 与上相机的 X / Z 重键会把两套地址写串（改下相机会把上相机改掉）。</para>
    /// </summary>
    [Fact]
    public async Task 收集标定页应含下相机三项且没有Y()
    {
        await using var 客户端 = new 替身Modbus客户端();
        var 服务实例 = 新服务(new 允许全部权限守卫(), 客户端);
        var 列表 = 服务实例.收集标定页();

        // 现场配置已带下相机 X/Z（2026-11-10 现场稳定版）：X=D1660 / Z=D1556、偏移沿用 D1664、启动 M83
        Assert.Contains(列表, 项 => 项.类别 == "标定字段" && 项.键 == "下相机X" && 项.地址文本 == "D1660");
        Assert.Contains(列表, 项 => 项.类别 == "标定字段" && 项.键 == "下相机Z" && 项.地址文本 == "D1556");
        Assert.Contains(列表, 项 => 项.类别 == "标定字段" && 项.键 == "下相机偏移量" && 项.地址文本 == "D1664");
        Assert.Contains(列表, 项 => 项.类别 == "标定按钮" && 项.键 == "下相机启动" && 项.地址文本 == "M83");

        // 下相机没有 Y：不得出现「下相机Y」这一行
        Assert.DoesNotContain(列表, 项 => 项.键.Contains("下相机Y", StringComparison.Ordinal));
    }

    /// <summary>
    /// 出厂配置自检：下相机只有 X / Z（<b>没有 Y</b>），且全部标定字段地址互不重叠。
    /// <para>Float32 每项占 2 字：上相机 3600/3602/3604/3606（各 2 字）之后，
    /// 下相机从 3608 起排——一旦地址写重叠，两个字段会互相覆盖寄存器（值看着像"随机跳"）。</para>
    /// </summary>
    [Fact]
    public void 出厂配置_下相机应只有X与Z且地址不与上相机重叠()
    {
        var 配置项 = 配方配置加载器.加载(out _).标定;

        Assert.Equal(2, 配置项.下相机中心点.Count);
        Assert.Equal(["X", "Z"], 配置项.下相机中心点.Select(字段项 => 字段项.键).ToArray());
        Assert.Null(配置项.查找下相机("Y"));   // 下相机没有 Y

        // 全部标定字段（上相机 X/Y/Z + 上相机偏移 + 下相机 X/Z + 下相机偏移）两两不重叠
        var 区间列表 = 配置项.中心点
            .Concat([配置项.每点偏移])
            .Concat(配置项.下相机中心点)
            .Concat([配置项.下相机每点偏移])
            .Select(字段项 => (字段项.显示名称, 起: 字段项.基址, 止: 字段项.基址 + 字段项.寄存器数量 - 1))
            .ToList();

        for (var 甲 = 0; 甲 < 区间列表.Count; 甲++)
        {
            for (var 乙 = 甲 + 1; 乙 < 区间列表.Count; 乙++)
            {
                var 交叉 = 区间列表[甲].起 <= 区间列表[乙].止 && 区间列表[乙].起 <= 区间列表[甲].止;

                Assert.False(交叉,
                    $"{区间列表[甲].显示名称}(D{区间列表[甲].起}) 与 {区间列表[乙].显示名称}(D{区间列表[乙].起}) 地址重叠");
            }
        }

        // 两个启动按钮也必须是不同的 M 位（同址会同时触发两台相机的标定）
        Assert.NotEqual(配置项.启动地址, 配置项.下相机启动地址);
    }

    /// <summary>
    /// 改下相机地址不得动上相机：两条地址链共用一个编辑器，
    /// 键前缀写错就会"改下相机、上相机跟着变"（数据串位，现场极难查）。
    /// </summary>
    [Fact]
    public async Task 保存标定页_下相机地址不得写串到上相机()
    {
        var 配方路径 = 配置路径.配方配置;
        var 原始 = await File.ReadAllTextAsync(配方路径);

        try
        {
            await using var 客户端 = new 替身Modbus客户端();
            var 服务实例 = 新服务(new 允许全部权限守卫(), 客户端);

            var 项集合 = 服务实例.收集标定页().ToList();

            // 3748 / 3750：避开点胶结构体数组（D3500–D3739）与其它点（3760 起）
            项集合.First(项 => 项.键 == "下相机X").地址文本 = "D3748";
            项集合.First(项 => 项.键 == "下相机偏移量").地址文本 = "D3750";
            项集合.First(项 => 项.键 == "下相机启动").地址文本 = "M430";

            await 服务实例.异步保存标定页(new 地址编辑请求
            {
                标题 = "标定 · 其它点",
                项集合 = 项集合,
            });

            // 从磁盘重新加载：同时验证"已落盘"（服务内部快照是另一个对象）
            var 配置项 = 配方配置加载器.加载(out _).标定;
            Assert.Equal(3748, 配置项.查找下相机("X")!.基址);
            Assert.Equal(3750, 配置项.下相机每点偏移.基址);
            Assert.Equal("M430", 配置项.下相机启动地址);

            // 上相机三项必须原封不动（现场地址：X=D1650、偏移=D1654、启动 M84）
            Assert.Equal(1650, 配置项.查找中心点("X")!.基址);
            Assert.Equal(1654, 配置项.每点偏移.基址);
            Assert.Equal("M84", 配置项.启动地址);
        }
        finally
        {
            await File.WriteAllTextAsync(配方路径, 原始);
        }
    }

    [Fact]
    public async Task 非管理员保存应被拒绝()
    {
        await using var 客户端 = new 替身Modbus客户端();
        var 服务实例 = 新服务(new 拒绝全部权限守卫(), 客户端);
        var 请求 = new 地址编辑请求
        {
            标题 = "锁附示教",
            项集合 = 服务实例.收集点表页("锁附").ToList(),
        };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => 服务实例.异步保存点表页("锁附", 请求));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => 服务实例.异步保存标定页(请求));
    }

    [Fact]
    public async Task 保存点表页应写回点表列与辅助地址()
    {
        var 配方路径 = 配置路径.配方配置;
        var 原始 = await File.ReadAllTextAsync(配方路径);

        try
        {
            await using var 客户端 = new 替身Modbus客户端();
            var 服务实例 = 新服务(new 允许全部权限守卫(), 客户端);
            var 请求 = new 地址编辑请求
            {
                标题 = "锁附示教",
                默认分组 = "锁附",
                项集合 = 服务实例.收集点表页("锁附").ToList(),
            };

            var X列 = 请求.项集合.First(项 => 项.类别 == "点表列" && 项.键 == "X");
            X列.地址文本 = "D2450";

            var 上料点Y = 请求.项集合.First(项 => 项.类别 == "辅助地址" && 项.键 == "上料点Y");
            上料点Y.地址文本 = "D2684";

            // 删除一个辅助地址
            var 待删 = 请求.项集合.First(项 => 项.类别 == "辅助地址" && 项.键 == "辅助地址11");
            请求.项集合.Remove(待删);
            请求.已删除项.Add(待删);

            await 服务实例.异步保存点表页("锁附", 请求);

            var 文本 = await File.ReadAllTextAsync(配方路径);
            Assert.Contains("2450", 文本, StringComparison.Ordinal);
            Assert.Contains("D2684", 文本, StringComparison.Ordinal);
            Assert.DoesNotContain("辅助地址11", 文本, StringComparison.Ordinal);
        }
        finally
        {
            await File.WriteAllTextAsync(配方路径, 原始);
        }
    }

    [Fact]
    public async Task 保存点表页应可新增与删除动作按钮()
    {
        var 配方路径 = 配置路径.配方配置;
        var 原始 = await File.ReadAllTextAsync(配方路径);

        try
        {
            await using var 客户端 = new 替身Modbus客户端();
            var 服务实例 = 新服务(new 允许全部权限守卫(), 客户端);
            var 请求 = new 地址编辑请求
            {
                标题 = "锁附示教",
                默认分组 = "锁附",
                项集合 = 服务实例.收集点表页("锁附").ToList(),
            };

            请求.项集合.Add(new 地址编辑项
            {
                类别 = "动作按钮",
                键 = "锁附-自定义动作",
                名称 = "自定义动作",
                地址文本 = "M499",
                数据类型文本 = "布尔",
                分组 = "锁附",
                可删除 = true,
                新增 = true,
            });

            var 待删 = 请求.项集合.First(项 => 项.类别 == "动作按钮" && 项.键 == "锁附-电批启动");
            请求.项集合.Remove(待删);
            请求.已删除项.Add(待删);

            await 服务实例.异步保存点表页("锁附", 请求);

            var 配置 = 配方配置加载器.加载(out _);
            var 新按钮 = 配置.动作按钮.FirstOrDefault(项 => 项.键 == "锁附-自定义动作");
            Assert.NotNull(新按钮);
            Assert.Equal("M499", 新按钮!.地址);
            Assert.Equal("锁附", 新按钮.分组);
            Assert.DoesNotContain(配置.动作按钮, 项 => 项.键 == "锁附-电批启动");
        }
        finally
        {
            await File.WriteAllTextAsync(配方路径, 原始);
        }
    }

    [Fact]
    public async Task 标定页应可新增动作按钮与辅助地址()
    {
        var 配方路径 = 配置路径.配方配置;
        var 原始 = await File.ReadAllTextAsync(配方路径);

        try
        {
            await using var 客户端 = new 替身Modbus客户端();
            var 服务实例 = 新服务(new 允许全部权限守卫(), 客户端);

            var 请求 = new 地址编辑请求
            {
                标题 = "标定 · 其它点",
                默认分组 = "标定",
                项集合 = 服务实例.收集标定页().ToList(),
            };

            请求.项集合.Add(new 地址编辑项
            {
                类别 = "动作按钮",
                键 = "标定-确认",
                名称 = "标定确认",
                地址文本 = "M498",
                数据类型文本 = "布尔",
                分组 = "标定",
                可删除 = true,
                新增 = true,
            });

            请求.项集合.Add(new 地址编辑项
            {
                类别 = "辅助地址",
                键 = "标定-中心偏差",
                名称 = "中心偏差",
                地址文本 = "D3748",
                数据类型文本 = "Float32",
                分组 = "标定",
                可删除 = true,
                新增 = true,
            });

            await 服务实例.异步保存标定页(请求);

            var 配置 = 配方配置加载器.加载(out _);
            Assert.Contains(配置.动作按钮, 项 => 项.键 == "标定-确认" && 项.地址 == "M498" && 项.分组 == "标定");
            Assert.Contains(配置.辅助地址, 项 => 项.键 == "标定-中心偏差" && 项.地址 == "D3748");

            // 保存后应能被重新收集（页面刷新依据）
            Assert.Contains(服务实例.收集标定页(), 项 => 项.类别 == "动作按钮" && 项.键 == "标定-确认");
        }
        finally
        {
            await File.WriteAllTextAsync(配方路径, 原始);
        }
    }

    [Fact]
    public async Task 辅助地址应可写入并读回()
    {
        await using var 设备 = new 虚拟PLC设备(NullLogger<虚拟PLC设备>.Instance);
        await using var 客户端 = new 仿真Modbus客户端(设备, NullLogger<仿真Modbus客户端>.Instance);
        await 客户端.异步连接();

        var 服务实例 = 新服务(new 允许全部权限守卫(), 客户端);
        var 项 = 服务实例.辅助地址("锁附").First(甲 => 甲.键 == "上料点Y");

        await 服务实例.异步写入辅助地址(项, 123.45);

        var 回读 = await 服务实例.异步读取辅助地址(项);
        Assert.Equal(123.45, 回读, 2);
    }

    [Fact]
    public async Task 未连接时读取辅助地址应返回零()
    {
        await using var 客户端 = new 替身Modbus客户端();
        var 服务实例 = 新服务(new 允许全部权限守卫(), 客户端);
        var 项 = 服务实例.辅助地址("锁附").First(甲 => 甲.键 == "上料点Y");

        Assert.Equal(0, await 服务实例.异步读取辅助地址(项));
    }
}
