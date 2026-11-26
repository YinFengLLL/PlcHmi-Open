namespace PlcHmi.Core.Models;

/// <summary>
/// 坐标显示与比较工具。
///
/// <para><b>为什么必须用它：</b>坐标是 32 位浮点数，无法精确表示所有两位小数——
/// <c>9999.99</c> 实际存为 <c>9999.99023</c>，<c>-999.99</c> 存为 <c>-999.98999</c>。
/// 因此界面显示必须统一走定点格式化，一致性比较必须带容差。</para>
/// </summary>
public static class CoordinateAux
{
    /// <summary>界面显示格式：XXXX.XX（定点两位小数）。</summary>
    public const string showFormat = "F2";

    /// <summary>默认比较容差（输出刻度 0.01 的一半，兼顾精度与稳定性）。</summary>
    public const double defaultTolerance = 0.005;

    /// <summary>格式化为界面显示文本。</summary>
    public static string Format(double Value) => Value.ToString(showFormat);

    /// <summary>格式化并附带单位文本。</summary>
    public static string Format(double Value, string? Unit) =>
        string.IsNullOrWhiteSpace(Unit) ? Format(Value) : $"{Format(Value)} {Unit}";

    /// <summary>
    /// 容差比较。
    /// <para><b>浮点禁止使用 <c>==</c></b>：写下去 9999.99，读回来是 9999.99023，
    /// 直接比较永远为 false。</para>
    /// </summary>
    public static bool AlmostEqual(double item, double item2, double Tolerance = defaultTolerance) =>
        Math.Abs(item - item2) < Tolerance;

    /// <summary>两个定位点是否在容差内相等。</summary>
    public static bool AlmostEqual(TeachPoint item, TeachPoint item2, double Tolerance = defaultTolerance) =>
        AlmostEqual(item.X, item2.X, Tolerance) &&
        AlmostEqual(item.Y, item2.Y, Tolerance) &&
        AlmostEqual(item.Z, item2.Z, Tolerance) &&
        AlmostEqual(item.R, item2.R, Tolerance);

    /// <summary>把值限制到允许范围内。</summary>
    public static double Clamp(double Value, double MinValue, double MaxValue) =>
        Value < MinValue ? MinValue : Value > MaxValue ? MaxValue : Value;

    /// <summary>值是否在允许范围内。</summary>
    public static bool IsInRange(double Value, double MinValue, double MaxValue) =>
        Value >= MinValue && Value <= MaxValue;

    /// <summary>尝试解析用户输入的坐标文本（容忍带单位后缀、前后空格）。</summary>
    public static bool TryParse(string? Text, out double Value)
    {
        Value = 0;

        if (string.IsNullOrWhiteSpace(Text))
        {
            return false;
        }

        var trimmed = Text.Trim();

        foreach (var suffixValue in suffixCollection)
        {
            if (trimmed.EndsWith(suffixValue, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[..^suffixValue.Length].Trim();
                break;
            }
        }

        return double.TryParse(
            trimmed,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out Value);
    }

    private static readonly string[] suffixCollection = ["mm", "MM", "°", "deg"];
}
