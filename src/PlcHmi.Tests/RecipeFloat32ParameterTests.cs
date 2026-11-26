using 上位机.业务.配置;
using 上位机.通信.Modbus;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.上位机测试;

/// <summary>
/// 轴参数区的契约测试：地址排布、数据类型、允许区间。
///
/// <para><b>守的是什么（2026-09-12 现场实测 + 2026-09-17 按 PLC 地址分配表迁移）</b>：
/// 参数项原先一律按 <c>UInt16</c> 编码、只写 1 个寄存器，而现场约定是 Float32（占 2 字）；
/// 后一项的低字会踩掉前一项的高字——PLC 读到的是错位的值。
/// 现地址按分配表逐轴落位（轴 1–轴 6 各 100 字，偏移 2），这一组把新契约钉死。</para>
/// </summary>
public class 配方Float32参数测试
{
    private static 配方配置 配置() => 配方配置加载器.加载(out _);

    [Fact]
    public void 参数应为六项定位速度且各落其轴区()
    {
        var 配置对象 = 配置();

        // 2026-10-12：点胶锁付机为六轴（锁附 X/Y/Z/ZZ + 点胶 X/Z）
        Assert.Equal(6, 配置对象.参数.Count);
        Assert.Equal(
            new[]
            {
                "X.LocateSpeed", "Y.LocateSpeed", "Z.LocateSpeed",
                "ZZ.LocateSpeed", "GlueX.LocateSpeed", "GlueZ.LocateSpeed",
            },
            配置对象.参数.Select(点位项 => 点位项.键).ToArray());

        // 2026-10-21 现场配方：六轴速度集中在 D1400 起（每项 2 字）
        Assert.Equal(
            new[] { 1400, 1402, 1404, 1406, 1410, 1408 },
            配置对象.参数.Select(点位项 => 点位项.地址).ToArray());

        // 六轴速度集中在 D1400 起，每项 Float32 占 2 字（地址必为偶数）
        Assert.All(配置对象.参数, 点位项 => Assert.Equal(0, 点位项.地址 % 2));
    }

    [Fact]
    public void 参数应声明为Float32且各占两个字()
    {
        foreach (var 规格项 in 配置().参数)
        {
            Assert.Equal(标签数据类型.Float32, 规格项.数据类型);
            Assert.Equal(2, 规格项.寄存器数量);
        }
    }

    [Fact]
    public void 参数地址不应重叠()
    {
        var 范围 = 配置().参数
            .Select(点位项 => (点位项.键, 启动: 点位项.地址, End: 点位项.地址结束边界))
            .ToList();

        for (var 条目 = 0; 条目 < 范围.Count; 条目++)
        {
            for (var 内层序号 = 条目 + 1; 内层序号 < 范围.Count; 内层序号++)
            {
                Assert.False(
                    范围[条目].启动 < 范围[内层序号].End && 范围[内层序号].启动 < 范围[条目].End,
                    $"{范围[条目].键} 与 {范围[内层序号].键} 地址重叠");
            }
        }
    }

    [Theory]
    [InlineData(1200, true)]
    [InlineData(1500, true)]
    [InlineData(1799, true)]
    [InlineData(1199, false)]     // 2026-09-17 起轴参数区从 D1200 开始
    [InlineData(1800, false)]     // 轴 6 结束于 D1799，D1800 已出区间
    public void 轴参数允许区间应为D1200到D1799(int 地址值, bool 期望)
    {
        Assert.Equal(期望, 配置().参数地址范围.包含(地址值));
    }

    [Fact]
    public void 默认定位速度的CDAB字序应符合实测向量()
    {
        foreach (var 规格项 in 配置().参数)
        {
            var 寄存器列表 = 寄存器编解码器.编码(规格项.数据类型, 规格项.默认, 字顺序.小字节序);

            Assert.Equal(2, 寄存器列表.Length);

            // 四个默认值都是整数，CDAB 下低字必为 0、高字承载全部信息
            Assert.Equal((ushort)0x0000, 寄存器列表[0]);

            var 已解码 = (float)寄存器编解码器.解码(标签数据类型.Float32, 寄存器列表, 字顺序.小字节序);
            Assert.Equal(规格项.默认, 已解码, 3);
        }
    }

    [Fact]
    public void 点动速度档位应覆盖六轴()
    {
        var 配置对象 = 配置();

        Assert.Equal(6, 配置对象.点动速度等级.Count);
        Assert.Equal(
            new[] { "X", "Y", "Z", "ZZ", "GlueX", "GlueZ" },
            配置对象.点动速度等级.Select(档位 => 档位.轴).ToArray());

        // 六轴共用同一个速度档位地址（切档一次，六轴同时生效）；现场版地址为 D15
        Assert.All(
            配置对象.点动速度等级,
            档位 => Assert.Equal(15, 档位.地址));
    }

    [Theory]
    // 2026-10-13 现场口径：档位写入值 低=0、中=1、高=2
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(-1, false)]
    public void 档位值只接受零到二(int 等级, bool 期望) =>
        Assert.Equal(期望, 点动速度等级地址.是否有效等级(等级));

    [Fact]
    public void 配置里不应再出现已废弃的手动速度参数()
    {
        Assert.DoesNotContain(
            配置().参数,
            点位项 => 点位项.键.Contains("ManualSpeed", StringComparison.OrdinalIgnoreCase));
    }
}
