using System.Text.Json;
using Microsoft.Extensions.Logging;
using 上位机.业务.配置;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// 手动页元件服务。加载与保存 <c>config/manual-widgets.json</c>。
///
/// <para><b>核心约束：元件只能绑定 M 区地址。</b>
/// 上位机只置位内部继电器，由 PLC 程序决定何时驱动 Y 输出——
/// 避免绕过 PLC 的联锁逻辑。绑定 Y 点的配置会在校验阶段被拒绝。</para>
/// </summary>
public sealed class 手动元件服务 : 手动元件服务接口
{
    private readonly 地址解析器接口 _解析器;
    private readonly ILogger<手动元件服务> _日志;
    private readonly 配置健康接口? _健康;
    private readonly 权限守卫接口 _权限;
    private readonly 变量表服务接口? _变量表;
    private readonly List<手动元件定义> _元件 = new();

    public 手动元件服务(
        地址解析器接口 解析器, ILogger<手动元件服务> 日志,
        权限守卫接口 权限, 配置健康接口? 健康 = null, 变量表服务接口? 变量表 = null)
    {
        _解析器 = 解析器;
        _日志 = 日志;
        _权限 = 权限;
        _健康 = 健康;
        _变量表 = 变量表;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 返回<b>快照</b>而不是内部列表引用：此前交出内部 List，
    /// 调用方可以直接 Add 后调保存绕过权限校验，也会在枚举时
    /// 撞上别处的增删（Collection was modified）。
    /// </remarks>
    public IReadOnlyList<手动元件定义> 元件集合 => _元件.ToArray();

    /// <inheritdoc />
    public event EventHandler? 元件已变更;

    /// <inheritdoc />
    public async Task 异步初始化(CancellationToken 取消令牌 = default)
    {
        var 文件路径 = 配置路径.手动元件;

        var 结果值 = await 配置文件加载器
            .异步加载JSON<手动元件配置>(文件路径, JSON配置.读取, _日志, 取消令牌)
            .ConfigureAwait(false);

        _健康.上报("手动页元件配置", 结果值);

        if (结果值.加载结果 is 加载结果分类.缺失)
        {
            _日志.LogInformation("未找到手动页元件配置，写入一份默认配置：{Path}", 文件路径);

            _元件.Clear();
            _元件.AddRange(创建默认值());
            排序();

            // 首次运行写默认配置：启动阶段尚无登录身份，走核心保存
            await 异步保存核心(取消令牌).ConfigureAwait(false);
            return;
        }

        _元件.Clear();
        _元件.AddRange(结果值.值?.元件集合 ?? new List<手动元件定义>());
        排序();
        应用变量绑定();

        if (结果值.加载结果 is 加载结果分类.已加载)
        {
            _日志.LogInformation("已加载 {Count} 个手动页元件", _元件.Count);
        }

        元件已变更?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 变量绑定回填：绑定了变量的元件用变量表中的软元件地址刷新地址字段。
    /// <para>变量失效时保留原地址并登记健康警告——禁止无声降级。</para>
    /// </summary>
    private void 应用变量绑定()
    {
        foreach (var 元件项 in _元件)
        {
            元件项.地址 = 变量绑定回填.解析地址(元件项.变量, _变量表, _解析器, 元件项.地址, out var 错误);

            if (错误.Length > 0)
            {
                _健康?.上报回退($"手动元件「{元件项.名称}」", 错误);
            }

            元件项.状态地址 = 变量绑定回填.解析地址(
                元件项.状态值变量, _变量表, _解析器, 元件项.状态地址, out 错误);

            if (错误.Length > 0)
            {
                _健康?.上报回退($"手动元件「{元件项.名称}」", 错误);
            }
        }
    }

    /// <inheritdoc />
    public async Task 异步保存(CancellationToken 取消令牌 = default)
    {
        // 保存即"把当前元件整体落盘"，与增删改同权限（管理员）：
        // 否则直接改内部列表再调保存就能绕过校验（R5）
        _权限.要求(权限.编辑手动元件);

        await 异步保存核心(取消令牌).ConfigureAwait(false);
        元件已变更?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>落盘但不通知界面（初始化 / 已有校验的写路径内部使用）。</summary>
    private async Task 异步保存核心(CancellationToken 取消令牌)
    {
        排序();

        var 配置对象 = new 手动元件配置 { 元件集合 = _元件.Select(元件条目 => 元件条目.克隆()).ToList() };
        var JSON文本 = JsonSerializer.Serialize(配置对象, JSON配置.写入);

        try
        {
            await 原子文件.异步写入全部文本(配置路径.手动元件, JSON文本, 取消令牌)
                .ConfigureAwait(false);

            _日志.LogInformation("手动页元件已保存（{Count} 个）：{Path}", _元件.Count, 配置路径.手动元件);
        }
        catch (Exception 异常)
        {
            _日志.LogError(异常, "保存手动页元件失败：{Path}", 配置路径.手动元件);
            throw;
        }
    }

    /// <inheritdoc />
    public 手动元件校验 校验(手动元件定义 元件项)
    {
        var 错误列表 = new List<string>();

        if (string.IsNullOrWhiteSpace(元件项.名称))
        {
            错误列表.Add("元件名称不能为空。");
        }

        switch (元件项.类型)
        {
            case 手动元件类型.气缸:
                // 单线圈气缸：一个地址，置位伸出、复位缩回
                校验中继(元件项.地址, "控制地址", required: true, 错误列表);
                break;

            case 手动元件类型.按钮:
                校验中继(元件项.地址, "触发地址", required: true, 错误列表);
                break;

            case 手动元件类型.指示灯:
                校验中继(元件项.状态地址, "监测地址", required: true, 错误列表);
                break;
        }

        return new 手动元件校验 { 是否有效 = 错误列表.Count == 0, 错误集合 = 错误列表 };
    }

    /// <inheritdoc />
    public void 添加(手动元件定义 元件项)
    {
        ArgumentNullException.ThrowIfNull(元件项);

        // 增删元件是管理员专有：元件定义决定"手动页上有什么按钮、按下去动哪个 M 点"，
        // 改错了等于给操作员一个会误动作的界面。
        _权限.要求(权限.编辑手动元件);

        if (string.IsNullOrWhiteSpace(元件项.标识))
        {
            元件项.标识 = 新标识(元件项.类型);
        }

        if (元件项.顺序 <= 0)
        {
            元件项.顺序 = _元件.Count == 0 ? 1 : _元件.Max(已有项 => 已有项.顺序) + 1;
        }

        _元件.Add(元件项.克隆());
        排序();

        _日志.LogInformation("已新增手动页元件：{Name}", 元件项.名称);
        元件已变更?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void 更新(手动元件定义 元件项)
    {
        ArgumentNullException.ThrowIfNull(元件项);

        _权限.要求(权限.编辑手动元件);

        var 序号 = _元件.FindIndex(待更新项 => string.Equals(待更新项.标识, 元件项.标识, StringComparison.OrdinalIgnoreCase));

        if (序号 < 0)
        {
            throw new InvalidOperationException($"找不到 Id 为「{元件项.标识}」的元件。");
        }

        _元件[序号] = 元件项.克隆();
        排序();

        _日志.LogInformation("已更新手动页元件：{Name}", 元件项.名称);
        元件已变更?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void 移除(string 标识)
    {
        _权限.要求(权限.编辑手动元件);

        var 已移除 = _元件.RemoveAll(元件条目 => string.Equals(元件条目.标识, 标识, StringComparison.OrdinalIgnoreCase));

        if (已移除 > 0)
        {
            _日志.LogInformation("已删除手动页元件：{Id}", 标识);
            元件已变更?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public string 新标识(手动元件类型 type)
    {
        var 前缀 = type switch
        {
            手动元件类型.气缸 => "cyl",
            手动元件类型.按钮 => "btn",
            _ => "lamp",
        };

        // 保证不与现有 Id 冲突
        for (var 条目 = 1; 条目 < 1000; 条目++)
        {
            var 候选 = $"{前缀}-{条目}";

            if (_元件.All(元件条目 => !string.Equals(元件条目.标识, 候选, StringComparison.OrdinalIgnoreCase)))
            {
                return 候选;
            }
        }

        return $"{前缀}-{Guid.NewGuid():N}"[..12];
    }

    // ── 内部 ────────────────────────────────────────────────────────────

    /// <summary>校验一个地址是否为 M 区。</summary>
    private void 校验中继(string 地址值, string 标签名, bool required, List<string> 错误列表)
    {
        if (string.IsNullOrWhiteSpace(地址值))
        {
            if (required)
            {
                错误列表.Add($"{标签名}不能为空。");
            }

            return;
        }

        if (!_解析器.尝试解析(地址值, out var PLC地址) || PLC地址 is null)
        {
            错误列表.Add($"{标签名}「{地址值}」不是有效的软元件地址。");
            return;
        }

        if (PLC地址.元素 != 软元素.M)
        {
            错误列表.Add($"{标签名}「{地址值}」必须是 M 区地址（当前解析为 {PLC地址.元素} 区）。"
                       + "上位机只允许操作内部继电器，由 PLC 程序决定何时驱动 Y 输出。");
        }
    }

    private void 排序() => _元件.Sort((甲, 乙) => 甲.顺序.CompareTo(乙.顺序));

    /// <summary>首次运行时的默认元件（单线圈气缸，顺序与旧版硬编码一致）。</summary>
    private static IEnumerable<手动元件定义> 创建默认值() =>
    [
        new()
        {
            标识 = "cyl-1", 类型 = 手动元件类型.气缸, 名称 = "上料气缸", 顺序 = 1,
            地址 = "M220",
        },
        new()
        {
            标识 = "cyl-2", 类型 = 手动元件类型.气缸, 名称 = "压装气缸", 顺序 = 2,
            地址 = "M221",
        },
        new()
        {
            标识 = "cyl-3", 类型 = 手动元件类型.气缸, 名称 = "顶升气缸", 顺序 = 3,
            地址 = "M222",
        },
    ];
}
