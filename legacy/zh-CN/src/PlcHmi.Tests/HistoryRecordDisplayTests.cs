using Microsoft.Extensions.Logging.Abstractions;
using 上位机.业务.配置;
using 上位机.业务.服务;
using 上位机.通信.汇川;
using 上位机.核心.枚举;
using 上位机.核心.模型;
using 上位机.上位机测试.替身;
namespace 上位机.上位机测试;

/// <summary>
/// 历史记录的<b>显示增强</b>回归：DINT 按 ASCII 显示、列颜色（红 / 黄 / 绿，可选）。
///
/// <para>与 <see cref="历史记录测试"/>（数值缩放 / CSV / 校验）分工：那是"采到什么"，
/// 这里是"采到了怎么显示"。从后者拆出——它已顶到项目规模门禁。</para>
///
/// <para>两条服务层校验用例要读 <c>config/history-record.json</c>，与其它动配置文件的用例
/// 同属 <c>config-files</c> 集合，禁用并行。</para>
/// </summary>
[Collection("config-files")]
public class 历史记录显示测试
{
    /// <summary>
    /// 按字序把 4 个字符打包成 32 位值（与实现的拆解互为逆运算）。
    /// <para><b>字内低字节在前</b>（2026-10-17 现场确认）：首字符落在第一个字的低字节——
    /// 现场读到的字是 <c>19279</c>（0x4B4F）时应显示 <c>OK</c>，即 'O'(0x4F) 在低字节、
    /// 'K'(0x4B) 在高字节。两个字之间的先后才由字序决定。</para>
    /// </summary>
    private static int 打包字符(char 甲, char 乙, char 丙, char 丁, 字顺序 字序)
    {
        var 首字 = 甲 | (乙 << 8);
        var 次字 = 丙 | (丁 << 8);

        return 字序 is 字顺序.大字节序
            ? (首字 << 16) | 次字   // 高字在前
            : 首字 | (次字 << 16);  // 低字在前（CDAB，汇川默认）
    }

    // ── ASCII 显示（2026-10-17 现场要求：DINT 可选按字符显示）──────────

    /// <summary>
    /// 现场实例回归：读到的字是 <c>19279</c>（0x4B4F）时必须显示 <c>OK</c>。
    /// <para>这条用例钉死"<b>字内低字节在前</b>"的约定——曾经按"寄存器大端"实现，
    /// 同样的字会显示成 <c>KO</c>，现场直接报障。</para>
    /// </summary>
    [Fact]
    public void ASCII显示_19279应显示为OK()
    {
        var 项 = new 历史数据项
        {
            名称 = "结果文本", 地址 = "D2130", 数据类型 = 标签数据类型.Int32, 显示为Ascii = true,
        };

        // 高 16 位为 0 → 后两个字符是不可打印字节，占位为点
        Assert.Equal("OK..", 项.显示文本(19279, 字顺序.小字节序));
    }

    /// <summary>
    /// DINT 按 ASCII 显示：4 个字符打包进一个 32 位寄存器。
    /// <para><b>两个字的先后必须按配置的字序</b>：CDAB 与大字节序下"哪两个字谁在前"相反，
    /// 写死一种顺序会让另一种显示成乱序（红线 R1：字序必须可配）。</para>
    /// </summary>
    [Fact]
    public void DINT按ASCII显示_应按配置字序拆字节()
    {
        var 项 = new 历史数据项
        {
            名称 = "批次号", 地址 = "D2130", 数据类型 = 标签数据类型.Int32, 显示为Ascii = true,
        };

        Assert.Equal("AB12", 项.显示文本(打包字符('A', 'B', '1', '2', 字顺序.小字节序), 字顺序.小字节序));
        Assert.Equal("AB12", 项.显示文本(打包字符('A', 'B', '1', '2', 字顺序.大字节序), 字顺序.大字节序));

        // 用错字序会乱序——这条断言本身就是"字序必须可配"的理由
        Assert.Equal("12AB", 项.显示文本(打包字符('A', 'B', '1', '2', 字顺序.小字节序), 字顺序.大字节序));

        // 不传字序时按汇川默认（小字节序），老调用点行为不变
        Assert.Equal("AB12", 项.显示文本(打包字符('A', 'B', '1', '2', 字顺序.小字节序)));
    }

    /// <summary>不可打印字节用 <c>.</c> 占位：控制字符画进表格会把行高撑坏。</summary>
    [Fact]
    public void DINT按ASCII显示_不可打印字节应占位为点()
    {
        var 项 = new 历史数据项
        {
            名称 = "产品码", 地址 = "D2132", 数据类型 = 标签数据类型.Int32, 显示为Ascii = true,
        };

        Assert.Equal("....", 项.显示文本(
            打包字符('\0', '\x1F', '\x7F', '\x80', 字顺序.小字节序), 字顺序.小字节序));

        Assert.Equal("K7.-", 项.显示文本(
            打包字符('K', '7', '\0', '-', 字顺序.小字节序), 字顺序.小字节序));
    }

    /// <summary>ASCII 显示优先于小数位（字符没有小数点），且只对 DINT 生效。</summary>
    [Fact]
    public void ASCII显示应优先于小数位且仅DINT生效()
    {
        var DINT项 = new 历史数据项
        {
            名称 = "批次号", 地址 = "D2130", 数据类型 = 标签数据类型.Int32,
            小数位 = 2, 显示为Ascii = true,
        };

        Assert.Equal("AB12", DINT项.显示文本(打包字符('A', 'B', '1', '2', 字顺序.小字节序)));

        // INT 只有 2 字节，凑不出 4 个字符：即便配置里勾了也按数值显示（界面已禁用勾选）
        var INT项 = new 历史数据项
        {
            名称 = "工位号", 地址 = "D2100", 数据类型 = 标签数据类型.Int16,
            小数位 = 0, 显示为Ascii = true,
        };

        Assert.Equal("12", INT项.显示文本(12));
        Assert.False(INT项.可设Ascii);
    }

    /// <summary>
    /// 2026-10-24 现场要求：新支持 REAL（32 位浮点）。
    /// <para>REAL 的原始值<b>已经是工程量</b>，小数位只决定显示保留几位、不做 ÷10^位的缩放——
    /// 照整数那样除会把 12.35 显示成 0.12，属于"能存但显示错"，比报错难查得多。</para>
    /// </summary>
    [Fact]
    public void REAL应按工程量显示且小数位只控制保留位数()
    {
        var 两位 = new 历史数据项
        {
            名称 = "胶量", 地址 = "D1420", 数据类型 = 标签数据类型.Float32, 小数位 = 2, 单元 = "mg",
        };

        Assert.Equal("12.35", 两位.显示文本(12.3456));
        Assert.Equal("REAL", 两位.类型文本);
        Assert.False(两位.可设Ascii);   // 浮点的 4 个字节拆成字符全是乱码

        var 零位 = 两位.克隆();
        零位.小数位 = 0;
        Assert.Equal("12", 零位.显示文本(12.3456));   // 四舍五入到整数

        // CSV 导出走同一套显示规则
        var 配置 = new 历史记录配置
        {
            标题 = "浮点记录", 触发地址 = "M8", 数据项集合 = [两位],
        };

        var 文本 = 历史记录CSV编解码器.编码(
            配置,
            [new 历史记录条目 { 时刻 = new DateTime(2026, 10, 24, 9, 0, 0), 数值集合 = [12.3456] }]);

        Assert.Contains("12.35", 文本, StringComparison.Ordinal);
    }

    /// <summary>CSV 导出与界面显示一致：ASCII 列导出的是字符，不是打包后的整数。</summary>
    [Fact]
    public void CSV导出ASCII列应写出字符而非整数()
    {
        var 配置 = new 历史记录配置
        {
            标题 = "锁螺丝记录",
            触发地址 = "M700",
            数据项集合 =
            [
                new() { 名称 = "批次号", 地址 = "D2130", 数据类型 = 标签数据类型.Int32, 显示为Ascii = true },
                new() { 名称 = "实际扭力", 地址 = "D2109", 数据类型 = 标签数据类型.Int32, 小数位 = 2 },
            ],
        };

        var 条目 = new 历史记录条目
        {
            时刻 = new DateTime(2026, 10, 17, 8, 30, 0),
            数值集合 = [打包字符('A', 'B', '1', '2', 字顺序.小字节序), 600],
        };

        var 文本 = 历史记录CSV编解码器.编码(配置, [条目]);

        Assert.Contains("AB12", 文本, StringComparison.Ordinal);
        Assert.Contains("6.00", 文本, StringComparison.Ordinal);
    }

    // ── 列颜色（红 / 黄 / 绿是可选项，不选则默认色）────────────────────

    /// <summary>
    /// 颜色是<b>可选项</b>：不选（默认）时数据列用正文色、表头用次要色——
    /// 与加这个功能之前的观感完全一致，老配置升级后零变化。
    /// </summary>
    [Fact]
    public void 列颜色_不选时应回落默认颜色()
    {
        var 项 = new 历史数据项 { 名称 = "结果", 地址 = "D2121" };

        Assert.Equal(历史列颜色.无, 项.颜色);           // 默认即"不选"
        Assert.Equal("TextPrimaryBrush", 项.颜色键);
        Assert.Equal("TextSecondaryBrush", 项.表头颜色键);
    }

    /// <summary>选了红 / 黄 / 绿时，数据列与表头用同一个语义色。</summary>
    [Theory]
    [InlineData(历史列颜色.红, "DangerBrush")]
    [InlineData(历史列颜色.黄, "WarningBrush")]
    [InlineData(历史列颜色.绿, "SuccessBrush")]
    public void 列颜色_三种可选色应映射到主题语义色(历史列颜色 颜色, string 期望键)
    {
        var 项 = new 历史数据项 { 名称 = "结果", 地址 = "D2121", 颜色 = 颜色 };

        Assert.Equal(期望键, 项.颜色键);
        Assert.Equal(期望键, 项.表头颜色键);
    }

    /// <summary>克隆必须带上新增字段：编辑对话框取消时不丢，保存时也不会静默还原。</summary>
    [Fact]
    public void 克隆应保留ASCII与颜色设置()
    {
        var 项 = new 历史数据项
        {
            名称 = "批次号", 地址 = "D2130", 数据类型 = 标签数据类型.Int32,
            显示为Ascii = true, 颜色 = 历史列颜色.黄,
        };

        var 副本 = 项.克隆();

        Assert.True(副本.显示为Ascii);
        Assert.Equal(历史列颜色.黄, 副本.颜色);
    }

    // ── 服务层校验：ASCII 只支持 DINT（界面禁用之外的第二道边界）──────

    /// <summary>
    /// ASCII 显示只支持 DINT：INT 只有 2 字节，勾了也显示不对。
    /// <para>界面已禁用非 DINT 的勾选，但配置还有"手改 JSON"这个入口——
    /// 规则必须两边一致，否则现场手改的文件会安静地显示错值（R5 同源规则）。</para>
    /// </summary>
    [Fact]
    public async Task INT项勾选ASCII应被拒绝()
    {
        await using var 客户端 = new 替身Modbus客户端();
        var 服务实例 = 新服务(客户端);

        var 配置 = new 历史记录配置
        {
            标题 = "非法配置",
            触发地址 = "M700",
            数据项集合 =
            [
                new() { 名称 = "工位号", 地址 = "D2100", 数据类型 = 标签数据类型.Int16, 显示为Ascii = true },
            ],
        };

        var 校验项 = 服务实例.校验(配置);

        Assert.False(校验项.是否有效);
        Assert.Contains("ASCII 显示只支持 DINT", 校验项.摘要);
    }

    /// <summary>DINT 勾 ASCII、INT 选颜色都是合法组合，不应被误伤。</summary>
    [Fact]
    public async Task DINT项勾选ASCII且设颜色应通过校验()
    {
        await using var 客户端 = new 替身Modbus客户端();
        var 服务实例 = 新服务(客户端);

        var 配置 = new 历史记录配置
        {
            标题 = "合法配置",
            触发地址 = "M700",
            数据项集合 =
            [
                new() { 名称 = "批次号", 地址 = "D2130", 数据类型 = 标签数据类型.Int32, 显示为Ascii = true },
                new() { 名称 = "结果", 地址 = "D2132", 数据类型 = 标签数据类型.Int16, 颜色 = 历史列颜色.红 },
            ],
        };

        var 校验项 = 服务实例.校验(配置);

        Assert.True(校验项.是否有效, 校验项.摘要);
    }

    /// <summary>构造被测服务（替身客户端 + 放行权限的守卫 + 内存存储）。</summary>
    private static 历史记录服务 新服务(替身Modbus客户端 客户端) => new(
        new 汇川地址解析器(PLC系列.小型PLC),
        客户端,
        new 允许全部权限守卫(),
        new 历史记录存储(NullLogger<历史记录存储>.Instance),
        NullLogger<历史记录服务>.Instance);
}
