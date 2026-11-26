using 上位机.业务.配置;
using 上位机.通信.配方;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.上位机测试;

/// <summary>
/// 通用点表地址计算的测试。
///
/// <para>锁附：X 2500、Y 2560、浮高 2620；点胶：指令码 3400、X 2700、Y 2800、Z 2900
/// （2026-11-18 起点胶扩到 <b>50 点</b>：每列 100 字，Y/Z 基址随之后移，以现场 recipe-config.json 为准）。
/// 每点 Float32 占 2 字（指令码 UInt16 占 1 字）。偏移算错一个数，现场表现是
/// "轴跑到错误位置"或"指令码错位"，因此这些断言必须逐条钉死。</para>
/// </summary>
public class 点表地址映射测试
{
    private static 点表定义 锁附表() => new()
    {
        键 = "锁附",
        名称 = "锁附示教",
        点数 = 30,
        列 =
        [
            new 点表列定义 { 键 = "X", 基址 = 2500 },
            new 点表列定义 { 键 = "Y", 基址 = 2560 },
            new 点表列定义 { 键 = "浮高", 基址 = 2620 },
        ],
    };

    private static 点表定义 点胶表() => new()
    {
        键 = "点胶",
        名称 = "点胶示教",
        点数 = 50,
        列 =
        [
            new 点表列定义 { 键 = "指令码", 基址 = 3400, 数据类型 = 标签数据类型.UInt16 },
            new 点表列定义 { 键 = "X", 基址 = 2700 },
            new 点表列定义 { 键 = "Y", 基址 = 2800 },
            new 点表列定义 { 键 = "Z", 基址 = 2900 },
        ],
    };

    [Theory]
    [InlineData(0, "X", 2500)]
    [InlineData(1, "X", 2502)]
    [InlineData(29, "X", 2558)]
    [InlineData(0, "Y", 2560)]
    [InlineData(1, "Y", 2562)]
    [InlineData(29, "Y", 2618)]
    [InlineData(0, "浮高", 2620)]
    [InlineData(1, "浮高", 2622)]
    public void 锁附坐标地址应符合与PLC约定的地址表(int 序号, string 列键, int 期望) =>
        Assert.Equal(期望, new 点表地址映射(锁附表()).地址(序号, 列键));

    [Theory]
    [InlineData(0, "指令码", 3400)]
    [InlineData(1, "指令码", 3401)]
    [InlineData(29, "指令码", 3429)]
    [InlineData(49, "指令码", 3449)]
    [InlineData(0, "X", 2700)]
    [InlineData(1, "X", 2702)]
    [InlineData(49, "X", 2798)]
    [InlineData(1, "Y", 2802)]
    [InlineData(49, "Y", 2898)]
    [InlineData(1, "Z", 2902)]
    [InlineData(49, "Z", 2998)]
    public void 点胶地址应符合与PLC约定的地址表(int 序号, string 列键, int 期望) =>
        Assert.Equal(期望, new 点表地址映射(点胶表()).地址(序号, 列键));

    [Fact]
    public void 每点步长应与数据类型一致()
    {
        var 锁附 = new 点表地址映射(锁附表());
        Assert.Equal(2, 锁附.字每点位("X"));
        Assert.Equal(60, 锁附.列块字("X"));       // 30 点 × 2 字
        Assert.Equal(2502 - 2500, 锁附.地址(1, "X") - 锁附.地址(0, "X"));

        var 点胶 = new 点表地址映射(点胶表());
        Assert.Equal(1, 点胶.字每点位("指令码"));   // 指令码单字
        Assert.Equal(50, 点胶.列块字("指令码"));    // 50 点 × 1 字
        Assert.Equal(2, 点胶.字每点位("X"));
        Assert.Equal(100, 点胶.列块字("X"));        // 50 点 × 2 字
    }

    [Fact]
    public void 越界序号与未知列应抛异常()
    {
        var 映射 = new 点表地址映射(锁附表());

        Assert.Throws<ArgumentOutOfRangeException>(() => 映射.地址(-1, "X"));
        Assert.Throws<ArgumentOutOfRangeException>(() => 映射.地址(30, "X"));
        Assert.Throws<ArgumentOutOfRangeException>(() => 映射.地址(0, "不存在"));
        Assert.False(映射.序号合法(30));
    }

    [Fact]
    public void 点数或列为零时应拒绝构造()
    {
        Assert.Throws<ArgumentException>(() => new 点表地址映射(new 点表定义 { 点数 = 0 }));
        Assert.Throws<ArgumentException>(
            () => new 点表地址映射(new 点表定义 { 点数 = 10 }));
    }

    [Fact]
    public void 真实配置的列基址应与需求一致()
    {
        // 守住"改配置改错列基址"这类问题：直接读现场配置文件
        var 配置 = 配方配置加载器.加载(out var 错误);
        Assert.Null(错误);

        var 锁附 = 配置.点表.查找("锁附");
        Assert.NotNull(锁附);
        Assert.Equal(30, 锁附!.点数);
        Assert.Equal(2500, 锁附.查找列("X")!.基址);
        Assert.Equal(2560, 锁附.查找列("Y")!.基址);

        var 点胶 = 配置.点表.查找("点胶");
        Assert.NotNull(点胶);
        Assert.Equal(50, 点胶!.点数);
        Assert.Equal(3400, 点胶.查找列("指令码")!.基址);
        Assert.Equal(2700, 点胶.查找列("X")!.基址);
        Assert.Equal(2800, 点胶.查找列("Y")!.基址);
        Assert.Equal(2900, 点胶.查找列("Z")!.基址);
    }

    /// <summary>
    /// 配置自检：动作按钮 / 回零 / 点动的线圈地址不得互相重叠。
    ///
    /// <para><b>为什么必须有这条</b>：取消地址范围限制后，页面上的元件地址由管理员自行维护，
    /// 没有第二道闸门拦住"两个功能写同一个 M 点"。2026-10-12 审计就真的发现了
    /// 「锁附-电批启动 M480」与「点胶 Z 轴点动正转 M480」撞车——按下点动会把电批点着。
    /// 这类错误在界面上完全看不出来，只能靠配置自检钉住。</para>
    /// </summary>
    [Fact]
    public void 真实配置的动作按钮与点动回零线圈不得重叠()
    {
        var 配置 = 配方配置加载器.加载(out _);
        var 占用 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void 登记(string 地址值, string 用途)
        {
            if (string.IsNullOrWhiteSpace(地址值))
            {
                return;
            }

            Assert.False(占用.TryGetValue(地址值, out var 已有),
                $"地址 {地址值} 被重复占用：{已有} 与 {用途}");

            占用[地址值] = 用途;
        }

        foreach (var 项 in 配置.动作按钮)
        {
            登记(项.地址, $"动作按钮「{项.显示名称}」");
        }

        foreach (var 项 in 配置.回零)
        {
            登记(项.地址, $"回零「{项.显示名称}」");
        }

        // 点胶 Y 与锁附 Y 是同一根轴（共轴），配置里会出现两行同样的线圈地址——
        // 按轴去重后再比对，避免把"共轴"误报成冲突。
        foreach (var 项 in 配置.点动
                     .GroupBy(项 => 项.轴, StringComparer.OrdinalIgnoreCase)
                     .Select(组 => 组.First()))
        {
            登记(项.正转地址, $"点动「{项.显示名称}」正转");
            登记(项.反转地址, $"点动「{项.显示名称}」反转");
        }
    }

    [Fact]
    public void 真实配置的保存坐标列应只覆盖对应轴()
    {
        // 「保存坐标点」写哪些列是现场语义：锁附写 X/Y + 当前浮高（2026-10-24 起浮高由测高地址给出），
        // 点胶写 X/Y/Z
        var 配置 = 配方配置加载器.加载(out _);

        var 锁附 = 配置.点表.查找("锁附")!;
        Assert.True(锁附.是否参与保存坐标("X"));
        Assert.True(锁附.是否参与保存坐标("Y"));
        Assert.True(锁附.是否参与保存坐标("浮高"));
        Assert.Equal(240, 锁附.当前浮高地址);

        var 点胶 = 配置.点表.查找("点胶")!;
        Assert.True(点胶.是否参与保存坐标("X"));
        Assert.True(点胶.是否参与保存坐标("Y"));
        Assert.True(点胶.是否参与保存坐标("Z"));
        Assert.False(点胶.是否参与保存坐标("指令码"));
    }

    [Fact]
    public void 轴速度参数应为六轴且单位统一为毫米每秒()
    {
        // 参数页「轴速度」表的行来自 appsettings 的轴列表：六轴、单位一律 mm/s
        var 设置 = 应用设置加载器.加载(out _);

        Assert.Equal(6, 设置.轴列表.Count);
        Assert.Equal(
            new[] { "X", "Y", "Z", "ZZ", "GlueX", "GlueZ" },
            设置.轴列表.Select(条目项 => 条目项.轴).ToArray());
        Assert.All(设置.轴列表, 条目项 => Assert.Equal("mm/s", 条目项.单元));
    }
}
