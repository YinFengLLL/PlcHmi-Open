using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using 上位机.业务.配置;
using 上位机.核心.抽象;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// IO 点位服务。管理 X / Y 点位表及实时状态。
///
/// <para><b>设计要点：</b>每个 IO 点的“含义”由工程师在界面上填写并保存回 JSON，
/// 程序本身不需要知道 “X0 是什么”。因此换设备时零改代码。</para>
/// </summary>
public sealed class IO点位服务 : IO点位服务接口
{
    private readonly ILogger<IO点位服务> _日志;
    private readonly 配置健康接口? _健康;
    private readonly 权限守卫接口 _权限;
    private readonly ConcurrentDictionary<string, IO点位状态> _状态 = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 代码 → 点位定义索引。轮询热路径每个 IO 点每周期都要反查定义
    /// （是否取反），线性搜索使整个快组退化为 O(n²)/周期；改为加载与
    /// 新增一栏时整体重建（引用原子替换），查找 O(1)。
    /// </summary>
    private Dictionary<string, IO点位> _点位索引 = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 权限守卫是<b>必填</b>参数，刻意不给默认值：本项目审查标准 R5 要求写操作在服务层校验，
    /// 而"缺少守卫时默认放行"正是该规则要拦的失效模式（界面隐藏按钮只是防误触，不是边界）。
    /// 测试若不需要覆盖权限分支，请显式传入 <see cref="允许全部权限守卫"/>。
    /// </summary>
    public IO点位服务(权限守卫接口 权限, ILogger<IO点位服务> 日志, 配置健康接口? 健康 = null)
    {
        ArgumentNullException.ThrowIfNull(权限);

        _权限 = 权限;
        _日志 = 日志;
        _健康 = 健康;
        表格 = new IO点位表格();
        加载();
    }

    /// <inheritdoc />
    public IO点位表格 表格 { get; private set; }

    /// <inheritdoc />
    public event EventHandler<IO点位状态>? 状态已变更;

    /// <summary>未命中时的空状态（静态单例：每秒数百次未命中不该每次 new 一个对象）。</summary>
    private static readonly IO点位状态 空状态 = new(string.Empty, false, DateTime.MinValue);

    /// <inheritdoc />
    public IO点位状态 获取状态(string 代码值) =>
        _状态.TryGetValue(代码值, out var 状态值) ? 状态值 : 空状态;

    /// <inheritdoc />
    public void 更新状态(string 代码值, bool 原始值)
    {
        var 点位项 = 查找点位(代码值);
        var 逻辑 = 点位项?.取反 == true ? !原始值 : 原始值;

        // 2026-11-19 性能改造：把"取时间 + 写字典"降到只做一次，事件只在真实翻转时抛。
        // 注意【时间戳必须每次刷新】：现场与回归用例都用"时间戳是否随周期推进"
        // 来证明该点仍被采样（IO 位区回退测试就卡在这条上），跳过它会变成功能退化。
        var 已存在 = _状态.TryGetValue(代码值, out var 旧);
        var 已翻转 = !已存在 || 旧.是否开启 != 逻辑;

        var 新状态值 = new IO点位状态(代码值, 逻辑, DateTime.Now);
        _状态[代码值] = 新状态值;

        if (已翻转)
        {
            状态已变更?.Invoke(this, 新状态值);
        }
    }

    /// <inheritdoc />
    public void 更新含义(string 代码值, string 含义)
    {
        // R5：改的是会被 异步保存 落盘的数据，与保存同权限
        _权限.要求(权限.编辑IO点位);

        var 点位项 = 查找点位(代码值);
        if (点位项 is null)
        {
            _日志.LogWarning("尝试修改不存在的 IO 点含义：{Code}", 代码值);
            return;
        }

        点位项.含义 = 含义;
        _日志.LogDebug("已更新 {Code} 含义为 \"{Meaning}\"", 代码值, 含义);
    }

    /// <inheritdoc />
    public async Task 异步保存(CancellationToken 取消令牌 = default)
    {
        // R5：写操作在服务层二次校验。异步保存 会整体改写 io-points.json
        // （含"含义"字段），与 异步新增一栏 同为管理员专有（权限.编辑IO点位）。
        // 此前只校验了新增一栏，直接调保存即可绕过——界面门槛不是边界。
        _权限.要求(权限.编辑IO点位);

        try
        {
            var JSON文本 = JsonSerializer.Serialize(表格, JSON配置.写入);
            await 原子文件.异步写入全部文本(配置路径.IO点位集合, JSON文本, 取消令牌).ConfigureAwait(false);
            _日志.LogInformation("IO 点位表已保存到 {Path}", 配置路径.IO点位集合);
        }
        catch (Exception 异常)
        {
            _日志.LogError(异常, "保存 IO 点位表失败");
            throw;
        }
    }

    /// <inheritdoc />
    public Task 异步重载(CancellationToken 取消令牌 = default)
    {
        加载();
        return Task.CompletedTask;
    }

    // ── 新增一栏（v2.1）────────────────────────────────────────────────
    //
    // 汇川 X / Y 的编址上限。这个数与 汇川地址解析器 里软元件规格的 最大序号
    // 是同一个事实的两份描述（X 基址 0xF800、Y 基址 0xFC00，序号上限 1023，
    // 即八进制 1777）。解析器没有把这个数暴露成公共常量，这里只能各自持有一份；
    // 若要改，两处必须一起改 —— 超限的点会在轮询编译时被静默跳过，很难查。
    private const int 最大八进制序号 = 1023;

    /// <inheritdoc />
    public string 预览下一栏(string 前缀)
    {
        var (侧列表, 前缀规格) = 取侧(前缀);
        var 起点 = 计算下一栏起点(侧列表);

        return $"{前缀规格}{Convert.ToString(起点, 8)} — " +
               $"{前缀规格}{Convert.ToString(起点 + 分组大小() - 1, 8)}";
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IO点位>> 异步新增一栏(string 前缀, CancellationToken 取消令牌 = default)
    {
        // 权限先于一切：越权调用不应留下任何状态，也不该看到参数校验的细节。
        // 新增一栏会改写 io-points.json 并改变采集范围，属管理员专有（权限.编辑IO点位）。
        _权限.要求(权限.编辑IO点位);

        var (侧列表, 前缀规格) = 取侧(前缀);
        var 每组点数 = 分组大小();
        var 起点 = 计算下一栏起点(侧列表);

        var 新增列表 = new List<IO点位>(每组点数);
        var 既有编号 = 侧列表
            .Select(点位项 => 点位项.代码)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var 序号 = 起点; 序号 < 起点 + 每组点数; 序号++)
        {
            if (序号 > 最大八进制序号)
            {
                // 先整栏校验再写入：不做"加到一半失败"的部分写入，
                // 否则配置里会留下一截半栏，界面和轮询各看到一半。
                throw new InvalidOperationException(
                    $"{前缀规格} 区编号已到上限（八进制 {Convert.ToString(最大八进制序号, 8)}），无法再新增一栏。");
            }

            var 代码值 = 前缀规格 + Convert.ToString(序号, 8);

            if (!既有编号.Add(代码值))
            {
                throw new InvalidOperationException($"点位 {代码值} 已存在，无法新增。");
            }

            新增列表.Add(new IO点位 { 代码 = 代码值, 含义 = string.Empty, 启用 = true });
        }

        侧列表.AddRange(新增列表);

        // 新点也要能被 查找点位 反查到（轮询热路径走索引）
        重建点位索引();

        // 元数据里的数量字段虽然目前只有 JSON 记录作用，也应保持一致，
        // 否则现场工程师对照 config 会看到两套对不上的数字。
        if (是输入侧(前缀规格))
        {
            表格.元.输入数量 = 侧列表.Count;
        }
        else
        {
            表格.元.输出数量 = 侧列表.Count;
        }

        await 异步保存(取消令牌).ConfigureAwait(false);

        _日志.LogInformation("已新增 {Prefix} 区一栏 {First} — {Last}（{Count} 点），点位总数 {Total}",
            前缀规格, 新增列表[0].代码, 新增列表[^1].代码, 新增列表.Count, 侧列表.Count);

        return 新增列表;
    }

    /// <summary>取某一侧的点位列表与规范前缀（大写 X / Y）。</summary>
    private (List<IO点位> 点位列表, string 前缀规格) 取侧(string 前缀) =>
        是输入侧(前缀) ? (表格.输入, "X") : (表格.输出, "Y");

    private static bool 是输入侧(string 前缀) =>
        string.Equals(前缀, "X", StringComparison.OrdinalIgnoreCase);

    private int 分组大小() =>
        表格.元.分组大小 > 0 ? 表格.元.分组大小 : 8;

    /// <summary>
    /// 下一栏的起始序号：取该侧最大的八进制序号，向上取整到下一个分组边界。
    /// <para>空表从 0 开始；已有 X0–X27（最大序号 23）则下一栏起点为 24，即八进制 30。</para>
    /// </summary>
    private int 计算下一栏起点(IReadOnlyList<IO点位> 点位列表)
    {
        var 每组点数 = 分组大小();
        var 最大序号 = -1;

        foreach (var 点位项 in 点位列表)
        {
            var 数字列表 = new string(点位项.代码.SkipWhile(char.IsLetter).ToArray());
            var 序号值 = 手工解析八进制(数字列表, 点位项.代码);

            if (序号值 > 最大序号)
            {
                最大序号 = 序号值;
            }
        }

        if (最大序号 < 0)
        {
            return 0;
        }

        return (最大序号 / 每组点数 + 1) * 每组点数;
    }

    private static int 手工解析八进制(string 数字列表, string 代码值)
    {
        var 值 = 0;

        foreach (var 通道 in 数字列表)
        {
            var 数字项 = 通道 - '0';

            if (数字项 is < 0 or > 7)
            {
                throw new InvalidOperationException(
                    $"点位 {代码值} 的编号「{数字列表}」不是合法的八进制，无法计算下一栏起点。");
            }

            值 = 值 * 8 + 数字项;
        }

        return 值;
    }

    /// <summary>查找 IO 点定义。</summary>
    public IO点位? 查找点位(string 代码值) =>
        _点位索引.TryGetValue(代码值, out var 点位项) ? 点位项 : null;

    /// <summary>把点位按八进制分组（每 8 点一组），便于界面按组排布。</summary>
    public static IEnumerable<IGrouping<string, IO点位>> 按八进制分组(IReadOnlyList<IO点位> 点位列表)
    {
        // X0-X7、X10-X17……按“前缀 + 高位数字”分组
        return 点位列表.GroupBy(点位条目 =>
        {
            var 数字列表 = new string(点位条目.代码.SkipWhile(char.IsLetter).ToArray());
            if (数字列表.Length <= 1)
            {
                return "0";
            }

            return 数字列表[..^1];
        });
    }

    /// <summary>
    /// 整体重建代码 → 点位索引（加载 / 新增一栏时调用）。
    /// <para>先加输入侧再加输出侧（TryAdd 保持输入侧优先，与旧版
    /// FirstOrDefault 先查输入的语义一致）；整表替换是原子引用赋值，
    /// 轮询线程无需加锁。</para>
    /// </summary>
    private void 重建点位索引()
    {
        var 索引 = new Dictionary<string, IO点位>(
            表格.输入.Count + 表格.输出.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var 点位项 in 表格.输入.Concat(表格.输出))
        {
            索引.TryAdd(点位项.代码, 点位项);
        }

        _点位索引 = 索引;
    }

    private void 加载()
    {
        var 结果值 = 配置文件加载器.加载JSON<IO点位表格>(
            配置路径.IO点位集合, JSON配置.读取, _日志);

        _健康.上报("IO 点位表", 结果值);

        if (结果值.值 is null)
        {
            return;
        }

        表格 = 结果值.值;
        _状态.Clear();
        重建点位索引();

        if (结果值.加载结果 is 加载结果分类.已加载)
        {
            _日志.LogInformation("已加载 IO 点位表：X {InputCount} 点、Y {OutputCount} 点（机型 {Model}）",
                表格.输入.Count, 表格.输出.Count, 表格.元.PLC模型);
        }
    }
}
