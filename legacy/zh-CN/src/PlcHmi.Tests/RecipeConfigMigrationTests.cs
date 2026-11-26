using System.Text.Json;
using 上位机.业务.配置;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.上位机测试;

/// <summary>
/// 老现场配置的「点胶 30 点 → 50 点」结构升级（2026-11）。
///
/// <para><b>守的是什么：</b>发布包只补缺失文件、不覆盖现场 <c>config/</c>，
/// 所以升级必须由程序在启动时把现场的 <c>recipe-config.json</c> 改过来 ——
/// 但又不能乱改：点数不是 30、或列基址被现场改过时，一律不碰。</para>
///
/// <para>另外钉死"最小改动"：迁移前后除那三处值以外<b>逐字节相同</b>
/// （不重新序列化整个 JSON，避免重排字段 / 丢键）。</para>
/// </summary>
public class 配方配置迁移测试 : IDisposable
{
    private readonly string _目录 =
        Path.Combine(Path.GetTempPath(), "plchmi-迁移-" + Guid.NewGuid().ToString("N")[..8]);

    public 配方配置迁移测试() => Directory.CreateDirectory(_目录);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_目录, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响用例结论
        }
    }

    /// <summary>写一份"1.7.0 现场版"风格的配置（点胶 30 点、Y/Z 旧基址）。</summary>
    private string 写配置(int 点数 = 30, int y基址 = 2760, int z基址 = 2820)
    {
        var 配置 = new 配方配置();

        配置.点表.点表.Add(建表("锁附", 30, 2500, 2560, 2620));
        配置.点表.点表.Add(建表("点胶", 点数, 2700, y基址, z基址, 含指令码: true));

        var 路径 = Path.Combine(_目录, "recipe-config.json");
        File.WriteAllText(路径, JsonSerializer.Serialize(配置, JSON配置.写入));
        return 路径;
    }

    private static 点表定义 建表(
        string 键, int 点数, int x, int y, int z, bool 含指令码 = false)
    {
        var 表 = new 点表定义 { 键 = 键, 名称 = $"{键}示教", 点数 = 点数 };

        if (含指令码)
        {
            表.列.Add(new 点表列定义
            {
                键 = "指令码", 名称 = "指令码", 基址 = 3400,
                数据类型 = 标签数据类型.UInt16, 位置标签 = null,
            });
        }

        表.列.Add(new 点表列定义 { 键 = "X", 名称 = "X坐标", 基址 = x, 位置标签 = "AxisGlueXPosition" });
        表.列.Add(new 点表列定义 { 键 = "Y", 名称 = "Y坐标", 基址 = y, 位置标签 = "AxisYPosition" });
        表.列.Add(new 点表列定义
        {
            键 = 键 == "锁附" ? "浮高" : "Z", 名称 = "Z坐标", 基址 = z, 位置标签 = "AxisGlueZPosition",
        });

        return 表;
    }

    private static 配方配置 读(string 路径) =>
        JsonSerializer.Deserialize<配方配置>(File.ReadAllText(路径), JSON配置.读取)!;

    [Fact]
    public void 老配置应升级到50点并后移YZ基址()
    {
        var 路径 = 写配置();
        var 原文本 = File.ReadAllText(路径);

        var 说明 = 配方配置迁移.应用(路径);

        Assert.NotNull(说明);
        Assert.Contains("50 点", 说明, StringComparison.Ordinal);

        var 迁移后 = 读(路径);
        var 点胶 = 迁移后.点表.查找("点胶")!;

        Assert.Equal(50, 点胶.点数);
        Assert.Equal(2700, 点胶.查找列("X")!.基址);
        Assert.Equal(2800, 点胶.查找列("Y")!.基址);
        Assert.Equal(2900, 点胶.查找列("Z")!.基址);
        Assert.Equal(3400, 点胶.查找列("指令码")!.基址);

        // 锁附不受影响
        Assert.Equal(30, 迁移后.点表.查找("锁附")!.点数);
        Assert.Equal(2560, 迁移后.点表.查找("锁附")!.查找列("Y")!.基址);

        // 只有那三处值变了：把新值改回旧值应与原文逐字节相同
        var 迁移文本 = File.ReadAllText(路径);
        var 回退 = 迁移文本
            .Replace("\"点数\": 50", "\"点数\": 30", StringComparison.Ordinal)
            .Replace("\"基址\": 2800", "\"基址\": 2760", StringComparison.Ordinal)
            .Replace("\"基址\": 2900", "\"基址\": 2820", StringComparison.Ordinal);

        Assert.Equal(原文本, 回退);

        // 旧文件已留备份
        Assert.True(File.Exists(路径 + ".bak"), "迁移后应留下 .bak 以便回滚");
        Assert.Equal(原文本, File.ReadAllText(路径 + ".bak"));
    }

    [Fact]
    public void 迁移应幂等_再次启动不再改动()
    {
        var 路径 = 写配置();

        Assert.NotNull(配方配置迁移.应用(路径));

        var 一次后 = File.ReadAllText(路径);

        Assert.Null(配方配置迁移.应用(路径));
        Assert.Equal(一次后, File.ReadAllText(路径));
    }

    /// <summary>现场自己算过地址（基址不是出厂旧值）时，宁可不动，也不能把地址改错。</summary>
    [Fact]
    public void 基址被现场改过时不应自动迁移()
    {
        var 路径 = 写配置(y基址: 2761);
        var 原文本 = File.ReadAllText(路径);

        Assert.Null(配方配置迁移.应用(路径));
        Assert.Equal(原文本, File.ReadAllText(路径));
        Assert.Equal(30, 读(路径).点表.查找("点胶")!.点数);
    }

    [Fact]
    public void 点数不是30的现场配置不应被改动()
    {
        var 路径 = 写配置(点数: 20);
        var 原文本 = File.ReadAllText(路径);

        Assert.Null(配方配置迁移.应用(路径));
        Assert.Equal(原文本, File.ReadAllText(路径));
    }

    [Fact]
    public void 文件不存在时应安全返回()
    {
        Assert.Null(配方配置迁移.应用(Path.Combine(_目录, "不存在.json")));
    }
}
