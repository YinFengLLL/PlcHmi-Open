using System.Text;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.业务.配置;

/// <summary>
/// 汇川变量表 CSV 解析结果。
/// </summary>
/// <param name="文档">解析出的变量表文档。</param>
/// <param name="跳过行数">被跳过的行数（结构体头行 / 不支持的类型 / 缺地址）。</param>
public sealed record 变量表解析结果(变量表文档 文档, int 跳过行数);

/// <summary>
/// 汇川变量表 CSV 解析器。
///
/// <para><b>支持的格式</b>（汇川 AutoShop 导出，9 列 + 尾逗号）：</para>
/// <code>
/// 序号,变量名,数据类型,隐藏初始值,初始值,掉电保持,网络公开,注释,软元件地址,
/// 1,X轴参数,单轴参数,,...,保持,公有,,D1200,        ← 结构体头行：类型非基础类型，跳过
/// ,X轴参数.r_定位速度,REAL,,0.000000,,,,D1202,     ← 成员行：完整路径变量名
/// ,X轴命令.b_轴使能,BOOL,,OFF,,,,D1210.0,           ← 字.位形式：可寻址（读写按整字读-改-写）
/// </code>
///
/// <para><b>编码</b>：先按 UTF-8 严格解码，失败（汇川部分版本导出 GBK）回退 GBK。</para>
/// </summary>
public static class 变量表CSV解析器
{
    /// <summary>CSV 数据类型 → 上位机标签数据类型的映射表。</summary>
    private static readonly Dictionary<string, 标签数据类型> 类型映射表 = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BOOL"] = 标签数据类型.布尔,
        ["SINT"] = 标签数据类型.Int16,
        ["INT"] = 标签数据类型.Int16,
        ["USINT"] = 标签数据类型.UInt16,
        ["UINT"] = 标签数据类型.UInt16,
        ["BYTE"] = 标签数据类型.UInt16,
        ["WORD"] = 标签数据类型.UInt16,
        ["DINT"] = 标签数据类型.Int32,
        ["UDINT"] = 标签数据类型.UInt32,
        ["DWORD"] = 标签数据类型.UInt32,
        ["TIME"] = 标签数据类型.UInt32,
        ["REAL"] = 标签数据类型.Float32,
        ["LREAL"] = 标签数据类型.Float64,
    };

    static 变量表CSV解析器()
    {
        // GBK 编码在 .NET Core 后需要手动注册 CodePages 提供程序
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>把 CSV 文本解析为变量表文档。文件级失败（空内容 / 全部行无效）抛 <see cref="InvalidOperationException"/>。</summary>
    public static 变量表解析结果 解析文本(string CSV文本)
    {
        if (string.IsNullOrWhiteSpace(CSV文本))
        {
            throw new InvalidOperationException("变量表 CSV 内容为空。");
        }

        var 条目列表 = new List<变量表条目>();
        var 跳过行数 = 0;
        var 行列表 = CSV文本.ReplaceLineEndings("\n").Split('\n');

        foreach (var 原始行 in 行列表.Skip(1))    // 首行为表头
        {
            var 行文本 = 原始行.TrimEnd('\r').Trim();

            if (行文本.Length == 0)
            {
                continue;
            }

            var 列 = 行文本.Split(',');
            if (列.Length < 9)
            {
                跳过行数++;
                continue;
            }

            var 变量名 = 列[1].Trim();
            var 类型文本 = 列[2].Trim();
            var 地址文本 = 列[8].Trim();

            // 变量名为空（纯结构体头行变体）或缺地址：跳过
            if (变量名.Length == 0 || 地址文本.Length == 0)
            {
                跳过行数++;
                continue;
            }

            // 非基础类型（结构体头行如 "单轴参数"、STRING 等）：跳过并计数
            if (!类型映射表.TryGetValue(类型文本, out var 数据类型))
            {
                跳过行数++;
                continue;
            }

            条目列表.Add(new 变量表条目
            {
                变量名 = 变量名,
                软元件地址 = 地址文本,
                数据类型 = 数据类型,
                注释 = 列[7].Trim().Length > 0 ? 列[7].Trim() : null,
            });
        }

        if (条目列表.Count == 0)
        {
            throw new InvalidOperationException(
                $"变量表 CSV 中没有可识别的变量行（跳过 {跳过行数} 行）。请确认文件为汇川变量表导出格式。");
        }

        return new 变量表解析结果(new 变量表文档 { 条目集合 = 条目列表 }, 跳过行数);
    }

    /// <summary>读文件并自动识别编码（UTF-8 优先，失败回退 GBK）。</summary>
    public static string 读取文本(string 文件路径)
    {
        var 字节 = File.ReadAllBytes(文件路径);

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(字节);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding("GBK").GetString(字节);
        }
    }
}
