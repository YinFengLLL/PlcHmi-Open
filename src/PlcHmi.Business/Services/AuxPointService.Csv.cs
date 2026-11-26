using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// <see cref="AuxPointService"/> 的「CSV 导入导出」部分（partial）。
///
/// <para><b>为什么拆成 partial 文件：</b>主文件已接近 400 行规模门禁，
/// CSV 编解码（含引号感知拆分、重名/空名校验）是独立职责。</para>
/// </summary>
public sealed partial class AuxPointService
{
    /// <inheritdoc />
    public string ExportCsv()
    {
        var textBuilder = new StringBuilder();
        textBuilder.AppendLine("# PlcHmi 其它点坐标 — 可用 Excel 编辑后，通过界面「导入」写回");
        textBuilder.AppendLine("# 导出时间," + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        textBuilder.AppendLine("名称,X基址,Y基址,Z基址");

        foreach (var PointEntry in _point)
        {
            textBuilder.AppendLine(string.Join(',',
                Exit(PointEntry.Name),
                PointEntry.XBaseAddress.ToString(CultureInfo.InvariantCulture),
                PointEntry.YBaseAddress.ToString(CultureInfo.InvariantCulture),
                PointEntry.ZBaseAddress.ToString(CultureInfo.InvariantCulture)));
        }

        return textBuilder.ToString();
    }

    /// <inheritdoc />
    public async Task ExportCsvToFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        // UTF-8 带 BOM：Excel 双击可正确显示中文
        await File.WriteAllTextAsync(filePath, ExportCsv(), new UTF8Encoding(true), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> FromFileImportCsvAsync(string filePath, CancellationToken cancellationToken = default)
    {
        _permission.WantRequire(Permission.EditBindAddress);

        var Text = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        var NewList = new List<AuxPointDefinition>();
        var errorList = new List<string>();

        foreach (var SourceRow in Text.Split('\n'))
        {
            var Row = SourceRow.TrimEnd('\r').Trim();

            if (Row.Length == 0 || Row.StartsWith('#'))
            {
                continue;
            }

            // 带引号感知的拆分：与导出的 退出() 口径一致。
            // 曾经裸 Split(',')，名称 "A,B" 导出成 "A,B",1,2,3，导入被切成 5 段——
            // 要么判定失败要么落到错误列，往返失真
            var field = SplitRow(Row);

            if (field.Length < 4 || field[0] == "名称")
            {
                continue;
            }

            if (!int.TryParse(field[1], out var XBaseAddress)
                || !int.TryParse(field[2], out var YBaseAddress)
                || !int.TryParse(field[3], out var ZBaseAddress))
            {
                errorList.Add($"「{field[0]}」的地址不是整数，已跳过。");
                continue;
            }

            NewList.Add(new AuxPointDefinition
            {
                Name = field[0].Trim(),
                XBaseAddress = XBaseAddress,
                YBaseAddress = YBaseAddress,
                ZBaseAddress = ZBaseAddress,
            });
        }

        if (errorList.Count > 0)
        {
            throw new InvalidOperationException(string.Join("\n", errorList));
        }

        foreach (var PointEntry in NewList)
        {
            ValidateAddress(PointEntry);
        }

        // 导入路径同样查重与拒空：异步新增/异步编辑 都校验，唯独导入不校验——
        // 导入后可出现同名点（名称是查找/删除的唯一依据）与空名点
        var alreadySee = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var PointEntry in NewList)
        {
            if (string.IsNullOrWhiteSpace(PointEntry.Name))
            {
                throw new InvalidOperationException("CSV 中存在名称为空的点行，已拒绝整个导入。");
            }

            if (!alreadySee.Add(PointEntry.Name))
            {
                throw new InvalidOperationException($"CSV 中名称「{PointEntry.Name}」重复，已拒绝整个导入。");
            }
        }

        lock (_sync)
        {
            _point.Clear();
            _point.AddRange(NewList);
        }

        await PersistAsync(cancellationToken).ConfigureAwait(false);

        _log.LogInformation("已从 CSV 导入其它点定义：{Count} 个", _point.Count);
        return _point.Count;
    }

    private static string Exit(string Value) =>
        Value.Contains(',') || Value.Contains('"') ? $"\"{Value.Replace("\"", "\"\"")}\"" : Value;

    /// <summary>
    /// 带引号感知的 CSV 行拆分：引号内的逗号不分字段，"" 还原为一个引号。
    /// 与 <see cref="Exit"/> 的加引号规则互为逆运算。
    /// </summary>
    private static string[] SplitRow(string Row)
    {
        var field = new List<string>();
        var Current = new StringBuilder();
        var atQuoteNumberInner = false;

        for (var Sequence = 0; Sequence < Row.Length; Sequence++)
        {
            var character = Row[Sequence];

            if (atQuoteNumberInner)
            {
                if (character != '"')
                {
                    Current.Append(character);
                }
                else if (Sequence + 1 < Row.Length && Row[Sequence + 1] == '"')
                {
                    Current.Append('"');
                    Sequence++;
                }
                else
                {
                    atQuoteNumberInner = false;
                }
            }
            else if (character == '"')
            {
                atQuoteNumberInner = true;
            }
            else if (character == ',')
            {
                field.Add(Current.ToString());
                Current.Clear();
            }
            else
            {
                Current.Append(character);
            }
        }

        field.Add(Current.ToString());
        return field.ToArray();
    }
}
