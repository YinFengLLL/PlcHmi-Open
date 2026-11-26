using Microsoft.Extensions.Logging;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;

namespace PlcHmi.Business.Services;

/// <summary>
/// IO 通信诊断实现：X / Y 段各用 FC01 与 FC02 读一次，并排呈现。
///
/// <para><b>为什么按功能码对照而不是只报"读失败"：</b>2026-10-17 现场第二次报障的真因就是
/// "X 是物理输入、本该用 FC02 读，却被放在线圈表用 FC01 读"——连接正常、无异常、无告警，
/// 只是读到了同一段地址上无关的位。只有把两个功能码的读数并排摆出来，这种错位才看得见。</para>
/// </summary>
public sealed class IoDiagnosticService : IIoDiagnosticService
{
    private readonly IModbusClient _client;
    private readonly ILogger<IoDiagnosticService> _log;

    public IoDiagnosticService(IModbusClient Client, ILogger<IoDiagnosticService> Log)
    {
        _client = Client;
        _log = Log;
    }

    /// <inheritdoc />
    public async Task<IoDiagnosticReport> DiagnosticAsync(
        int XBaseAddress, int YBaseAddress, int bitCount = 16, CancellationToken cancellationToken = default)
    {
        var ItemCollection = new List<IoDiagnosticItem>(4);

        ItemCollection.Add(await Probe("X 区 · 线圈", "FC01", XBaseAddress, bitCount, ModbusArea.Coil, cancellationToken));
        ItemCollection.Add(await Probe("X 区 · 离散输入", "FC02", XBaseAddress, bitCount, ModbusArea.DiscreteInput, cancellationToken));
        ItemCollection.Add(await Probe("Y 区 · 线圈", "FC01", YBaseAddress, bitCount, ModbusArea.Coil, cancellationToken));
        ItemCollection.Add(await Probe("Y 区 · 离散输入", "FC02", YBaseAddress, bitCount, ModbusArea.DiscreteInput, cancellationToken));

        var Conclusion = GenerateConclusion(ItemCollection);

        _log.LogInformation(
            "IO 通信诊断完成：{Summary}；结论：{Conclusion}",
            string.Join("；", ItemCollection.Select(Item => $"{Item.Title}={Item.ResultText}")), Conclusion);

        return new IoDiagnosticReport { ItemCollection = ItemCollection, Conclusion = Conclusion };
    }

    /// <summary>读一段位，无论成败都转成一行（失败也要让人看见，不能静默跳过）。</summary>
    private async Task<IoDiagnosticItem> Probe(
        string Title, string functionCode, int Address, int bitCount, ModbusArea Area, CancellationToken cancellationToken)
    {
        try
        {
            var ValueList = Area is ModbusArea.DiscreteInput
                ? await _client.ReadDiscreteInputAsync(Address, bitCount, cancellationToken).ConfigureAwait(false)
                : await _client.ReadCoilAsync(Address, bitCount, cancellationToken).ConfigureAwait(false);

            var setOneBitCount = 0;
            var builder = new System.Text.StringBuilder(bitCount);

            // 高位在左书写：与 PLC 位号顺序一致（地址 + 序号 从左到右递增）
            for (var Sequence = ValueList.Length - 1; Sequence >= 0; Sequence--)
            {
                var Value = Sequence < bitCount && ValueList[Sequence];
                builder.Append(Value ? '1' : '0');

                if (Value)
                {
                    setOneBitCount++;
                }
            }

            return new IoDiagnosticItem(Title, functionCode, Address, true, builder.ToString(), setOneBitCount, string.Empty);
        }
        catch (Exception exception)
        {
            // 诊断的目的就是收集失败：这里绝不能把异常抛给界面
            return new IoDiagnosticItem(Title, functionCode, Address, false, string.Empty, 0, exception.Message);
        }
    }

    /// <summary>按"哪个功能码读到了 1"给出结论与下一步操作。</summary>
    private static string GenerateConclusion(IReadOnlyList<IoDiagnosticItem> ItemCollection)
    {
        var builder = new System.Text.StringBuilder();

        builder.Append(JudgeOneSide(ItemCollection, "X", 0, 1));
        builder.Append(JudgeOneSide(ItemCollection, "Y", 2, 3));

        builder.Append("配置方法：用文本编辑器打开 config/io-points.json，把「元」里对应的 "
                      + "X区域 / Y区域 配成上面结论中的功能码，保存后重启程序生效。");

        return builder.ToString();
    }

    /// <summary>判断某一侧（X 或 Y）的物理 IO 落在哪个功能码上。</summary>
    private static string JudgeOneSide(IReadOnlyList<IoDiagnosticItem> ItemCollection, string sideName, int coilIndex, int discreteIndex)
    {
        var Coil = ItemCollection[coilIndex];
        var discrete = ItemCollection[discreteIndex];

        var coilHas = Coil.Success && Coil.setOneBitCount > 0;
        var discreteHas = discrete.Success && discrete.setOneBitCount > 0;

        if (coilHas && discreteHas)
        {
            return $"{sideName} 区：两个功能码都读到信号，无法自动判定——请以 PLC 地址分配表为准，"
                   + $"在 io-points.json 元里把 {sideName}区域 显式配为 \"线圈\" 或 \"离散输入\"。";
        }

        if (discreteHas)
        {
            return $"{sideName} 区：物理状态在【离散输入（FC02）】——请把 {sideName}区域 配为 \"离散输入\"。";
        }

        if (coilHas)
        {
            return $"{sideName} 区：物理状态在【线圈（FC01）】——请把 {sideName}区域 配为 \"线圈\"。";
        }

        if (!Coil.Success || !discrete.Success)
        {
            return $"{sideName} 区：两个功能码都读不到（{Summary(Coil)} / {Summary(discrete)}）——"
                   + "请检查 PLC 连接、从站号与该地址段是否在从站的映射范围内。";
        }

        return $"{sideName} 区：两个功能码都读到全 0——若现场这些点实际有输入/输出，"
               + "请核对地址（X/Y 按八进制编号：X10 = 第 8 点）与基址配置。";
    }

    private static string Summary(IoDiagnosticItem Item) =>
        Item.Success ? "读取成功但全 0" : $"读取失败（{Item.errorMessage}）";
}
