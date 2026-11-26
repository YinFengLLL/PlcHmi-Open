using Microsoft.Extensions.Logging;
using 上位机.业务.配置;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// 汇川变量表服务。内存持有 <see cref="变量表文档"/>（启动时从 variables.json 预加载），
/// 提供按名查找 / 按地址反查（供 OPC UA 符号寻址）与 CSV 导入。
///
/// <para><b>按地址反查的匹配规则</b>：变量的软元件地址经解析得到 (区域, Modbus地址)，
/// 以此为键建表；查询时要求<b>起始地址与寄存器数量都精确匹配</b>——
/// REAL 变量占 2 字，只有从首地址、按 2 字数量查询才命中，
/// 这样轮询合并块才能识别出“这 2 个寄存器其实是同一个 REAL 节点”。</para>
///
/// <para><b>无法寻址的条目</b>（如 D1210.0 字.位形式）：保留在条目列表里供界面展示与绑定校验拒绝，
/// 但不进入按地址反查表——Modbus 管线无法表达字内位。</para>
/// </summary>
public sealed class 变量表服务 : 变量表服务接口
{
    private readonly 地址解析器接口 _解析器;
    private readonly ILogger<变量表服务> _日志;
    private readonly SemaphoreSlim _写闸锁 = new(1, 1);

    /// <summary>按变量名查找（大小写不敏感）。volatile 引用整体替换，读侧免锁。</summary>
    private volatile Dictionary<string, 变量表条目> _名称索引 = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>按 (区域, Modbus地址) 反查：键为变量首地址。volatile 引用整体替换，读侧免锁。</summary>
    private volatile Dictionary<(Modbus区域 区域, int 起始地址), (变量表条目 条目, int 寄存器数量)> _地址索引 = new();

    private volatile 变量表文档 _文档;

    public 变量表服务(变量表文档 初始文档, 地址解析器接口 解析器, ILogger<变量表服务> 日志)
    {
        _解析器 = 解析器;
        _日志 = 日志;
        _文档 = 初始文档;
        重建索引();
    }

    /// <inheritdoc />
    public IReadOnlyList<变量表条目> 条目集合 => _文档.条目集合;

    /// <inheritdoc />
    public event EventHandler? 已重新导入;

    /// <inheritdoc />
    public 变量表条目? 查找(string? 变量名)
    {
        if (string.IsNullOrWhiteSpace(变量名))
        {
            return null;
        }

        // 先取 volatile 引用再查：CSV 导入瞬间整体换表，读侧看到的是旧表或新表，
        // 绝不会看到"半建好的索引"（此前重建是原地 Clear，读侧无锁 → 漏项/KeyNotFound）
        var 索引 = _名称索引;
        return 索引.TryGetValue(变量名.Trim(), out var 条目) ? 条目 : null;
    }

    /// <inheritdoc />
    public 变量表条目? 按地址查找(Modbus区域 区域值, int 起始地址值, int 寄存器数量值)
    {
        var 索引 = _地址索引;
        return 索引.TryGetValue((区域值, 起始地址值), out var 命中)
            && 命中.寄存器数量 == 寄存器数量值 ? 命中.条目 : null;
    }

    /// <inheritdoc />
    public 变量表条目? 按首地址查找(Modbus区域 区域值, int 起始地址值, out int 寄存器数量值)
    {
        var 索引 = _地址索引;

        if (索引.TryGetValue((区域值, 起始地址值), out var 命中))
        {
            寄存器数量值 = 命中.寄存器数量;
            return 命中.条目;
        }

        寄存器数量值 = 0;
        return null;
    }

    /// <inheritdoc />
    public async Task<变量表导入结果> 异步导入(string CSV文件路径, CancellationToken 取消令牌 = default)
    {
        var 解析结果 = 变量表CSV解析器.解析文本(变量表CSV解析器.读取文本(CSV文件路径));

        await _写闸锁.WaitAsync(取消令牌).ConfigureAwait(false);
        try
        {
            // 先落盘后换内存：写入失败（磁盘不可写）时旧表保持不变
            await 变量表加载器.异步落盘(解析结果.文档, 取消令牌).ConfigureAwait(false);

            _文档 = 解析结果.文档;
            重建索引();
        }
        finally
        {
            _写闸锁.Release();
        }

        _日志.LogInformation("变量表已导入：{Count} 个变量，跳过 {Skipped} 行（来源 {File}）",
            解析结果.文档.条目集合.Count, 解析结果.跳过行数, CSV文件路径);

        已重新导入?.Invoke(this, EventArgs.Empty);

        return new 变量表导入结果
        {
            有效条数 = 解析结果.文档.条目集合.Count,
            跳过行数 = 解析结果.跳过行数,
        };
    }

    /// <summary>
    /// 重建名称索引与地址反查索引。
    /// <para>构建全新字典后<b>整体替换</b> volatile 引用：读侧免锁且永远看到完整快照，
    /// 不会出现原地 Clear 期间的半建好状态。调用方（构造函数 / 异步导入）已持写闸锁。</para>
    /// </summary>
    private void 重建索引()
    {
        var 新名称索引 = new Dictionary<string, 变量表条目>(StringComparer.OrdinalIgnoreCase);
        var 新地址索引 = new Dictionary<(Modbus区域 区域, int 起始地址), (变量表条目 条目, int 寄存器数量)>();

        var 被遮蔽的位条目 = new List<string>();

        foreach (var 条目 in _文档.条目集合)
        {
            if (条目.变量名.Length > 0)
            {
                新名称索引[条目.变量名] = 条目;
            }

            // 地址无法按当前机型寻址（如中型 PLC 的非法格式）的条目只进名称索引。
            // D/R 字内位（D1210.2 等，汇川位打包）自 2026-09-17 起可寻址：
            // 读写按整字处理，故占 1 字。
            if (_解析器.尝试解析(条目.软元件地址, out var 地址值) && 地址值 is not null)
            {
                var 键 = (地址值.区域, 地址值.Modbus地址);

                // 同一字上若已有条目，反查应回到先占者，不被后来的位条目抢走
                if (地址值.是字内位 && 新地址索引.ContainsKey(键))
                {
                    被遮蔽的位条目.Add($"{条目.变量名}（{条目.软元件地址}）");
                    continue;
                }

                var 寄存器数量项 = 地址值.是字内位 ? 1 : _解析器.获取寄存器数量(条目.数据类型);
                新地址索引[键] = (条目, 寄存器数量项);
            }
        }

        // 先发布两张表，再让后续读取看到新快照
        _名称索引 = 新名称索引;
        _地址索引 = 新地址索引;

        // 被遮蔽的条目**不能静默丢弃**：同一个 D 字上只有第一个变量能用于反查
        // （Modbus/OPC UA 侧读写的是整字），其余变量按名称仍可寻址。
        // 配置静默降级成"少了一半绑定"比崩溃更难查 —— 所以这里必须留痕。
        if (被遮蔽的位条目.Count > 0)
        {
            _日志.LogWarning(
                "变量表有 {Count} 个条目与同一字上的先占变量共享地址，地址反查将由先占者响应"
                + "（按变量名绑定不受影响）：{Items}",
                被遮蔽的位条目.Count, string.Join("、", 被遮蔽的位条目.Take(8)));
        }
    }
}
