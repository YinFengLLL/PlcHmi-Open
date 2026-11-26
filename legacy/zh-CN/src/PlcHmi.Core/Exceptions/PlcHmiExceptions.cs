namespace 上位机.核心.异常;

/// <summary>软元件地址解析失败。</summary>
public sealed class 地址解析异常 : Exception
{
    public 地址解析异常(string 消息) : base(消息) { }

    public 地址解析异常(string 消息, Exception 内部) : base(消息, 内部) { }

    public static 地址解析异常 输入(string 输入项, string 原因) =>
        new($"无法解析软元件地址 \"{输入项}\"：{原因}");

    public static 地址解析异常 超范围(string 输入项, int 序号, int 最大值) =>
        new($"软元件地址 \"{输入项}\" 越界：序号 {序号} 超出允许范围 0-{最大值}。");
}

/// <summary>点位写入值超出允许范围。</summary>
public sealed class 标签值超范围异常 : Exception
{
    public 标签值超范围异常(string 名称值, double 写入值, double? 最小值, double? 最大值)
        : base($"点位 \"{名称值}\" 的写入值 {写入值} 超出允许范围 [{最小值?.ToString() ?? "-∞"}, {最大值?.ToString() ?? "+∞"}]。")
    {
        标签名称 = 名称值;
        值 = 写入值;
        最小 = 最小值;
        最大 = 最大值;
    }

    public string 标签名称 { get; }
    public double 值 { get; }
    public double? 最小 { get; }
    public double? 最大 { get; }
}
