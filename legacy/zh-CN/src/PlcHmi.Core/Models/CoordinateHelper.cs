namespace 上位机.核心.模型;

/// <summary>
/// 坐标显示与比较工具。
///
/// <para><b>为什么必须用它：</b>坐标是 32 位浮点数，无法精确表示所有两位小数——
/// <c>9999.99</c> 实际存为 <c>9999.99023</c>，<c>-999.99</c> 存为 <c>-999.98999</c>。
/// 因此界面显示必须统一走定点格式化，一致性比较必须带容差。</para>
/// </summary>
public static class 坐标辅助
{
    /// <summary>界面显示格式：XXXX.XX（定点两位小数）。</summary>
    public const string 显示格式 = "F2";

    /// <summary>默认比较容差（输出刻度 0.01 的一半，兼顾精度与稳定性）。</summary>
    public const double 默认容差 = 0.005;

    /// <summary>格式化为界面显示文本。</summary>
    public static string 格式化(double 值) => 值.ToString(显示格式);

    /// <summary>格式化并附带单位文本。</summary>
    public static string 格式化(double 值, string? 单元) =>
        string.IsNullOrWhiteSpace(单元) ? 格式化(值) : $"{格式化(值)} {单元}";

    /// <summary>
    /// 容差比较。
    /// <para><b>浮点禁止使用 <c>==</c></b>：写下去 9999.99，读回来是 9999.99023，
    /// 直接比较永远为 false。</para>
    /// </summary>
    public static bool 近乎相等(double 甲, double 乙, double 容差 = 默认容差) =>
        Math.Abs(甲 - 乙) < 容差;

    /// <summary>两个定位点是否在容差内相等。</summary>
    public static bool 近乎相等(示教点位 甲, 示教点位 乙, double 容差 = 默认容差) =>
        近乎相等(甲.X, 乙.X, 容差) &&
        近乎相等(甲.Y, 乙.Y, 容差) &&
        近乎相等(甲.Z, 乙.Z, 容差) &&
        近乎相等(甲.R, 乙.R, 容差);

    /// <summary>把值限制到允许范围内。</summary>
    public static double 钳制(double 值, double 最小值, double 最大值) =>
        值 < 最小值 ? 最小值 : 值 > 最大值 ? 最大值 : 值;

    /// <summary>值是否在允许范围内。</summary>
    public static bool 是否在范围内(double 值, double 最小值, double 最大值) =>
        值 >= 最小值 && 值 <= 最大值;

    /// <summary>尝试解析用户输入的坐标文本（容忍带单位后缀、前后空格）。</summary>
    public static bool 尝试解析(string? 文本, out double 值)
    {
        值 = 0;

        if (string.IsNullOrWhiteSpace(文本))
        {
            return false;
        }

        var 已裁剪 = 文本.Trim();

        foreach (var 后缀值 in 后缀集合)
        {
            if (已裁剪.EndsWith(后缀值, StringComparison.OrdinalIgnoreCase))
            {
                已裁剪 = 已裁剪[..^后缀值.Length].Trim();
                break;
            }
        }

        return double.TryParse(
            已裁剪,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out 值);
    }

    private static readonly string[] 后缀集合 = ["mm", "MM", "°", "deg"];
}
