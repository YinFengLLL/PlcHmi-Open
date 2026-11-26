using System.Globalization;
using System.Text.RegularExpressions;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.异常;
using 上位机.核心.模型;

namespace 上位机.通信.汇川;

/// <summary>
/// 汇川软元件地址解析器。
///
/// <para><b>为什么需要它：</b>汇川 PLC 的 Modbus 地址不是标准 4xxxx 顺序编号，
/// 而是“功能区起始地址 + 软元件序号”。例如 X0 对应 63488 而不是 0。
/// 搞错这一层，所有数据都会错位。</para>
///
/// <para><b>最易踩的坑：</b>X / Y 的序号是<b>八进制</b>——<c>X10</c> 表示第 8 个点，
/// 换算后 Modbus 地址是 63496，不是 63498。</para>
///
/// <para><b>机型差异：</b>小型 PLC（EASY320 / EASY52X / H5U / H3U）使用 M/B/S/X/Y/D/R 体系；
/// 中型 PLC（AM600 / AC800）使用 QX/IX/MW 体系，且 SM/SD 需专用功能码。</para>
/// </summary>
public sealed class 汇川地址解析器 : 地址解析器接口
{
    // 基址表与地址前缀对照表已抽到 汇川软元件规格表（数据与算法分离）。
    // 注意：SM 必须排在 M 之前，SD 必须排在 D 之前，否则会被短前缀抢先匹配
    private static readonly Regex 小型PLC模式 =
        new(@"^(?<elem>SM|SD|X|Y|M|B|S|D|R|T|C)(?<index>\d+)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 小型 PLC 的 D/R 区「字内位」：D1210.2 = D 字第 2 位（0–15）。
    // 汇川位打包约定（PLC 地址分配表）：BOOL 占 D 的一位，可连续；
    // Modbus 无「字内位」功能码，读写须按整字处理（见 TagWriter 的读-改-写）。
    private static readonly Regex 小型字位模式 =
        new(@"^(?<elem>D|R)(?<word>\d+)\.(?<bit>1[0-5]|[0-9])$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex 中等位模式 =
        new(@"^(?<elem>QX|IX)(?<byte>\d+)\.(?<bit>[0-7])$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex 中等字模式 =
        new(@"^(?<elem>MW|SM|SD)(?<index>\d+)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Dictionary<软元素, 软元素规格> _规格;

    public 汇川地址解析器(PLC系列 系列值)
    {
        系列 = 系列值;
        _规格 = 汇川软元件规格表.按机型(系列值);
    }

    /// <inheritdoc />
    public PLC系列 系列 { get; }

    /// <inheritdoc />
    public PLC地址 解析(string 软元素)
    {
        if (string.IsNullOrWhiteSpace(软元素))
        {
            throw 地址解析异常.输入(软元素 ?? "(null)", "地址为空");
        }

        var 输入项 = 软元素.Trim();

        return 系列 == PLC系列.小型PLC
            ? 解析小型PLC(输入项)
            : 解析中等PLC(输入项);
    }

    /// <inheritdoc />
    public bool 尝试解析(string 软元素, out PLC地址? 地址值)
    {
        try
        {
            地址值 = 解析(软元素);
            return true;
        }
        catch (地址解析异常)
        {
            地址值 = null;
            return false;
        }
    }

    /// <inheritdoc />
    public int 获取寄存器数量(标签数据类型 数据类型) => 数据类型 switch
    {
        标签数据类型.Int32 or 标签数据类型.UInt32 or 标签数据类型.Float32 => 2,
        标签数据类型.Float64 => 4,
        _ => 1,
    };

    /// <summary>
    /// 把 Modbus 地址<b>反解</b>回软元件符号。OPC UA 模式专用：
    /// 客户端从业务层只拿到“区域 + Modbus 地址”，必须先还原软元件符号才能推导 NodeId。
    ///
    /// <para>反解规则与 <see cref="解析"/> 的基址表严格互逆。同一区域内区间重叠时
    /// （中型 PLC 的 Q 覆盖 SM / SD 覆盖 R），取序号上限更小、更具体的那一个。</para>
    /// </summary>
    /// <param name="区域值">Modbus 数据区（由调用方接口方法隐含给出）。</param>
    /// <param name="Modbus地址值">换算后的 Modbus 地址。</param>
    /// <exception cref="地址解析异常">地址不属于当前机型任何软元件区间时抛出。</exception>
    public PLC地址 反解(Modbus区域 区域值, int Modbus地址值)
    {
        if (Modbus地址值 is < 0 or > ushort.MaxValue)
        {
            throw 地址解析异常.输入(Modbus地址值.ToString(CultureInfo.InvariantCulture),
                "地址超出 Modbus 地址空间范围 0-65535");
        }

        var 候选列表 = _规格.Values
            .Where(规格项 => 规格项.区域 == 区域值)
            .Where(规格项 => Modbus地址值 >= 规格项.基址地址 &&
                             Modbus地址值 - 规格项.基址地址 <= 规格项.最大序号)
            .OrderBy(规格项 => 规格项.最大序号)          // 范围小者优先——更具体的软元件
            .ThenBy(规格项 => 规格项.基址地址)
            .ToList();

        if (候选列表.Count == 0)
        {
            throw 地址解析异常.输入(
                $"{区域值}@{Modbus地址值}",
                $"该 Modbus 地址不属于当前机型的任何软元件区间，无法推导 OPC UA NodeId");
        }

        var 规格项 = 候选列表[0];
        var 序号 = Modbus地址值 - 规格项.基址地址;
        var 符号文本 = 生成软元件文本(规格项.元素, 序号);

        return new PLC地址
        {
            元素 = 规格项.元素,
            逻辑序号 = 序号,
            Modbus地址 = Modbus地址值,
            区域 = 区域值,
            寄存器数量 = 1,
            来源 = 符号文本,
        };
    }

    /// <summary>
    /// 软元件 + 序号 → 书写形式文本。X / Y 的序号是八进制语义，反解时必须按八进制还原
    /// （序号 8 还原为 <c>X10</c> 而不是 <c>X8</c>），否则与 PLC 服务端节点名对不上。
    /// </summary>
    public static string 生成软元件文本(软元素 元素, int 序号)
    {
        // 前缀是现场书写约定，与枚举成员名解耦（理由同 前缀对照表）
        var 前缀 = 元素 switch
        {
            软元素.特殊寄存器 => "SM",
            软元素.系统寄存器 => "SD",
            软元素.Q => "QX",
            软元素.接口 => "IX",
            _ => 元素.ToString(),
        };

        return 元素 is 软元素.X or 软元素.Y
            ? 前缀 + Convert.ToString(序号, 8)
            : 前缀 + 序号.ToString(CultureInfo.InvariantCulture);
    }

    // ── 小型 PLC 解析 ───────────────────────────────────────────────────

    private PLC地址 解析小型PLC(string 输入项)
    {
        // D1210.2 / R50.15 —— D/R 字内位（汇川位打包：BOOL 占 D 的一位，0–15）
        var 位匹配 = 小型字位模式.Match(输入项);
        if (位匹配.Success)
        {
            var 位元素 = 解析元素(位匹配.Groups["elem"].Value, 输入项);
            var 位规格 = 获取规格(位元素, 输入项);
            var 字序号 = 解析序号(位匹配.Groups["word"].Value, 10, 输入项, 位元素);
            var 位号 = int.Parse(位匹配.Groups["bit"].Value, CultureInfo.InvariantCulture);

            校验范围(字序号, 位规格, 输入项);

            return new PLC地址
            {
                元素 = 位元素,
                逻辑序号 = 字序号,
                Modbus地址 = 位规格.基址地址 + 字序号,
                区域 = 位规格.区域,          // 保持寄存器——读写走整字，再按位取/改
                寄存器数量 = 1,
                位序号 = 位号,
                来源 = 输入项,
            };
        }

        var 匹配 = 小型PLC模式.Match(输入项);
        if (!匹配.Success)
        {
            throw 地址解析异常.输入(输入项,
                "格式不正确。小型 PLC 应形如 X10、Y0、M100、D100、R200、D1210.2（软元件前缀 + 数字，D/R 支持字内位 .0–.15）");
        }

        var 元素 = 解析元素(匹配.Groups["elem"].Value, 输入项);
        var 规格项 = 获取规格(元素, 输入项);
        var 序号 = 解析序号(匹配.Groups["index"].Value, 规格项.编号进制, 输入项, 元素);
        校验范围(序号, 规格项, 输入项);

        return new PLC地址
        {
            元素 = 元素,
            逻辑序号 = 序号,
            Modbus地址 = 规格项.基址地址 + 序号,
            区域 = 规格项.区域,
            寄存器数量 = 1,
            来源 = 输入项,
        };
    }

    // ── 中型 PLC 解析 ───────────────────────────────────────────────────

    private PLC地址 解析中等PLC(string 输入项)
    {
        // QX0.3 / IX1.7 —— 按位寻址
        var 位匹配 = 中等位模式.Match(输入项);
        if (位匹配.Success)
        {
            var 元素 = 解析元素(位匹配.Groups["elem"].Value, 输入项);
            var 规格项 = 获取规格(元素, 输入项);
            var 字节序号 = 解析序号(位匹配.Groups["byte"].Value, 10, 输入项, 元素);
            var 位序号 = 位匹配.Groups["bit"].Value[0] - '0';
            var 序号 = 字节序号 * 8 + 位序号;

            校验范围(序号, 规格项, 输入项);

            return new PLC地址
            {
                元素 = 元素,
                逻辑序号 = 序号,
                Modbus地址 = 规格项.基址地址 + 序号,
                区域 = 规格项.区域,
                寄存器数量 = 1,
                位序号 = 位序号,
                来源 = 输入项,
            };
        }

        // MW100 / SM10 / SD10 —— 按字寻址
        var 字匹配 = 中等字模式.Match(输入项);
        if (字匹配.Success)
        {
            var 元素 = 解析元素(字匹配.Groups["elem"].Value, 输入项);
            var 规格项 = 获取规格(元素, 输入项);
            var 序号 = 解析序号(字匹配.Groups["index"].Value, 10, 输入项, 元素);

            校验范围(序号, 规格项, 输入项);

            return new PLC地址
            {
                元素 = 元素,
                逻辑序号 = 序号,
                Modbus地址 = 规格项.基址地址 + 序号,
                区域 = 规格项.区域,
                寄存器数量 = 1,
                功能码覆盖 = 规格项.读取功能代码,
                来源 = 输入项,
            };
        }

        throw 地址解析异常.输入(输入项,
            "格式不正确。中型 PLC(AM600) 应形如 QX0.3、IX1.0、MW100、SM10、SD10");
    }

    // ── 辅助方法 ────────────────────────────────────────────────────────

    private 软元素规格 获取规格(软元素 元素, string 输入项)
    {
        if (_规格.TryGetValue(元素, out var 规格项))
        {
            return 规格项;
        }

        var 支持 = string.Join(" / ", _规格.Keys.Select(键 => 键.ToString()));
        var 系列名称 = 系列 == PLC系列.小型PLC ? "小型 PLC(EASY/H5U/H3U)" : "中型 PLC(AM600/AC800)";
        throw 地址解析异常.输入(输入项,
            $"{系列名称} 不支持软元件 {元素}。当前机型支持的软元件：{支持}");
    }

    /// <summary>前缀对照表见 <see cref="汇川软元件规格表.前缀对照"/>（数据与算法分离）。</summary>
    private static 软元素 解析元素(string 令牌, string 完整输入)
    {
        return 汇川软元件规格表.前缀对照.TryGetValue(令牌, out var 元素)
            ? 元素
            : throw 地址解析异常.输入(完整输入, $"未知的软元件前缀 \"{令牌}\"");
    }

    /// <summary>
    /// 按指定进制解析序号。<b>这里是八进制坑的所在</b>：
    /// X / Y 的 "10" 必须解析为 8，而不是 10。
    /// </summary>
    private static int 解析序号(string 数字列表, int 进制, string 输入项, 软元素 元素)
    {
        long 值 = 0;
        foreach (var 通道 in 数字列表)
        {
            var 数字项 = 通道 - '0';
            if (数字项 < 0 || 数字项 >= 进制)
            {
                var 进制名称 = 进制 == 8 ? "八" : "十";
                throw 地址解析异常.输入(输入项,
                    $"序号中的字符 '{通道}' 不是合法的{进制名称}进制数字，" +
                    $"{(进制 == 8 ? "八进制只允许 0-7" : "十进制只允许 0-9")}");
            }

            值 = 值 * 进制 + 数字项;

            if (值 > int.MaxValue)
            {
                throw 地址解析异常.输入(输入项, "序号过大，超出整数范围");
            }
        }

        return (int)值;
    }

    private static void 校验范围(int 序号, 软元素规格 规格项, string 输入项)
    {
        if (序号 < 0 || 序号 > 规格项.最大序号)
        {
            throw 地址解析异常.超范围(输入项, 序号, 规格项.最大序号);
        }
    }

    /// <summary>
    /// 生成便于人工核对地址换算的说明文本，用于诊断界面。
    /// </summary>
    public string 说明(string 软元素)
    {
        var 地址值 = 解析(软元素);
        var 规格项 = _规格[地址值.元素];
        var 进制备注 = 规格项.编号进制 == 8 ? "（八进制解析）" : string.Empty;

        return string.Create(CultureInfo.InvariantCulture,
            $"{软元素} → {规格项.元素} 序号 {地址值.逻辑序号}{进制备注} " +
            $"= {规格项.基址地址} + {地址值.逻辑序号} = {地址值.Modbus地址} " +
            $"[{地址值.区域}]");
    }
}
