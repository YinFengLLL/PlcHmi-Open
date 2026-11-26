using System.Text;
using Microsoft.Extensions.Logging;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// 配方服务的「CSV 导入导出」部分。
///
/// <para><b>为什么拆成 partial 文件</b>：主文件已越过项目规模门禁（400 行）的基线，
/// 而 CSV 交换是一条完整的、独立的职责线（编码 / 解码 / 文件读写 / 导入落盘），
/// 拆开后主文件只留"参数 · 示教 · 机种切换"三件事。</para>
///
/// <para><b>导入的边界语义</b>：CSV 解析失败（用户输入错误）返回结果对象而不是抛异常；
/// 权限不足与 IO 失败仍抛异常——调用方按"可预期的输入错误 vs 系统错误"分流。</para>
/// </summary>
public sealed partial class 配方服务
{
    /// <inheritdoc />
    public string 导出CSV(int 配方号)
    {
        校验配方号(配方号);

        var 数据 = _存储.查找(配方号) ?? new 配方数据 { 号 = 配方号 };
        return 配方CSV编解码器.编码(数据);
    }

    /// <inheritdoc />
    public async Task 异步导出CSV到文件(
        int 配方号, string 文件路径, CancellationToken 取消令牌 = default)
    {
        var 文本 = 导出CSV(配方号);

        var 目录 = Path.GetDirectoryName(文件路径);
        if (!string.IsNullOrEmpty(目录))
        {
            Directory.CreateDirectory(目录);
        }

        // 必须带 BOM：否则 Excel 双击打开时中文会乱码
        await File.WriteAllTextAsync(
                文件路径, 文本,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                取消令牌)
            .ConfigureAwait(false);

        _日志.LogInformation("机种 {No} 已导出到 {Path}", 配方号, 文件路径);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> 异步导出全部CSV到目录(
        string 目录, CancellationToken 取消令牌 = default)
    {
        Directory.CreateDirectory(目录);

        var 已写入 = new List<string>();

        for (var 号 = 1; 号 <= _配置.配方数量; 号++)
        {
            var 数据 = _存储.查找(号);

            // 没有数据的机种也导出 —— 作为空白模板，方便先填后导
            var 文件名称 = string.IsNullOrWhiteSpace(数据?.名称)
                ? $"recipe-{号:00}.csv"
                : $"recipe-{号:00}-{清洗文件名称(数据!.名称)}.csv";

            var 文件路径 = Path.Combine(目录, 文件名称);
            await 异步导出CSV到文件(号, 文件路径, 取消令牌).ConfigureAwait(false);
            已写入.Add(文件路径);
        }

        _日志.LogInformation("已导出 {Count} 个机种到 {Dir}", 已写入.Count, 目录);
        return 已写入;
    }

    /// <inheritdoc />
    public CSV导入结果 解析CSV(string CSV文本, int 回退配方号) =>
        配方CSV编解码器.解码(CSV文本, 回退配方号);

    /// <inheritdoc />
    public async Task<CSV导入结果> 异步从文件导入CSV(
        string 文件路径, int? 目标配方号 = null, CancellationToken 取消令牌 = default)
    {
        // 导入会整体覆盖目标机种的机种区段数据并落盘，必须经服务层权限校验
        // （解析阶段不落盘，无需校验；这里在第一次写主存前拦截）。
        _权限.要求(权限.切换配方);

        if (!File.Exists(文件路径))
        {
            return 失败($"文件不存在：{文件路径}");
        }

        string 文本;

        try
        {
            文本 = await File.ReadAllTextAsync(文件路径, 取消令牌).ConfigureAwait(false);
        }
        catch (Exception 异常)
        {
            return 失败($"读取文件失败：{异常.Message}");
        }

        var 结果值 = 解析CSV(文本, 目标配方号 ?? _当前号);

        if (!结果值.成功 || 结果值.配方 is null)
        {
            return 结果值;
        }

        // 目标机种号优先用显式指定的 —— 这样就能实现「把机种 A 拷到机种 B」
        var 目标号 = 目标配方号 ?? 结果值.配方.号;

        if (目标号 < 1 || 目标号 > _配置.配方数量)
        {
            return new CSV导入结果
            {
                成功 = false,
                错误集合 = { $"目标机种号 {目标号} 超出范围（1 – {_配置.配方数量}）。" },
                警告 = 结果值.警告,
            };
        }

        结果值.配方.号 = 目标号;

        var 数据 = _存储.获取或创建(目标号);
        数据.名称 = 结果值.配方.名称;
        数据.区段快照 = new Dictionary<int, int>(结果值.配方.区段快照);

        await _存储.异步保存(取消令牌).ConfigureAwait(false);

        // 导入只改本机数据，不写 PLC —— 要生效仍需「切换机种」或「下发机种」
        _日志.LogInformation("已从 {Path} 导入到机种 {No}", 文件路径, 目标号);
        配方已变更?.Invoke(this, EventArgs.Empty);

        return 结果值;
    }

    private static CSV导入结果 失败(string 消息) =>
        new() { 成功 = false, 错误集合 = { 消息 } };

    /// <summary>把机种名里不能做文件名的字符替换掉。</summary>
    private static string 清洗文件名称(string 名称值)
    {
        var 无效 = Path.GetInvalidFileNameChars();
        var 已清洗 = new string(名称值.Select(字符 => 无效.Contains(字符) ? '_' : 字符).ToArray()).Trim();

        return 已清洗.Length == 0 ? "未命名" : 已清洗;
    }
}
