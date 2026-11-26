using Microsoft.Extensions.Logging;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// 手动控制服务。手动页元件动作与四轴点动。
///
/// <para><b>安全联锁：</b>所有手动动作都要求先开启手动使能。未使能时直接抛异常。</para>
///
/// <para><b>元件地址硬约束：</b>手动页元件只能绑定 <b>M 区（辅助继电器）</b>。
/// 上位机只置位内部继电器，由 PLC 程序决定何时驱动 Y 输出——
/// 这样上位机不会绕过 PLC 的联锁逻辑直接动作执行机构。
/// 传入非 M 区地址会被拒绝（服务层拦截，不只靠界面限制）。</para>
/// </summary>
public sealed partial class 手动控件服务 : 手动控件服务接口
{
    /// <summary>
    /// 支持的轴名称。老四轴（X/Y/Z/R）的点动方向写在各轴参数区的 D 字内位
    /// （如 R 轴 D1510.2/.3）；点胶锁付机新增的 ZZ / 点胶 X / 点胶 Z 走
    /// <c>recipe-config.json</c> 的 jogButtons（配置驱动的 M 区线圈）。
    /// </summary>
    private static readonly string[] 支持轴 = ["X", "Y", "Z", "R", "ZZ", "GlueX", "GlueZ"];

    /// <summary>按钮脉冲的默认宽度。</summary>
    private static readonly TimeSpan 默认脉冲宽度 = TimeSpan.FromMilliseconds(200);

    private readonly 标签写入器 _写入器;
    private readonly Modbus客户端接口 _客户端;
    private readonly 地址解析器接口 _解析器;
    private readonly 配方配置 _配置;
    private readonly 权限守卫接口 _权限;
    private readonly ILogger<手动控件服务> _日志;

    /// <summary>
    /// 权限守卫是<b>必填</b>参数，刻意不给默认值（审查标准 R5）：
    /// 手动动作会真实驱动执行机构，服务层必须自成边界。
    /// 测试若不需要覆盖权限分支，请显式传入 <see cref="允许全部权限守卫"/>。
    /// </summary>
    public 手动控件服务(
        标签写入器 写入器,
        Modbus客户端接口 客户端,
        地址解析器接口 解析器,
        配方配置 配置对象,
        权限守卫接口 权限,
        ILogger<手动控件服务> 日志)
    {
        _写入器 = 写入器;
        _客户端 = 客户端;
        _解析器 = 解析器;
        _配置 = 配置对象;
        _权限 = 权限;
        _日志 = 日志;
    }

    /// <inheritdoc />
    public bool 是否启用 { get; private set; }

    /// <inheritdoc />
    public event EventHandler<bool>? 启用已变更;

    /// <inheritdoc />
    public void 设置启用(bool 启用)
    {
        // 关闭使能是安全方向，任何情况下都放行；开启需要操作页权限。
        // 手动页与示教页都要求 视图全部页面（工程师及以上），本校验不改变现有可用性，
        // 只是把"界面隐藏按钮"升级为服务层边界（R5）。
        if (启用)
        {
            _权限.要求(权限.视图全部页面);
        }

        if (是否启用 == 启用)
        {
            return;   // 无变化就不通知：避免订阅方回写属性造成的来回触发
        }

        是否启用 = 启用;
        _日志.LogInformation("手动使能：{State}", 启用 ? "开启" : "关闭");

        启用已变更?.Invoke(this, 启用);
    }

    /// <summary>
    /// 把手动使能状态同步到 PLC（M666 线圈，见 <see cref="手动使能寄存器"/>）。
    ///
    /// <para><b>按常量写、不按点位名写</b>：现场口径是固定信号 M666，
    /// 而 <c>tags.json</c> 里的 <c>ManualEnable</c> 点位在老配置里可能还指向别的地址——
    /// 按名写会跟着旧配置写到别的位上（可能撞上 PLC 的其它用途），按常量写则现场必达。</para>
    ///
    /// <para>由 <see cref="手动使能保持服务"/> 周期调用，实现"打开时持续置位 / 关闭时持续复位"；
    /// 开关切换时也会立即调用一次，界面手感不受周期影响。</para>
    /// </summary>
    public async Task 异步同步启用(CancellationToken 取消令牌 = default)
    {
        // 与 设置启用 同口径：仅"开启方向"需要权限，关闭永远放行
        if (是否启用)
        {
            _权限.要求(权限.视图全部页面);
        }

        if (!_客户端.是否已连接)
        {
            // 未连接不抛异常：使能开关是界面操作，不能因为 PLC 抖动把人挡在外面
            _日志.LogDebug("PLC 未连接，手动使能状态（{State}）未写入 M{Coil}",
                是否启用 ? "开启" : "关闭", 手动使能寄存器.线圈);
            return;
        }

        await _客户端.异步写入单个线圈(手动使能寄存器.线圈, 是否启用, 取消令牌).ConfigureAwait(false);
    }

    // ── 手动页元件（按地址操作）：见 ManualControlService.Widgets.cs（partial）──

    // ── 四轴点动 ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task 异步点动(string 轴号, int 方向, CancellationToken 取消令牌 = default)
    {
        _权限.要求(权限.视图全部页面);
        确保启用();

        var 已规范化 = 规范化轴(轴号);

        if (方向 == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(方向), 方向, "点动方向必须为 1（正向）或 -1（负向）。");
        }

        var 正转标签 = 轴标签名.点动正转(已规范化);
        var 反转标签 = 轴标签名.点动反转(已规范化);
        var 目标 = 方向 > 0 ? 正转标签 : 反转标签;
        var 相反 = 方向 > 0 ? 反转标签 : 正转标签;

        _日志.LogInformation("{Axis} 轴{Jog}点动", 已规范化, 方向 > 0 ? "正向" : "负向");

        // 点动速度不由方向命令携带：具体速度由该轴的档位寄存器决定
        // （SetJogSpeedLevelAsync 写 1/2/3，PLC 读档位选择速度），
        // 上位机这里只负责置位方向命令，避免两处速度配置打架。
        return 异步写入对(相反, 目标, 取消令牌);
    }

    /// <inheritdoc />
    public Task 异步停止点动(string 轴号, CancellationToken 取消令牌 = default)
    {
        var 已规范化 = 规范化轴(轴号);

        _日志.LogInformation("{Axis} 轴停止点动", 已规范化);

        return 异步内部停止点动(已规范化, 取消令牌);
    }

    // ── 点动速度档位 ────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task 异步设置点动速度等级(
        string 轴号, int 等级, CancellationToken 取消令牌 = default)
    {
        _权限.要求(权限.视图全部页面);

        if (!点动速度等级地址.是否有效等级(等级))
        {
            throw new ArgumentOutOfRangeException(
                nameof(等级), 等级,
                $"点动速度档位只能是 {点动速度等级地址.最小等级}（低速）/ "
                + $"{点动速度等级地址.最小等级 + 1}（中速）/ {点动速度等级地址.最大等级}（高速）。");
        }

        var 已规范化 = 规范化轴(轴号);

        var 目标 = _配置.查找点动速度等级(已规范化)
            ?? throw new InvalidOperationException(
                $"未配置 {已规范化} 轴的点动速度档位寄存器"
                + "（config/recipe-config.json 的 jogSpeedLevels）。");

        // 走 TagWriter 的"按地址写"：它会在服务层再强制一次 D 区，
        // 即使配置文件被手工改错，也不会写到线圈区去。
        await _写入器
            .异步写入寄存器地址($"D{目标.地址}", 等级, 取消令牌)
            .ConfigureAwait(false);

        _日志.LogInformation("{Axis} 轴点动速度档位已写入 D{Address} = {Level}",
            已规范化, 目标.地址, 等级);
    }

    // ── 回零 ────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task 异步回零(string 轴号, CancellationToken 取消令牌 = default)
    {
        _权限.要求(权限.视图全部页面);
        确保启用();

        var 项 = _配置.回零
            .FirstOrDefault(回零项 => string.Equals(回零项.轴, 轴号, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"未配置「{轴号}」轴的回零按钮（config/recipe-config.json 的 homeButtons）。");

        _日志.LogInformation("{Axis} 轴回零（{Address}）", 轴号, 项.地址);

        // 复用脉冲线圈：解析强制 M 区，且复位是无条件安全出口
        await 异步脉冲线圈(项.地址, TimeSpan.Zero, 取消令牌).ConfigureAwait(false);
    }

    // ── 配置驱动的轴点动（锁附 X/Y/Z/ZZ、点胶 X/Z） ─────────────────────

    /// <inheritdoc />
    public async Task 异步点动轴(string 轴键, int 方向, CancellationToken 取消令牌 = default)
    {
        _权限.要求(权限.视图全部页面);
        确保启用();

        var 项 = 查找点动轴(轴键);

        if (方向 == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(方向), 方向, "点动方向必须为 1（正向）或 -1（负向）。");
        }

        var 目标 = 方向 > 0 ? 项.正转地址 : 项.反转地址;
        var 相反 = 方向 > 0 ? 项.反转地址 : 项.正转地址;

        _日志.LogInformation("{Axis} 点动（{Direction}）", 项.显示名称, 方向 > 0 ? "正向" : "负向");

        // 先复位相反方向再置位目标方向：避免两个方向线圈同时为 1
        await 异步写入方向对(相反, 目标, 取消令牌).ConfigureAwait(false);
    }

    /// <summary>
    /// 一次请求写两个方向线圈。
    ///
    /// <para><b>为什么合并</b>：原先是两次单点写（功能码 05）串行往返，按下方向键到轴真正动作
    /// 会多出一个 PLC 往返的延迟，现场手感是"按下去要等一下才动"。两个 M 地址相邻时改用
    /// "写多个线圈"（功能码 0F）一次写完，延迟砍半；地址不相邻时自动退回两次单点写。</para>
    /// </summary>
    private async Task 异步写入方向对(string 复位地址, string 置位地址, CancellationToken 取消令牌)
    {
        if (_客户端.是否已连接
            && 尝试解析M区(复位地址, out var 复位)
            && 尝试解析M区(置位地址, out var 置位)
            && Math.Abs(复位 - 置位) == 1)
        {
            var 起点 = Math.Min(复位, 置位);
            var 值列表 = 复位 < 置位 ? new[] { false, true } : new[] { true, false };

            await _客户端.异步写入多个线圈(起点, 值列表, 取消令牌).ConfigureAwait(false);
            return;
        }

        await _写入器.异步写入线圈地址(复位地址, false, 取消令牌).ConfigureAwait(false);
        await _写入器.异步写入线圈地址(置位地址, true, 取消令牌).ConfigureAwait(false);
    }

    /// <summary>尝试把地址解析为 M 区线圈的 Modbus 地址（非 M 区返回 false）。</summary>
    private bool 尝试解析M区(string 地址值, out int 地址)
    {
        地址 = 0;

        if (!_解析器.尝试解析(地址值, out var PLC地址) || PLC地址 is null || PLC地址.元素 != 软元素.M)
        {
            return false;
        }

        地址 = PLC地址.Modbus地址;
        return true;
    }

    /// <inheritdoc />
    public async Task 异步停止点动轴(string 轴键, CancellationToken 取消令牌 = default)
    {
        var 项 = _配置.点动
            .FirstOrDefault(点动项 => string.Equals(点动项.轴, 轴键, StringComparison.OrdinalIgnoreCase));

        if (项 is null)
        {
            return;
        }

        // 停止是安全出口：不走 确保启用，也不检查权限之外的联锁
        await _写入器.异步写入线圈地址(项.正转地址, false, 取消令牌).ConfigureAwait(false);
        await _写入器.异步写入线圈地址(项.反转地址, false, 取消令牌).ConfigureAwait(false);
    }

    private 点动按钮项 查找点动轴(string 轴键) =>
        _配置.点动.FirstOrDefault(点动项 => string.Equals(点动项.轴, 轴键, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException(
            $"未配置「{轴键}」轴的点动地址（config/recipe-config.json 的 jogButtons）。");

    // ── 内部 ────────────────────────────────────────────────────────────

    private async Task 异步写入对(string 重置标签, string 设置标签, CancellationToken 取消令牌)
    {
        await _写入器.异步写入布尔(重置标签, false, 取消令牌).ConfigureAwait(false);
        await _写入器.异步写入布尔(设置标签, true, 取消令牌).ConfigureAwait(false);
    }

    private async Task 异步内部停止点动(string 轴号, CancellationToken 取消令牌)
    {
        await _写入器.异步写入布尔($"Jog{轴号}Forward", false, 取消令牌).ConfigureAwait(false);
        await _写入器.异步写入布尔($"Jog{轴号}Reverse", false, 取消令牌).ConfigureAwait(false);
    }

    private void 确保启用()
    {
        if (!是否启用)
        {
            throw new InvalidOperationException("手动功能未使能，操作已被拒绝。请先开启手动使能。");
        }
    }

    private static string 规范化轴(string 轴号)
    {
        var 已规范化 = 轴号.Trim().ToUpperInvariant();

        return 支持轴.Contains(已规范化, StringComparer.OrdinalIgnoreCase)
            ? 已规范化
            : throw new ArgumentOutOfRangeException(
                nameof(轴号), 轴号, $"不支持的轴：{轴号}。支持 {string.Join(" / ", 支持轴)}。");
    }
}
