namespace 上位机.核心.抽象;

/// <summary>
/// IO 通信诊断的一行读数（某个功能码在某段地址上读到了什么）。
/// </summary>
/// <param name="标题">给人看的行标题，如「X 区 · 离散输入」。</param>
/// <param name="功能码">Modbus 功能码文本（FC01 / FC02）。</param>
/// <param name="地址">读取的起始 Modbus 地址。</param>
/// <param name="成功">读取是否成功（失败时 <paramref name="错误消息"/> 有内容）。</param>
/// <param name="位串">读到的位串（高位在左，仅成功时有）。</param>
/// <param name="置一位数">其中置 1 的位数。</param>
/// <param name="错误消息">读取失败时的异常摘要。</param>
public sealed record IO诊断项(
    string 标题,
    string 功能码,
    int 地址,
    bool 成功,
    string 位串,
    int 置一位数,
    string 错误消息)
{
    /// <summary>结果摘要（界面直接展示）。</summary>
    public string 结果文本 => 成功 ? $"读到 {置一位数} 个 1" : "读不到";
}

/// <summary>
/// IO 通信诊断报告。
///
/// <para><b>为什么需要它：</b>"IO 显示与物理对不上"这类问题，瓶颈是<b>看不到 PLC 到底返回了什么</b>——
/// 同一个地址用 FC01 和 FC02 读可能是完全不同的两块内存。本报告把两个功能码在 X/Y 段上的
/// 原始读数并排摆出来，现场一眼就能看出物理 IO 落在哪个功能码上，再据此配置
/// <c>io-points.json</c> 元的 <c>X区域</c>/<c>Y区域</c>。</para>
/// </summary>
public sealed class IO诊断报告
{
    /// <summary>全部读数行（建议顺序：X 的 FC01/FC02，Y 的 FC01/FC02）。</summary>
    public IReadOnlyList<IO诊断项> 项集合 { get; init; } = [];

    /// <summary>结论与下一步操作建议（可直接展示给现场工程师）。</summary>
    public string 结论 { get; init; } = string.Empty;
}

/// <summary>IO 通信诊断：把两个位功能码在 X / Y 段上的原始读数摆出来，用于定位"显示与物理对不上"。</summary>
public interface IO诊断服务接口
{
    /// <summary>
    /// 执行一次诊断（各发 4 个读请求：X/Y 段 × FC01/FC02）。
    /// </summary>
    /// <param name="X基址">X 区 Modbus 起始地址（io-points.json 元的 X基址地址）。</param>
    /// <param name="Y基址">Y 区 Modbus 起始地址（io-points.json 元的 Y基址地址）。</param>
    /// <param name="位数">每次读取的位数（默认 16，覆盖一栏八进制编号）。</param>
    Task<IO诊断报告> 异步诊断(int X基址, int Y基址, int 位数 = 16, CancellationToken 取消令牌 = default);
}
