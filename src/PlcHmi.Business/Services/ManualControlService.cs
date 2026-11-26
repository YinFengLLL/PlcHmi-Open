using Microsoft.Extensions.Logging;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

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
public sealed partial class ManualWidgetService : IManualWidgetService
{
    /// <summary>
    /// 支持的轴名称。老四轴（X/Y/Z/R）的点动方向写在各轴参数区的 D 字内位
    /// （如 R 轴 D1510.2/.3）；点胶锁付机新增的 ZZ / 点胶 X / 点胶 Z 走
    /// <c>recipe-config.json</c> 的 jogButtons（配置驱动的 M 区线圈）。
    /// </summary>
    private static readonly string[] supportAxis = ["X", "Y", "Z", "R", "ZZ", "GlueX", "GlueZ"];

    /// <summary>按钮脉冲的默认宽度。</summary>
    private static readonly TimeSpan defaultPulseWidth = TimeSpan.FromMilliseconds(200);

    private readonly TagWriter _writer;
    private readonly IModbusClient _client;
    private readonly IAddressParser _parser;
    private readonly RecipeConfig _config;
    private readonly IPermissionGuard _permission;
    private readonly ILogger<ManualWidgetService> _log;

    /// <summary>
    /// 权限守卫是<b>必填</b>参数，刻意不给默认值（审查标准 R5）：
    /// 手动动作会真实驱动执行机构，服务层必须自成边界。
    /// 测试若不需要覆盖权限分支，请显式传入 <see cref="AllowAllPermissionGuard"/>。
    /// </summary>
    public ManualWidgetService(
        TagWriter Writer,
        IModbusClient Client,
        IAddressParser Parser,
        RecipeConfig configPairObject,
        IPermissionGuard Permission,
        ILogger<ManualWidgetService> Log)
    {
        _writer = Writer;
        _client = Client;
        _parser = Parser;
        _config = configPairObject;
        _permission = Permission;
        _log = Log;
    }

    /// <inheritdoc />
    public bool IsEnabled { get; private set; }

    /// <inheritdoc />
    public event EventHandler<bool>? enabledChanged;

    /// <inheritdoc />
    public void SetEnabled(bool Enabled)
    {
        // 关闭使能是安全方向，任何情况下都放行；开启需要操作页权限。
        // 手动页与示教页都要求 视图全部页面（工程师及以上），本校验不改变现有可用性，
        // 只是把"界面隐藏按钮"升级为服务层边界（R5）。
        if (Enabled)
        {
            _permission.WantRequire(Permission.ViewAllPage);
        }

        if (IsEnabled == Enabled)
        {
            return;   // 无变化就不通知：避免订阅方回写属性造成的来回触发
        }

        IsEnabled = Enabled;
        _log.LogInformation("手动使能：{State}", Enabled ? "开启" : "关闭");

        enabledChanged?.Invoke(this, Enabled);
    }

    /// <summary>
    /// 把手动使能状态同步到 PLC（M666 线圈，见 <see cref="ManualEnableRegister"/>）。
    ///
    /// <para><b>按常量写、不按点位名写</b>：现场口径是固定信号 M666，
    /// 而 <c>tags.json</c> 里的 <c>ManualEnable</c> 点位在老配置里可能还指向别的地址——
    /// 按名写会跟着旧配置写到别的位上（可能撞上 PLC 的其它用途），按常量写则现场必达。</para>
    ///
    /// <para>由 <see cref="ManualEnableHoldService"/> 周期调用，实现"打开时持续置位 / 关闭时持续复位"；
    /// 开关切换时也会立即调用一次，界面手感不受周期影响。</para>
    /// </summary>
    public async Task SyncEnabledAsync(CancellationToken cancellationToken = default)
    {
        // 与 设置启用 同口径：仅"开启方向"需要权限，关闭永远放行
        if (IsEnabled)
        {
            _permission.WantRequire(Permission.ViewAllPage);
        }

        if (!_client.IsConnected)
        {
            // 未连接不抛异常：使能开关是界面操作，不能因为 PLC 抖动把人挡在外面
            _log.LogDebug("PLC 未连接，手动使能状态（{State}）未写入 M{Coil}",
                IsEnabled ? "开启" : "关闭", ManualEnableRegister.Coil);
            return;
        }

        await _client.WriteSingleCoilAsync(ManualEnableRegister.Coil, IsEnabled, cancellationToken).ConfigureAwait(false);
    }

    // ── 手动页元件（按地址操作）：见 ManualControlService.Widgets.cs（partial）──

    // ── 四轴点动 ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task JogAsync(string axisNumber, int direction, CancellationToken cancellationToken = default)
    {
        _permission.WantRequire(Permission.ViewAllPage);
        EnsureEnabled();

        var normalized = RuleRangeConvertAxis(axisNumber);

        if (direction == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "点动方向必须为 1（正向）或 -1（负向）。");
        }

        var forwardTag = AxisTagName.JogForward(normalized);
        var reverseTag = AxisTagName.JogReverse(normalized);
        var target = direction > 0 ? forwardTag : reverseTag;
        var mutualReverse = direction > 0 ? reverseTag : forwardTag;

        _log.LogInformation("{Axis} 轴{Jog}点动", normalized, direction > 0 ? "正向" : "负向");

        // 点动速度不由方向命令携带：具体速度由该轴的档位寄存器决定
        // （SetJogSpeedLevelAsync 写 1/2/3，PLC 读档位选择速度），
        // 上位机这里只负责置位方向命令，避免两处速度配置打架。
        return WritePairAsync(mutualReverse, target, cancellationToken);
    }

    /// <inheritdoc />
    public Task StopJogAsync(string axisNumber, CancellationToken cancellationToken = default)
    {
        var normalized = RuleRangeConvertAxis(axisNumber);

        _log.LogInformation("{Axis} 轴停止点动", normalized);

        return InnerPartStopJogAsync(normalized, cancellationToken);
    }

    // ── 点动速度档位 ────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task SetJogSpeedLevelAsync(
        string axisNumber, int Level, CancellationToken cancellationToken = default)
    {
        _permission.WantRequire(Permission.ViewAllPage);

        if (!JogSpeedLevelAddress.IsValidLevel(Level))
        {
            throw new ArgumentOutOfRangeException(
                nameof(Level), Level,
                $"点动速度档位只能是 {JogSpeedLevelAddress.MinLevel}（低速）/ "
                + $"{JogSpeedLevelAddress.MinLevel + 1}（中速）/ {JogSpeedLevelAddress.maxLevel}（高速）。");
        }

        var normalized = RuleRangeConvertAxis(axisNumber);

        var target = _config.FindJogSpeedLevel(normalized)
            ?? throw new InvalidOperationException(
                $"未配置 {normalized} 轴的点动速度档位寄存器"
                + "（config/recipe-config.json 的 jogSpeedLevels）。");

        // 走 TagWriter 的"按地址写"：它会在服务层再强制一次 D 区，
        // 即使配置文件被手工改错，也不会写到线圈区去。
        await _writer
            .WriteRegisterAddressAsync($"D{target.Address}", Level, cancellationToken)
            .ConfigureAwait(false);

        _log.LogInformation("{Axis} 轴点动速度档位已写入 D{Address} = {Level}",
            normalized, target.Address, Level);
    }

    // ── 回零 ────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task HomeAsync(string axisNumber, CancellationToken cancellationToken = default)
    {
        _permission.WantRequire(Permission.ViewAllPage);
        EnsureEnabled();

        var Item = _config.Home
            .FirstOrDefault(homeItem => string.Equals(homeItem.Axis, axisNumber, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"未配置「{axisNumber}」轴的回零按钮（config/recipe-config.json 的 homeButtons）。");

        _log.LogInformation("{Axis} 轴回零（{Address}）", axisNumber, Item.Address);

        // 复用脉冲线圈：解析强制 M 区，且复位是无条件安全出口
        await PulseCoilAsync(Item.Address, TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
    }

    // ── 配置驱动的轴点动（锁附 X/Y/Z/ZZ、点胶 X/Z） ─────────────────────

    /// <inheritdoc />
    public async Task JogAxisAsync(string AxisKey, int direction, CancellationToken cancellationToken = default)
    {
        _permission.WantRequire(Permission.ViewAllPage);
        EnsureEnabled();

        var Item = FindJogAxis(AxisKey);

        if (direction == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "点动方向必须为 1（正向）或 -1（负向）。");
        }

        var target = direction > 0 ? Item.ForwardAddress : Item.ReverseAddress;
        var mutualReverse = direction > 0 ? Item.ReverseAddress : Item.ForwardAddress;

        _log.LogInformation("{Axis} 点动（{Direction}）", Item.DisplayName, direction > 0 ? "正向" : "负向");

        // 先复位相反方向再置位目标方向：避免两个方向线圈同时为 1
        await WriteDirectionPairAsync(mutualReverse, target, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 一次请求写两个方向线圈。
    ///
    /// <para><b>为什么合并</b>：原先是两次单点写（功能码 05）串行往返，按下方向键到轴真正动作
    /// 会多出一个 PLC 往返的延迟，现场手感是"按下去要等一下才动"。两个 M 地址相邻时改用
    /// "写多个线圈"（功能码 0F）一次写完，延迟砍半；地址不相邻时自动退回两次单点写。</para>
    /// </summary>
    private async Task WriteDirectionPairAsync(string resetAddress, string setAddress, CancellationToken cancellationToken)
    {
        if (_client.IsConnected
            && TryParseMArea(resetAddress, out var reset)
            && TryParseMArea(setAddress, out var setAction)
            && Math.Abs(reset - setAction) == 1)
        {
            var startPoint = Math.Min(reset, setAction);
            var ValueList = reset < setAction ? new[] { false, true } : new[] { true, false };

            await _client.WriteMultiCoilAsync(startPoint, ValueList, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _writer.WriteCoilAddressAsync(resetAddress, false, cancellationToken).ConfigureAwait(false);
        await _writer.WriteCoilAddressAsync(setAddress, true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>尝试把地址解析为 M 区线圈的 Modbus 地址（非 M 区返回 false）。</summary>
    private bool TryParseMArea(string addressValue, out int Address)
    {
        Address = 0;

        if (!_parser.TryParse(addressValue, out var PlcAddress) || PlcAddress is null || PlcAddress.ElementItem != SoftElement.M)
        {
            return false;
        }

        Address = PlcAddress.ModbusAddress;
        return true;
    }

    /// <inheritdoc />
    public async Task StopJogAxisAsync(string AxisKey, CancellationToken cancellationToken = default)
    {
        var Item = _config.Jog
            .FirstOrDefault(jogItem => string.Equals(jogItem.Axis, AxisKey, StringComparison.OrdinalIgnoreCase));

        if (Item is null)
        {
            return;
        }

        // 停止是安全出口：不走 确保启用，也不检查权限之外的联锁
        await _writer.WriteCoilAddressAsync(Item.ForwardAddress, false, cancellationToken).ConfigureAwait(false);
        await _writer.WriteCoilAddressAsync(Item.ReverseAddress, false, cancellationToken).ConfigureAwait(false);
    }

    private JogButtonItem FindJogAxis(string AxisKey) =>
        _config.Jog.FirstOrDefault(jogItem => string.Equals(jogItem.Axis, AxisKey, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException(
            $"未配置「{AxisKey}」轴的点动地址（config/recipe-config.json 的 jogButtons）。");

    // ── 内部 ────────────────────────────────────────────────────────────

    private async Task WritePairAsync(string resetTag, string setTag, CancellationToken cancellationToken)
    {
        await _writer.WriteBooleanAsync(resetTag, false, cancellationToken).ConfigureAwait(false);
        await _writer.WriteBooleanAsync(setTag, true, cancellationToken).ConfigureAwait(false);
    }

    private async Task InnerPartStopJogAsync(string axisNumber, CancellationToken cancellationToken)
    {
        await _writer.WriteBooleanAsync($"Jog{axisNumber}Forward", false, cancellationToken).ConfigureAwait(false);
        await _writer.WriteBooleanAsync($"Jog{axisNumber}Reverse", false, cancellationToken).ConfigureAwait(false);
    }

    private void EnsureEnabled()
    {
        if (!IsEnabled)
        {
            throw new InvalidOperationException("手动功能未使能，操作已被拒绝。请先开启手动使能。");
        }
    }

    private static string RuleRangeConvertAxis(string axisNumber)
    {
        var normalized = axisNumber.Trim().ToUpperInvariant();

        return supportAxis.Contains(normalized, StringComparer.OrdinalIgnoreCase)
            ? normalized
            : throw new ArgumentOutOfRangeException(
                nameof(axisNumber), axisNumber, $"不支持的轴：{axisNumber}。支持 {string.Join(" / ", supportAxis)}。");
    }
}
