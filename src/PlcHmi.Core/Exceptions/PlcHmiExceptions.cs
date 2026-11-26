namespace PlcHmi.Core.Exceptions;

/// <summary>软元件地址解析失败。</summary>
public sealed class AddressParseException : Exception
{
    public AddressParseException(string Message) : base(Message) { }

    public AddressParseException(string Message, Exception innerPart) : base(Message, innerPart) { }

    public static AddressParseException Input(string inputItem, string originalReason) =>
        new($"无法解析软元件地址 \"{inputItem}\"：{originalReason}");

    public static AddressParseException OutOfRange(string inputItem, int Sequence, int MaxValue) =>
        new($"软元件地址 \"{inputItem}\" 越界：序号 {Sequence} 超出允许范围 0-{MaxValue}。");
}

/// <summary>点位写入值超出允许范围。</summary>
public sealed class TagValueOutOfRangeException : Exception
{
    public TagValueOutOfRangeException(string nameValue, double writeValue, double? MinValue, double? MaxValue)
        : base($"点位 \"{nameValue}\" 的写入值 {writeValue} 超出允许范围 [{MinValue?.ToString() ?? "-∞"}, {MaxValue?.ToString() ?? "+∞"}]。")
    {
        TagFullName = nameValue;
        Value = writeValue;
        Min = MinValue;
        Max = MaxValue;
    }

    public string TagFullName { get; }
    public double Value { get; }
    public double? Min { get; }
    public double? Max { get; }
}
