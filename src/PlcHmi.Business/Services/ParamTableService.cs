using System.Text.Json;
using Microsoft.Extensions.Logging;
using 上位机.业务.配置;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// 参数页自定义表格服务（延时表 / 按钮表）。
///
/// <para><b>地址约束：</b>延时表绑 D 区（数值）、按钮表绑 M 区（命令位）。
/// 按钮只操作内部继电器，由 PLC 决定何时驱动实际输出。</para>
/// </summary>
public sealed partial class 参数表格服务 : 参数表格服务接口
{
    /// <summary>延时表允许的软元件区。</summary>
    private static readonly 软元素[] 延迟元素 = [软元素.D];

    /// <summary>按钮表允许的软元件区。</summary>
    private static readonly 软元素[] 按钮元素 = [软元素.M];

    /// <summary>
    /// 按钮表允许的 M 区地址范围（含两端）。
    ///
    /// <para>M5000 以下已被系统占用（M200–M203 运行命令、M210 手动使能、M220–M225 气缸、
    /// M500–M600 报警位），所以自定义按钮统一放在 <b>M5000–M5099</b>，
    /// 与系统点位彻底隔开，避免管理员加一个开关就撞掉某个功能。</para>
    /// </summary>
    public const int 按钮地址最小 = 5000;

    /// <inheritdoc cref="按钮地址最小" />
    public const int 按钮地址最大 = 5099;

    /// <summary>
    /// 延时表允许的 D 区地址范围（含两端）。
    ///
    /// <para><b>地址规划（2026-09-12 需求方确认）</b>：D1000–D1099 为<b>延时表专属区</b>；
    /// D1100–D1500 为上位机预留段（用途待定）。原先把 D1000–D1500 记为
    /// 「PLC 公共区、禁止占用」的说法已作废——PLC 侧已释放整段给上位机使用。</para>
    ///
    /// <para>把延时锁进专属区的原因与按钮表相同：延时条目可以由管理员增删改地址，
    /// 不设边界就可能撞掉别的功能（轴速度参数区、示教点表区都在同一片 D 区里）。</para>
    ///
    /// <para><b>2026-10-16：</b>延时改为 32 位（DINT，占 2 字），故上限收到 <b>1098</b>——
    /// 否则最后一条会跨到 D1100，侵入上位机预留段。专属区因此可放 50 条延时。</para>
    /// </summary>
    public const int 延迟地址最小 = 1000;

    /// <inheritdoc cref="延迟地址最小" />
    public const int 延迟地址最大 = 1098;

    private readonly 地址解析器接口 _解析器;
    private readonly 标签写入器 _写入器;
    private readonly ILogger<参数表格服务> _日志;

    private readonly List<参数条目定义> _延迟 = new();
    private readonly List<参数条目定义> _按钮 = new();
    private readonly 配置健康接口? _健康;
    private readonly 变量表服务接口? _变量表;

    /// <summary>服务层权限守卫。增删改条目属管理员专有（改的是"表结构"，不是数值）。</summary>
    private readonly 权限守卫接口 _权限;

    /// <summary>防止下一次保存时把"加载期间"的默认值又写一遍。</summary>
    private bool _有未保存改动;

    public 参数表格服务(
        地址解析器接口 解析器, 标签写入器 写入器, ILogger<参数表格服务> 日志,
        权限守卫接口 权限, 配置健康接口? 健康 = null, 变量表服务接口? 变量表 = null)
    {
        _解析器 = 解析器;
        _写入器 = 写入器;
        _日志 = 日志;
        _权限 = 权限;
        _健康 = 健康;
        _变量表 = 变量表;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 返回<b>快照</b>而不是内部列表引用：此前交出内部 List，
    /// 调用方既可以直接 Add 绕过权限校验，也会在枚举时撞上 UI 线程的增删
    /// （Collection was modified）。
    /// </remarks>
    public IReadOnlyList<参数条目定义> 延迟集合 => _延迟.ToArray();

    /// <inheritdoc cref="延迟集合" />
    public IReadOnlyList<参数条目定义> 按钮集合 => _按钮.ToArray();

    /// <inheritdoc />
    public event EventHandler? 条目已变更;

    /// <inheritdoc />
    public IReadOnlyList<参数条目定义> 获取(参数表格类别 类别) =>
        类别 == 参数表格类别.延迟 ? _延迟.ToArray() : _按钮.ToArray();

    // ── 加载与保存 ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task 异步初始化(CancellationToken 取消令牌 = default)
    {
        var 文件路径 = 配置路径.参数表格;

        var 结果值 = await 配置文件加载器
            .异步加载JSON<参数表格配置>(文件路径, JSON配置.读取, _日志, 取消令牌)
            .ConfigureAwait(false);

        _健康.上报("参数表（延时/按钮）", 结果值);

        if (结果值.加载结果 is 加载结果分类.缺失)
        {
            _日志.LogInformation("未找到参数表配置，写入一份默认配置：{Path}", 文件路径);

            _延迟.Clear();
            _延迟.AddRange(参数表格默认值.创建延迟(延迟地址最小));

            _按钮.Clear();
            _按钮.AddRange(参数表格默认值.创建按钮());

            // 首次运行写默认配置：走核心保存（启动阶段尚无登录身份，
            // 不能因为有权限校验而让初始化失败）
            await 异步保存核心(取消令牌).ConfigureAwait(false);
            return;
        }

        _延迟.Clear();
        _延迟.AddRange(结果值.值?.延迟集合 ?? new List<参数条目定义>());

        _按钮.Clear();
        _按钮.AddRange(结果值.值?.按钮集合 ?? new List<参数条目定义>());

        排序全部();
        应用变量绑定();
        检查延时地址重叠();

        if (结果值.加载结果 is 加载结果分类.已加载)
        {
            _日志.LogInformation("已加载参数表：延时 {D} 条、按钮 {B} 条", _延迟.Count, _按钮.Count);
        }

        条目已变更?.Invoke(this, EventArgs.Empty);
    }

    // 延时地址重叠的启动期检查见 ParamTableService.Validate.cs（同为校验职责）。

    /// <summary>变量绑定回填：绑定了变量的条目用变量表中的软元件地址刷新地址字段。</summary>
    private void 应用变量绑定()
    {
        变量绑定回填.应用条目绑定(_延迟, "延时", _变量表, _解析器, _健康);
        变量绑定回填.应用条目绑定(_按钮, "按钮", _变量表, _解析器, _健康);
    }

    /// <inheritdoc />
    public async Task 异步保存(CancellationToken 取消令牌 = default)
    {
        // 保存的是"表结构"（地址 / 范围 / 条目集合），与增删改同权限：
        // 界面隐藏按钮只是防误触，边界在服务层（R5）
        _权限.要求(权限.编辑绑定地址);

        await 异步保存核心(取消令牌).ConfigureAwait(false);
        条目已变更?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 落盘但<b>不</b>通知界面。
    /// <para>实时下发延时后由 <see cref="异步写入延迟"/> 调用：
    /// 若走 <see cref="异步保存"/>，<c>ItemsChanged</c> 会重建整张表格，
    /// 打断用户在其他行正在进行的编辑。</para>
    /// </summary>
    private async Task 异步保存核心(CancellationToken 取消令牌)
    {
        排序全部();

        var 配置对象 = new 参数表格配置
        {
            延迟集合 = _延迟.Select(条目项 => 条目项.克隆()).ToList(),
            按钮集合 = _按钮.Select(乙 => 乙.克隆()).ToList(),
        };

        try
        {
            var JSON文本 = JsonSerializer.Serialize(配置对象, JSON配置.写入);
            await 原子文件.异步写入全部文本(配置路径.参数表格, JSON文本, 取消令牌).ConfigureAwait(false);

            _有未保存改动 = false;
            _日志.LogInformation("参数表已保存：延时 {D} 条、按钮 {B} 条", _延迟.Count, _按钮.Count);
        }
        catch (Exception 异常)
        {
            _日志.LogError(异常, "保存参数表失败：{Path}", 配置路径.参数表格);
            throw;
        }
    }

    // ── 校验见 ParamTableService.Validate.cs（partial）──────────────────

    // ── 增删改 ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    public void 添加(参数表格类别 类别, 参数条目定义 条目)
    {
        ArgumentNullException.ThrowIfNull(条目);

        // 角色校验在参数校验之前：被拒绝的调用不留任何状态痕迹
        _权限.要求(权限.编辑绑定地址);

        var 列表 = 变更(类别);

        if (string.IsNullOrWhiteSpace(条目.标识))
        {
            条目.标识 = 新标识(类别);
        }

        if (条目.顺序 <= 0)
        {
            条目.顺序 = 列表.Count == 0 ? 1 : 列表.Max(坐标X => 坐标X.顺序) + 1;
        }

        列表.Add(条目.克隆());
        排序全部();
        _有未保存改动 = true;

        _日志.LogInformation("已新增{Kind}条目：{Name}", 类别 == 参数表格类别.延迟 ? "延时" : "按钮", 条目.名称);
        条目已变更?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void 更新(参数表格类别 类别, 参数条目定义 条目)
    {
        ArgumentNullException.ThrowIfNull(条目);

        _权限.要求(权限.编辑绑定地址);

        var 列表 = 变更(类别);
        var 序号 = 列表.FindIndex(坐标X => string.Equals(坐标X.标识, 条目.标识, StringComparison.OrdinalIgnoreCase));

        if (序号 < 0)
        {
            throw new InvalidOperationException($"找不到编号为「{条目.标识}」的条目。");
        }

        列表[序号] = 条目.克隆();
        排序全部();
        _有未保存改动 = true;

        _日志.LogInformation("已更新{Kind}条目：{Name}", 类别 == 参数表格类别.延迟 ? "延时" : "按钮", 条目.名称);
        条目已变更?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void 移除(参数表格类别 类别, string 标识)
    {
        _权限.要求(权限.编辑绑定地址);

        var 已移除 = 变更(类别).RemoveAll(坐标X =>
            string.Equals(坐标X.标识, 标识, StringComparison.OrdinalIgnoreCase));

        if (已移除 > 0)
        {
            _有未保存改动 = true;
            _日志.LogInformation("已删除参数表条目：{Id}", 标识);
            条目已变更?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public string 新标识(参数表格类别 类别)
    {
        var 前缀 = 类别 == 参数表格类别.延迟 ? "delay" : "btn";
        var 列表 = 获取(类别);

        for (var 条目 = 1; 条目 < 1000; 条目++)
        {
            var 候选 = $"{前缀}-{条目}";

            if (列表.All(坐标X => !string.Equals(坐标X.标识, 候选, StringComparison.OrdinalIgnoreCase)))
            {
                return 候选;
            }
        }

        return $"{前缀}-{Guid.NewGuid():N}"[..12];
    }

    // ── 按钮置位 / 复位 ─────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task 异步设置按钮(
        参数条目定义 条目, bool on, CancellationToken 取消令牌 = default)
    {
        ArgumentNullException.ThrowIfNull(条目);

        // 置位/复位改的是设备状态，工程师及以上即可（不是改表结构）
        _权限.要求(权限.编辑参数值列表);

        // 服务层再校验一次：即使配置文件被手工改错，也不会写错区。
        // 界面上编辑器只让填 M 区不够——手工编辑 JSON 同样要被拦住。
        // isUpdate: true —— 按钮是已存在的条目，同 Id 就是它自己，不能报「已被占用」。
        var 校验项 = 校验(参数表格类别.按钮, 条目, 是否更新: true);

        if (!校验项.是否有效)
        {
            throw new InvalidOperationException(校验项.摘要);
        }

        await _写入器.异步写入线圈地址(条目.地址, on, 取消令牌).ConfigureAwait(false);

        _日志.LogInformation("按钮「{Name}」({Address}) 已{Action}",
            条目.名称, 条目.地址, on ? "置位" : "复位");
    }

    /// <inheritdoc />
    public async Task 异步写入延迟(
        参数条目定义 条目, CancellationToken 取消令牌 = default)
    {
        ArgumentNullException.ThrowIfNull(条目);

        _权限.要求(权限.编辑参数值列表);

        // 服务层再校验一次：即使配置文件被手工改错，也不会写错区、写出范围。
        // isUpdate: true —— 延时是已存在的条目，同 Id 就是它自己，不能报「已被占用」。
        var 校验项 = 校验(参数表格类别.延迟, 条目, 是否更新: true);

        if (!校验项.是否有效)
        {
            throw new InvalidOperationException(校验项.摘要);
        }

        // 界面与配置按秒，PLC 存 <b>32 位整数毫秒（DINT）</b>：0.500 S → 500（2026-10-16 现场确认，
        // 此前是 16 位毫秒，上限只有 65.535 S，等几十秒的工位延时根本不够用）。
        // 必须先在 double 域判定范围再转 int：条目.值 是 double 且校验只保证
        // 最小 < 值 < 最大（最大本身可以是任意大的 double），先乘后转会让
        // 超大值在 unchecked 下回绕成小正数，静默写错延时
        var 毫秒值 = 条目.值 * 1000;

        if (毫秒值 is < 0 or > int.MaxValue)
        {
            throw new InvalidOperationException(
                $"延时 {条目.值:0.###} S 换算为 {毫秒值:0} ms，超出 32 位整数（DINT）范围（0–{int.MaxValue}）。");
        }

        var 毫秒 = (int)Math.Round(毫秒值, MidpointRounding.AwayFromZero);

        await _写入器.异步写入寄存器地址32(条目.地址, 毫秒, 取消令牌)
            .ConfigureAwait(false);

        // 静默落盘：保证「PLC 里的值 = 配置文件里的值」，
        // 否则断电重启后配置退回旧值，与 PLC 不一致。
        await 异步保存核心(取消令牌).ConfigureAwait(false);

        _日志.LogInformation("延时「{Name}」({Address}) 已实时下发：{Seconds:0.###} S = {Millis} ms",
            条目.名称, 条目.地址, 条目.值, 毫秒);
    }

    // ── 内部 ────────────────────────────────────────────────────────────

    private List<参数条目定义> 变更(参数表格类别 类别) =>
        类别 == 参数表格类别.延迟 ? _延迟 : _按钮;

    private void 排序全部()
    {
        _延迟.Sort((甲, 乙) => 甲.顺序.CompareTo(乙.顺序));
        _按钮.Sort((甲, 乙) => 甲.顺序.CompareTo(乙.顺序));
    }

    /// <summary>是否有未保存的改动（供界面提示）。</summary>
    public bool 有未保存变更 => _有未保存改动;
}
