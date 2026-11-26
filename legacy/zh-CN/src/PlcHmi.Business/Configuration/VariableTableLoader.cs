using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using 上位机.核心.抽象;
using 上位机.核心.模型;

namespace 上位机.业务.配置;

/// <summary>
/// 变量表加载器。读取 <c>config/variables.json</c>（由变量表 CSV 导入生成）。
///
/// <para><b>文件缺失不是错误</b>：变量绑定是可选能力，未导入变量表时程序行为与旧版完全一致。</para>
/// </summary>
public static class 变量表加载器
{
    /// <summary>加载变量表。文件缺失 / 损坏时回退空表（<paramref name="错误"/> 给出原因）。</summary>
    public static 变量表文档 加载(out string? 错误, string? 文件路径 = null)
    {
        错误 = null;
        文件路径 ??= 配置路径.变量表;

        if (!File.Exists(文件路径))
        {
            return new 变量表文档();
        }

        try
        {
            using var 流 = File.OpenRead(文件路径);
            var 文档 = JsonSerializer.Deserialize<变量表文档>(流, JSON配置.读取);

            if (文档 is null)
            {
                错误 = $"变量表文件内容为空：{文件路径}。";
                return new 变量表文档();
            }

            return 文档;
        }
        catch (JsonException 异常)
        {
            错误 = $"解析变量表文件失败（JSON 格式）：{文件路径} —— {异常.Message}";
            return new 变量表文档();
        }
        catch (IOException 异常)
        {
            错误 = $"读取变量表文件失败：{文件路径} —— {异常.Message}";
            return new 变量表文档();
        }
        catch (UnauthorizedAccessException 异常)
        {
            错误 = $"读取变量表文件被拒绝：{文件路径} —— {异常.Message}";
            return new 变量表文档();
        }
    }

    /// <summary>把变量表文档序列化并原子落盘。</summary>
    public static async Task 异步落盘(变量表文档 文档, CancellationToken 取消令牌)
    {
        var JSON文本 = JsonSerializer.Serialize(文档, JSON配置.写入);
        await 原子文件.异步写入全部文本(配置路径.变量表, JSON文本, 取消令牌).ConfigureAwait(false);
    }
}

/// <summary>
/// 变量绑定回填助手。
///
/// <para><b>绑定语义：</b>配置条目填写「变量」后，启动 / 加载时从变量表查软元件地址回填到
/// <c>地址</c> 字段——下游全部服务继续按地址工作（Modbus 模式零改动）；
/// OPC UA 模式再由 NodeId 推导器按变量名做符号寻址。</para>
///
/// <para><b>失败降级</b>：变量不存在 / 地址无法寻址时保留原地址并返回错误说明，
/// 由调用方经配置健康登记——禁止无声降级（红线 R3）。</para>
/// </summary>
public static class 变量绑定回填
{
    /// <summary>
    /// 解析变量绑定，返回回填后的软元件地址。
    /// </summary>
    /// <param name="变量名">配置条目上的变量名；空表示未绑定（返回 <paramref name="当前地址"/>）。</param>
    /// <param name="表">变量表服务；未注入时视为未绑定。</param>
    /// <param name="解析器">软元件地址解析器（校验地址可寻址）。</param>
    /// <param name="当前地址">回填失败时保留的现有地址。</param>
    /// <param name="错误">失败原因；成功为空。</param>
    /// <returns>回填后的地址；失败返回 <paramref name="当前地址"/>。</returns>
    public static string 解析地址(
        string? 变量名,
        变量表服务接口? 表,
        地址解析器接口 解析器,
        string 当前地址,
        out string 错误)
    {
        if (表 is null)
        {
            错误 = string.Empty;
            return 当前地址;
        }

        return 解析地址(变量名, 表.查找(变量名), 解析器, 当前地址, out 错误);
    }

    /// <summary>按变量条目解析绑定（核心逻辑，其余重载最终都走这里）。</summary>
    public static string 解析地址(
        string? 变量名,
        变量表条目? 条目,
        地址解析器接口 解析器,
        string 当前地址,
        out string 错误)
    {
        错误 = string.Empty;

        if (string.IsNullOrWhiteSpace(变量名))
        {
            return 当前地址;
        }

        if (条目 is null)
        {
            错误 = $"变量「{变量名}」不在变量表中（可能未导入或已改名），保留原地址 {当前地址}。";
            return 当前地址;
        }

        if (!解析器.尝试解析(条目.软元件地址, out var 地址值) || 地址值 is null)
        {
            错误 = $"变量「{变量名}」的地址 {条目.软元件地址} 无法按当前机型寻址"
                   + "（请检查地址格式与机型支持的软元件范围，D/R 区字内位写作 D1210.2），保留原地址。";
            return 当前地址;
        }

        return 条目.软元件地址;
    }

    /// <summary>
    /// 批量回填一组参数条目的变量绑定（延时表 / 按钮表共用）。
    /// 变量失效时保留原地址并经 <paramref name="健康"/> 登记警告——禁止无声降级。
    /// </summary>
    public static void 应用条目绑定(
        IEnumerable<参数条目定义> 条目集合, string 类别名,
        变量表服务接口? 表, 地址解析器接口 解析器, 配置健康接口? 健康)
    {
        foreach (var 条目项 in 条目集合)
        {
            条目项.地址 = 解析地址(条目项.变量, 表, 解析器, 条目项.地址, out var 错误);

            if (错误.Length > 0)
            {
                健康?.上报回退($"{类别名}「{条目项.名称}」", 错误);
            }
        }
    }

    /// <summary>
    /// 把 recipe-config.json 的变量绑定批量回填到地址字段（App 启动期、DI 建立之前调用）。
    /// </summary>
    /// <param name="配置">已加载的配方配置（就地修改地址）。</param>
    /// <param name="文档">已加载的变量表文档。</param>
    /// <param name="解析器">软元件地址解析器。</param>
    /// <returns>回填失败的说明列表（空表示全部成功或没有绑定）。</returns>
    public static List<string> 应用配方配置(配方配置 配置, 变量表文档 文档, 地址解析器接口 解析器)
    {
        var 警告列表 = new List<string>();

        foreach (var 规格项 in 配置.参数)
        {
            if (string.IsNullOrWhiteSpace(规格项.变量))
            {
                continue;
            }

            var 回填地址 = 解析地址(规格项.变量, 文档.查找(规格项.变量), 解析器,
                格式化D区(规格项.地址), out var 错误);

            if (错误.Length > 0)
            {
                警告列表.Add($"参数项「{规格项.显示名称}」：{错误}");
                continue;
            }

            if (尝试解析D区(回填地址, out var 序号))
            {
                规格项.地址 = 序号;
            }
        }

        foreach (var 等级 in 配置.点动速度等级)
        {
            if (string.IsNullOrWhiteSpace(等级.变量))
            {
                continue;
            }

            var 回填地址 = 解析地址(等级.变量, 文档.查找(等级.变量), 解析器,
                格式化D区(等级.地址), out var 错误);

            if (错误.Length > 0)
            {
                警告列表.Add($"点动速度档位（{等级.轴} 轴）：{错误}");
                continue;
            }

            if (尝试解析D区(回填地址, out var 序号))
            {
                等级.地址 = 序号;
            }
        }

        var 控件 = 配置.控件;
        回填控件地址(警告列表, "机种号", 控件.配方号变量, 控件.配方号地址, 文档, 解析器,
            序号 => 控件.配方号地址 = 序号);
        回填控件地址(警告列表, "机种总数", 控件.配方数量变量, 控件.配方数量地址, 文档, 解析器,
            序号 => 控件.配方数量地址 = 序号);
        回填控件地址(警告列表, "示教点索引", 控件.示教序号变量, 控件.示教序号地址, 文档, 解析器,
            序号 => 控件.示教序号地址 = 序号);
        回填控件地址(警告列表, "当前面号", 控件.示教面变量, 控件.示教面地址, 文档, 解析器,
            序号 => 控件.示教面地址 = 序号);

        return 警告列表;
    }

    private static void 回填控件地址(
        List<string> 警告列表, string 标签文本, string? 变量名, int 当前地址,
        变量表文档 文档, 地址解析器接口 解析器, Action<int> 写回)
    {
        if (string.IsNullOrWhiteSpace(变量名))
        {
            return;
        }

        var 回填地址 = 解析地址(变量名, 文档.查找(变量名), 解析器, 格式化D区(当前地址), out var 错误);

        if (错误.Length > 0)
        {
            警告列表.Add($"控制地址（{标签文本}）：{错误}");
            return;
        }

        if (尝试解析D区(回填地址, out var 序号))
        {
            写回(序号);
        }
    }

    private static string 格式化D区(int 地址值) =>
        "D" + 地址值.ToString(CultureInfo.InvariantCulture);

    private static bool 尝试解析D区(string 文本, out int 值) =>
        int.TryParse(文本.TrimStart('D', 'd'), NumberStyles.Integer, CultureInfo.InvariantCulture, out 值);
}
