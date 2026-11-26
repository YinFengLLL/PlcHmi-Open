using Microsoft.Extensions.Logging.Abstractions;
using 上位机.业务.服务;
using 上位机.通信.仿真;
using 上位机.核心.枚举;

namespace 上位机.上位机测试;

/// <summary>
/// 设备状态字映射与模拟器软元件布局的回归测试。
///
/// <para>这两处曾经出过真实缺陷：状态字编号与 C# 枚举语义不一致导致“待机”显示成“未知”；
/// 模拟器把 X 点写进离散输入区，而轮询引擎按线圈区读取，导致常闭点状态错乱。</para>
/// </summary>
public class 设备状态仿真测试
{
    private const int X输入基址 = 63488;   // X0（离散输入区 FC02）
    private const int 寄存器D6运行状态 = 6;  // D6 设备状态字

    [Theory]
    [InlineData(0, 设备状态.空闲)]
    [InlineData(1, 设备状态.运行中)]
    [InlineData(2, 设备状态.待机中)]
    [InlineData(3, 设备状态.复位中)]
    [InlineData(4, 设备状态.报警中)]
    [InlineData(5, 设备状态.未知)]
    [InlineData(99, 设备状态.未知)]
    public void 状态字映射_应按PLC约定翻译为设备状态(int 原始值, 设备状态 期望) =>
        Assert.Equal(期望, 设备服务.映射状态字(原始值));

    /// <summary>
    /// 主监控页 5 态显示文案回归（2026-10-16 现场要求：空闲中/运行中/待机中/复位中/报警中）。
    /// 界面文案是操作员要核对的行为，改错了必须在这里红。
    /// </summary>
    [Theory]
    [InlineData(设备状态.空闲, "空闲中")]
    [InlineData(设备状态.运行中, "运行中")]
    [InlineData(设备状态.待机中, "待机中")]
    [InlineData(设备状态.复位中, "复位中")]
    [InlineData(设备状态.报警中, "报警中")]
    [InlineData(设备状态.未知, "未知")]
    public void 状态显示_五种状态应有明确文案且互不重复(设备状态 状态值, string 期望文本)
    {
        var (文本, 颜色键) = 设备服务.状态显示(状态值);

        Assert.Equal(期望文本, 文本);
        Assert.False(string.IsNullOrWhiteSpace(颜色键));
    }

    [Fact]
    public void 状态显示_五种有效状态文案必须两两不同()
    {
        // 图标转换器按文本选图标：两个状态共用一句文案，其中一种就会显示错误的图标
        var 文案集合 = new[]
        {
            设备状态.空闲, 设备状态.运行中, 设备状态.待机中, 设备状态.复位中, 设备状态.报警中,
        }.Select(状态项 => 设备服务.状态显示(状态项).文本).ToList();

        Assert.Equal(文案集合.Count, 文案集合.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void 状态字映射_零值应为空闲中而非未知()
    {
        // PLC 状态字 0 表示空闲中；枚举里的 未知=0 只是“未拿到有效数据”的哨兵，
        // 二者语义不同，必须解耦——这条断言就是防止有人再改回直接强转。
        Assert.Equal(设备状态.空闲, 设备服务.映射状态字(0));
        Assert.NotEqual(设备状态.未知, 设备服务.映射状态字(0));
    }

    [Fact]
    public async Task 模拟器_初始状态字应为空闲中()
    {
        await using var 设备 = new 虚拟PLC设备(NullLogger<虚拟PLC设备>.Instance);

        Assert.Equal(0, 设备.内存.不安全获取保持寄存器(寄存器D6运行状态));
        Assert.Equal(设备状态.空闲, 设备服务.映射状态字(
            设备.内存.不安全获取保持寄存器(寄存器D6运行状态)));
    }

    [Fact]
    public async Task 模拟器_X点必须写在离散输入区()
    {
        await using var 设备 = new 虚拟PLC设备(NullLogger<虚拟PLC设备>.Instance);
        var 内存 = 设备.内存;

        // X 是物理输入 → 离散输入区（FC02）；Y 是输出 → 线圈区（FC01）。
        // 混区会让模拟器自测通过、真机全错（2026-10-17 现场第二次报障的真因）。
        var 块 = 内存.读取离散输入(X输入基址, 24);

        Assert.True(块[2], "X2 安全门为常闭触点，初始应闭合（原始位 1）");
        Assert.True(块[7], "X7 急停按钮未按下时应闭合（原始位 1）");

        // 线圈区同一地址段必须保持全 0：证明 X 没有同时写进线圈区（功能码分工是唯一的）
        var 线圈块 = 内存.读取线圈(X输入基址, 24);
        Assert.DoesNotContain(true, 线圈块);
    }

    [Fact]
    public async Task 模拟器_常闭急停点应与急停状态反相()
    {
        await using var 设备 = new 虚拟PLC设备(NullLogger<虚拟PLC设备>.Instance);
        设备.启动();

        try
        {
            // 等模拟循环跑起来，让 Tick 写入一遍
            await Task.Delay(400);

            // 未急停：常闭触点闭合 → 原始位 1；经 io-points.json 的 inverted=true 后，
            // 逻辑值为 0（界面显示 OFF），表示“急停未触发”
            Assert.True(设备.内存.不安全获取离散输入(X输入基址 + 7));

            设备.内存.运行独占(() => 设备.内存.不安全设置线圈(204, true)); // M204 急停信号（电平保持）
            await Task.Delay(400);

            // 急停后：触点断开 → 原始位 0 → 逻辑值 1（界面显示 ON）
            Assert.False(设备.内存.不安全获取离散输入(X输入基址 + 7));
            Assert.Equal(4, 设备.内存.不安全获取保持寄存器(寄存器D6运行状态));
        }
        finally
        {
            await 设备.异步停止();
        }
    }
}
