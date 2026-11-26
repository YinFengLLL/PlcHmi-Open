using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// <see cref="辅助点服务"/> 的「CSV 导入导出」部分（partial）。
///
/// <para><b>为什么拆成 partial 文件：</b>主文件已接近 400 行规模门禁，
/// CSV 编解码（含引号感知拆分、重名/空名校验）是独立职责。</para>
/// </summary>
public sealed partial class 辅助点服务
{
    /// <inheritdoc />
    public string 导出CSV()
    {
        var 文本构建器 = new StringBuilder();
        文本构建器.AppendLine("# PlcHmi 其它点坐标 — 可用 Excel 编辑后，通过界面「导入」写回");
        文本构建器.AppendLine("# 导出时间," + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        文本构建器.AppendLine("名称,X基址,Y基址,Z基址");

        foreach (var 点项 in _点位)
        {
            文本构建器.AppendLine(string.Join(',',
                退出(点项.名称),
                点项.X基址.ToString(CultureInfo.InvariantCulture),
                点项.Y基址.ToString(CultureInfo.InvariantCulture),
                点项.Z基址.ToString(CultureInfo.InvariantCulture)));
        }

        return 文本构建器.ToString();
    }

    /// <inheritdoc />
    public async Task 异步导出CSV到文件(string 文件路径, CancellationToken 取消令牌 = default)
    {
        // UTF-8 带 BOM：Excel 双击可正确显示中文
        await File.WriteAllTextAsync(文件路径, 导出CSV(), new UTF8Encoding(true), 取消令牌)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> 异步从文件导入CSV(string 文件路径, CancellationToken 取消令牌 = default)
    {
        _权限.要求(权限.编辑绑定地址);

        var 文本 = await File.ReadAllTextAsync(文件路径, 取消令牌).ConfigureAwait(false);
        var 新列表 = new List<辅助点定义>();
        var 错误列表 = new List<string>();

        foreach (var 原始行 in 文本.Split('\n'))
        {
            var 行 = 原始行.TrimEnd('\r').Trim();

            if (行.Length == 0 || 行.StartsWith('#'))
            {
                continue;
            }

            // 带引号感知的拆分：与导出的 退出() 口径一致。
            // 曾经裸 Split(',')，名称 "A,B" 导出成 "A,B",1,2,3，导入被切成 5 段——
            // 要么判定失败要么落到错误列，往返失真
            var 字段 = 拆分行(行);

            if (字段.Length < 4 || 字段[0] == "名称")
            {
                continue;
            }

            if (!int.TryParse(字段[1], out var X基址)
                || !int.TryParse(字段[2], out var Y基址)
                || !int.TryParse(字段[3], out var Z基址))
            {
                错误列表.Add($"「{字段[0]}」的地址不是整数，已跳过。");
                continue;
            }

            新列表.Add(new 辅助点定义
            {
                名称 = 字段[0].Trim(),
                X基址 = X基址,
                Y基址 = Y基址,
                Z基址 = Z基址,
            });
        }

        if (错误列表.Count > 0)
        {
            throw new InvalidOperationException(string.Join("\n", 错误列表));
        }

        foreach (var 点项 in 新列表)
        {
            校验地址(点项);
        }

        // 导入路径同样查重与拒空：异步新增/异步编辑 都校验，唯独导入不校验——
        // 导入后可出现同名点（名称是查找/删除的唯一依据）与空名点
        var 已见 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var 点项 in 新列表)
        {
            if (string.IsNullOrWhiteSpace(点项.名称))
            {
                throw new InvalidOperationException("CSV 中存在名称为空的点行，已拒绝整个导入。");
            }

            if (!已见.Add(点项.名称))
            {
                throw new InvalidOperationException($"CSV 中名称「{点项.名称}」重复，已拒绝整个导入。");
            }
        }

        lock (_同步)
        {
            _点位.Clear();
            _点位.AddRange(新列表);
        }

        await 异步落盘(取消令牌).ConfigureAwait(false);

        _日志.LogInformation("已从 CSV 导入其它点定义：{Count} 个", _点位.Count);
        return _点位.Count;
    }

    private static string 退出(string 值) =>
        值.Contains(',') || 值.Contains('"') ? $"\"{值.Replace("\"", "\"\"")}\"" : 值;

    /// <summary>
    /// 带引号感知的 CSV 行拆分：引号内的逗号不分字段，"" 还原为一个引号。
    /// 与 <see cref="退出"/> 的加引号规则互为逆运算。
    /// </summary>
    private static string[] 拆分行(string 行)
    {
        var 字段 = new List<string>();
        var 当前 = new StringBuilder();
        var 在引号内 = false;

        for (var 序号 = 0; 序号 < 行.Length; 序号++)
        {
            var 字符 = 行[序号];

            if (在引号内)
            {
                if (字符 != '"')
                {
                    当前.Append(字符);
                }
                else if (序号 + 1 < 行.Length && 行[序号 + 1] == '"')
                {
                    当前.Append('"');
                    序号++;
                }
                else
                {
                    在引号内 = false;
                }
            }
            else if (字符 == '"')
            {
                在引号内 = true;
            }
            else if (字符 == ',')
            {
                字段.Add(当前.ToString());
                当前.Clear();
            }
            else
            {
                当前.Append(字符);
            }
        }

        字段.Add(当前.ToString());
        return 字段.ToArray();
    }
}
