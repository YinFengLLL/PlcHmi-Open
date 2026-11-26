using System.Text.Json;
using Microsoft.Extensions.Logging;
using 上位机.业务.配置;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// 报警定义服务。加载与保存 <c>config/alarms.json</c>。
///
/// <para><b>报警地址只允许落在这两段（需求方划定）：</b></para>
/// <list type="bullet">
///   <item><b>D550 – D554</b> 的位：如 <c>D550.1</c>、<c>D554.2</c>。5 个字 × 16 位 = 80 个报警位。</item>
///   <item><b>M500 – M600</b>：101 个位地址，每个地址一条报警。</item>
/// </list>
///
/// <para>管理员自定义时也只能选这段区域，且<b>最多 100 条</b>报警，每条可单独启用 / 停用。</para>
/// </summary>
public sealed class 报警定义服务 : 报警定义服务接口
{
    /// <summary>报警可用的 D 区范围（含）。</summary>
    public const int 报警D下限 = 550;

    /// <summary>报警可用的 D 区上限（含）。</summary>
    public const int 报警D上限 = 554;

    /// <summary>报警可用的 M 区范围（含）。</summary>
    public const int 报警M下限 = 500;

    /// <summary>报警可用的 M 区上限（含）。</summary>
    public const int 报警M上限 = 600;

    /// <summary>报警条数上限。</summary>
    public const int 最大报警数量 = 100;

    private readonly 地址解析器接口 _解析器;
    private readonly ILogger<报警定义服务> _日志;
    private readonly 权限守卫接口 _权限;
    private readonly 配置健康接口? _健康;
    private readonly List<报警定义> _定义 = new();

    public 报警定义服务(
        地址解析器接口 解析器, ILogger<报警定义服务> 日志,
        权限守卫接口 权限, 配置健康接口? 健康 = null)
    {
        _解析器 = 解析器;
        _日志 = 日志;
        _权限 = 权限;
        _健康 = 健康;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 返回<b>快照</b>而不是内部列表引用：报警扫描服务每 500ms 在后台线程枚举它，
    /// 而界面线程会同时增删改；此前交出内部 List，两个线程一撞就是
    /// <c>Collection was modified</c>——异常被扫描循环吞掉，表现为"改定义的瞬间
    /// 一整轮扫描静默丢失"。快照同时堵住"直接 Add 后调保存"绕过权限的路径。
    /// </remarks>
    public IReadOnlyList<报警定义> 定义集合 => _定义.ToArray();

    /// <inheritdoc />
    public event EventHandler? 定义已变更;

    // ── 加载与保存 ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task 异步初始化(CancellationToken 取消令牌 = default)
    {
        var 文件路径 = 配置路径.报警集合;

        var 结果值 = await 配置文件加载器
            .异步加载JSON<报警配置>(文件路径, JSON配置.读取, _日志, 取消令牌)
            .ConfigureAwait(false);

        // 报警表损坏是**安全级**降级：界面一切正常，但每一个故障位都被无视。
        // 必须登记出去，让操作员看见"报警功能不可信"，而不是安静地显示"无报警"。
        _健康.上报("报警配置", 结果值);

        if (结果值.加载结果 is 加载结果分类.缺失)
        {
            _日志.LogInformation("未找到报警配置，写入一份默认配置：{Path}", 文件路径);

            _定义.Clear();
            _定义.AddRange(创建默认值());

            // 首次运行写默认配置：启动阶段尚无登录身份，走核心保存
            await 异步保存核心(取消令牌).ConfigureAwait(false);
            return;
        }

        _定义.Clear();
        _定义.AddRange(结果值.值?.报警集合 ?? new List<报警定义>());

        if (结果值.加载结果 is 加载结果分类.已加载)
        {
            _日志.LogInformation("已加载 {Count} 条报警定义", _定义.Count);
        }

        定义已变更?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public async Task 异步保存(CancellationToken 取消令牌 = default)
    {
        // 保存即"把当前定义整体落盘"，与新增/修改/移除同权限：
        // 否则直接改内部列表再调保存就能绕过校验（界面门槛见 可编辑报警）
        _权限.要求(权限.编辑绑定地址);

        await 异步保存核心(取消令牌).ConfigureAwait(false);
        定义已变更?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>落盘但不通知界面（初始化 / 已有校验的写路径内部使用）。</summary>
    private async Task 异步保存核心(CancellationToken 取消令牌)
    {
        var 配置对象 = new 报警配置 { 报警集合 = _定义.Select(条目项 => 条目项.克隆()).ToList() };
        var JSON文本 = JsonSerializer.Serialize(配置对象, JSON配置.写入);

        try
        {
            await 原子文件.异步写入全部文本(配置路径.报警集合, JSON文本, 取消令牌).ConfigureAwait(false);
            _日志.LogInformation("报警配置已保存（{Count} 条）：{Path}", _定义.Count, 配置路径.报警集合);
        }
        catch (Exception 异常)
        {
            _日志.LogError(异常, "保存报警配置失败：{Path}", 配置路径.报警集合);
            throw;
        }
    }

    // ── 校验 ────────────────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// <b>占用检查为什么不能靠引用排除自身：</b>编辑对话框操作的是 <see cref="报警定义.克隆"/> 副本，
    /// <c>ReferenceEquals(d, definition)</c> 对副本永远不成立——旧实现因此把「正在编辑的那条」
    /// 也当成冲突来源，管理员只要改个名字就会弹「已被占用」（2026-09-11 需求方反馈）。
    /// 现按 <paramref name="是否更新"/> 区分：编辑时编号被对话框锁定，同 Id 的条目就是自身，直接跳过。
    /// </remarks>
    public 报警定义校验 校验(报警定义 定义项, bool 是否更新 = false)
    {
        var 错误列表 = new List<string>();

        if (string.IsNullOrWhiteSpace(定义项.标识))
        {
            错误列表.Add("报警编号不能为空。");
        }
        else if (!是否更新 && _定义.Any(条目项 =>
                     string.Equals(条目项.标识, 定义项.标识, StringComparison.OrdinalIgnoreCase)))
        {
            错误列表.Add($"报警编号「{定义项.标识}」已被占用。");
        }

        if (string.IsNullOrWhiteSpace(定义项.消息))
        {
            错误列表.Add("报警文本不能为空。");
        }

        if (定义项.位 is < -1 or > 15)
        {
            错误列表.Add("位号必须填 0–15，或填 -1。");
        }

        校验地址(定义项, 错误列表);

        // 新增时检查总数上限（更新已有条目不占新名额）
        if (!是否更新 && _定义.Count >= 最大报警数量)
        {
            错误列表.Add($"报警数量已达上限 {最大报警数量} 条，请先删除或停用不用的报警。");
        }

        return new 报警定义校验 { 是否有效 = 错误列表.Count == 0, 错误集合 = 错误列表 };
    }

    /// <summary>
    /// 校验报警地址是否落在允许区域：
    /// D550–D554 的位，或 M500–M600。
    /// </summary>
    private void 校验地址(报警定义 定义项, List<string> 错误列表)
    {
        if (string.IsNullOrWhiteSpace(定义项.地址))
        {
            错误列表.Add("读取地址不能为空。");
            return;
        }

        if (!_解析器.尝试解析(定义项.地址, out var PLC地址) || PLC地址 is null)
        {
            错误列表.Add($"「{定义项.地址}」不是有效的软元件地址。");
            return;
        }

        var 序号 = PLC地址.逻辑序号;

        switch (PLC地址.元素)
        {
            case 软元素.D when 序号 >= 报警D下限 && 序号 <= 报警D上限:
                // D 区一个字有 16 位，必须指定位号才有意义
                if (!定义项.是否位模式)
                {
                    错误列表.Add($"D 区报警必须指定位号（如 D{序号}.1），当前未指定位号。");
                }

                break;

            case 软元素.M when 序号 >= 报警M下限 && 序号 <= 报警M上限:
                break;

            default:
                错误列表.Add(
                    $"报警地址必须落在 D{报警D下限}–D{报警D上限}（指定位号，如 D550.1）"
                    + $"或 M{报警M下限}–M{报警M上限} 内，当前填的是 {PLC地址.元素}{序号}。");
                break;
        }
    }

    // ── 增删改 ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    public void 添加(报警定义 定义项)
    {
        ArgumentNullException.ThrowIfNull(定义项);

        // 改报警定义等于改"设备什么情况下会报警"。删掉一条真实的报警，
        // 操作员就再也看不到那个故障 —— 管理员专有，且必须在此处兜住。
        _权限.要求(权限.编辑绑定地址);

        if (string.IsNullOrWhiteSpace(定义项.标识))
        {
            定义项.标识 = 新标识();
        }

        _定义.Add(定义项.克隆());

        _日志.LogInformation("已新增报警定义：{Id} {Message}", 定义项.标识, 定义项.消息);
        定义已变更?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void 更新(报警定义 定义项)
    {
        ArgumentNullException.ThrowIfNull(定义项);

        _权限.要求(权限.编辑绑定地址);

        var 序号 = _定义.FindIndex(条目项 =>
            string.Equals(条目项.标识, 定义项.标识, StringComparison.OrdinalIgnoreCase));

        if (序号 < 0)
        {
            throw new InvalidOperationException($"找不到编号为「{定义项.标识}」的报警定义。");
        }

        _定义[序号] = 定义项.克隆();

        _日志.LogInformation("已更新报警定义：{Id} {Message}", 定义项.标识, 定义项.消息);
        定义已变更?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void 移除(string 标识)
    {
        _权限.要求(权限.编辑绑定地址);

        var 已移除 = _定义.RemoveAll(条目项 =>
            string.Equals(条目项.标识, 标识, StringComparison.OrdinalIgnoreCase));

        if (已移除 > 0)
        {
            _日志.LogInformation("已删除报警定义：{Id}", 标识);
            定义已变更?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public string 新标识()
    {
        for (var 条目 = 1; 条目 < 1000; 条目++)
        {
            var 候选 = $"A{条目:000}";

            if (_定义.All(条目项 => !string.Equals(条目项.标识, 候选, StringComparison.OrdinalIgnoreCase)))
            {
                return 候选;
            }
        }

        return $"A{Guid.NewGuid():N}"[..8].ToUpperInvariant();
    }

    /// <summary>首次运行时的默认报警（对应模拟器 M500 起的报警位）。</summary>
    private static IEnumerable<报警定义> 创建默认值() =>
    [
        new()
        {
            标识 = "A001", 等级 = 报警等级.紧急, 消息 = "设备急停被按下",
            地址 = "M500", 位 = -1, 建议 = "确认现场安全后复位设备",
        },
        new()
        {
            标识 = "A002", 等级 = 报警等级.警告, 消息 = "上料气缸超时未到位",
            地址 = "M501", 位 = -1, 建议 = "检查气缸磁性开关与气源压力",
        },
        new()
        {
            标识 = "A003", 等级 = 报警等级.警告, 消息 = "料仓余料不足",
            地址 = "M502", 位 = -1, 建议 = "及时补料",
        },
        new()
        {
            标识 = "A004", 等级 = 报警等级.紧急, 消息 = "安全门被打开",
            地址 = "M503", 位 = -1, 建议 = "确认无人后关闭安全门再复位",
        },
    ];
}
