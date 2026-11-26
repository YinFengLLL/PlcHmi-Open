using System.Globalization;
using Microsoft.Extensions.Logging;
using 上位机.业务.配置;
using 上位机.通信.Modbus;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// 辅助点（其它点）服务：管理非锁附/点胶类的辅助点位（滑螺丝点、相机中心、锁杆中心…）。
///
/// <para>点位定义（名称 + X/Y/Z 基址）落盘 <c>config/aux-points.json</c>，
/// 坐标值存放在 PLC 的对应寄存器中；支持增删改查与 CSV 导入导出。</para>
/// </summary>
public sealed partial class 辅助点服务 : 辅助点服务接口
{
    private readonly Modbus客户端接口 _客户端;
    private readonly 配方配置 _配置;
    private readonly 权限守卫接口 _权限;
    private readonly ILogger<辅助点服务> _日志;

    private readonly List<辅助点定义> _点位 = new();

    /// <summary>
    /// 点位列表的同步锁。此前本服务完全无同步：CSV 导入的 Clear()+AddRange 与
    /// UI 枚举 <see cref="点位集合"/> 并发会抛 Collection was modified。
    /// </summary>
    private readonly object _同步 = new();

    public 辅助点服务(
        Modbus客户端接口 客户端,
        配方配置 配置对象,
        权限守卫接口 权限,
        ILogger<辅助点服务> 日志)
    {
        _客户端 = 客户端;
        _配置 = 配置对象;
        _权限 = 权限;
        _日志 = 日志;

        // 先以配置中的默认点位打底；落盘文件存在时会在初始化阶段整体覆盖
        foreach (var 点项 in _配置.其它点.点位)
        {
            _点位.Add(点项.克隆());
        }
    }

    /// <inheritdoc />
    public event EventHandler? 已变更;

    /// <inheritdoc />
    // 快照交出：与 报警定义服务/参数表格服务/手动元件服务 统一口径。
    // 曾经直接交出内部 List，调用方既可绕过权限 Add，枚举期间遭遇 Clear() 还会抛异常。
    public IReadOnlyList<辅助点定义> 点位集合
    {
        get { lock (_同步) { return _点位.ToArray(); } }
    }

    /// <inheritdoc />
    public 其它点配置 配置 => _配置.其它点;

    // ── 初始化 ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task 异步初始化(CancellationToken 取消令牌 = default)
    {
        var 结果 = await 配置文件加载器
            .异步加载JSON<辅助点文件>(配置路径.其它点, JSON配置.读取, _日志, 取消令牌)
            .ConfigureAwait(false);

        if (结果.值 is null)
        {
            _日志.LogInformation("未找到其它点定义文件，按配置默认点位启动：{Path}", 配置路径.其它点);
            return;
        }

        lock (_同步)
        {
            _点位.Clear();

            foreach (var 点项 in 结果.值.点位)
            {
                _点位.Add(点项);
            }

            _日志.LogInformation("已加载其它点定义：{Count} 个", _点位.Count);
        }

        已变更?.Invoke(this, EventArgs.Empty);
    }

    // ── 坐标读写 ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<(double X, double Y, double Z)> 异步读取坐标(
        辅助点定义 点位项, CancellationToken 取消令牌 = default)
    {
        ArgumentNullException.ThrowIfNull(点位项);

        return (await 异步读取字段(点位项.X基址, 取消令牌).ConfigureAwait(false),
                await 异步读取字段(点位项.Y基址, 取消令牌).ConfigureAwait(false),
                await 异步读取字段(点位项.Z基址, 取消令牌).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task 异步写入坐标(
        辅助点定义 点位项, double X, double Y, double Z, CancellationToken 取消令牌 = default)
    {
        ArgumentNullException.ThrowIfNull(点位项);

        _权限.要求(权限.编辑示教);

        var 范围 = _配置.其它点;

        foreach (var (名称值, 值) in new[] { ("X", X), ("Y", Y), ("Z", Z) })
        {
            if (值 < 范围.最小值 || 值 > 范围.最大值)
            {
                throw new InvalidOperationException(
                    $"{点位项.名称} 的 {名称值} = {值:0.00} 超出允许范围"
                    + $"（{范围.最小值:0.00} ~ {范围.最大值:0.00}）。");
            }
        }

        if (!_客户端.是否已连接)
        {
            throw new InvalidOperationException("PLC 未连接，无法写入坐标。");
        }

        await 异步写入字段(点位项.X基址, X, 取消令牌).ConfigureAwait(false);
        await 异步写入字段(点位项.Y基址, Y, 取消令牌).ConfigureAwait(false);
        await 异步写入字段(点位项.Z基址, Z, 取消令牌).ConfigureAwait(false);

        _日志.LogInformation("已写入其它点 {Name}：X={X} Y={Y} Z={Z}", 点位项.名称, X, Y, Z);
    }

    // ── 增删改查 ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task 异步新增(辅助点定义 点位项, CancellationToken 取消令牌 = default)
    {
        ArgumentNullException.ThrowIfNull(点位项);
        _权限.要求(权限.编辑绑定地址);

        if (string.IsNullOrWhiteSpace(点位项.名称))
        {
            throw new InvalidOperationException("点位名称不能为空。");
        }

        lock (_同步)
        {
            if (_配置.其它点.查找(点位项.名称) is not null
                || _点位.Any(点项 => string.Equals(点项.名称, 点位项.名称, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"其它点「{点位项.名称}」已存在。");
            }

            校验地址(点位项);

            _点位.Add(点位项.克隆());
        }

        await 异步落盘(取消令牌).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task 异步编辑(string 原名, 辅助点定义 点位项, CancellationToken 取消令牌 = default)
    {
        ArgumentNullException.ThrowIfNull(点位项);
        _权限.要求(权限.编辑绑定地址);

        if (string.IsNullOrWhiteSpace(点位项.名称))
        {
            throw new InvalidOperationException("点位名称不能为空。");
        }

        lock (_同步)
        {
            var 索引 = _点位.FindIndex(点项 => string.Equals(点项.名称, 原名, StringComparison.OrdinalIgnoreCase));

            if (索引 < 0)
            {
                throw new InvalidOperationException($"其它点「{原名}」不存在。");
            }

            // 允许改名（2026-10-15 现场反馈），但改名后不能与别的点撞名——
            // 名称是"按名查找 / 删除 / CSV 导入"的依据，重名会让后续操作指向错误的点。
            // 只排除自己，这样"只改坐标、名称原样提交"不会被误判。
            if (_点位.Where((点项, 序号) => 序号 != 索引)
                    .Any(点项 => string.Equals(点项.名称, 点位项.名称, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"其它点「{点位项.名称}」已存在，请换一个名称。");
            }

            校验地址(点位项);

            _点位[索引] = 点位项.克隆();
        }

        await 异步落盘(取消令牌).ConfigureAwait(false);

        if (!string.Equals(原名, 点位项.名称, StringComparison.Ordinal))
        {
            _日志.LogInformation("其它点已改名：{From} → {To}", 原名, 点位项.名称);
        }
    }

    /// <inheritdoc />
    public async Task 异步删除(string 名称值, CancellationToken 取消令牌 = default)
    {
        _权限.要求(权限.编辑绑定地址);

        lock (_同步)
        {
            var 索引 = _点位.FindIndex(点项 => string.Equals(点项.名称, 名称值, StringComparison.OrdinalIgnoreCase));

            if (索引 < 0)
            {
                throw new InvalidOperationException($"其它点「{名称值}」不存在。");
            }

            _点位.RemoveAt(索引);
        }

        await 异步落盘(取消令牌).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IReadOnlyList<辅助点定义> 查询(string 关键字)
    {
        lock (_同步)
        {
            if (string.IsNullOrWhiteSpace(关键字))
            {
                return _点位.ToList();
            }

            var 键 = 关键字.Trim();
            return _点位
                .Where(点项 => 点项.名称.Contains(键, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    // CSV 导入导出见 AuxPointService.Csv.cs（partial）。

    // ── 内部 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 地址基本校验。
    /// <para><b>2026-10-12：</b>按现场要求取消地址区间限制——不再要求落在
    /// <c>其它点.允许范围</c> 内；只保留"必须是正整数"这一条录入错误判定。</para>
    /// </summary>
    private static void 校验地址(辅助点定义 点位项)
    {
        foreach (var (名称值, 地址值) in new[]
                 {
                     ("X", 点位项.X基址), ("Y", 点位项.Y基址), ("Z", 点位项.Z基址),
                 })
        {
            if (地址值 <= 0)
            {
                throw new InvalidOperationException($"{点位项.名称} 的 {名称值} 地址必须是正整数。");
            }
        }
    }

    private async Task<double> 异步读取字段(int 地址值, CancellationToken 取消令牌)
    {
        if (!_客户端.是否已连接)
        {
            return 0;
        }

        var 寄存器列表 = await _客户端.异步读取保持寄存器(地址值, 2, 取消令牌).ConfigureAwait(false);

        if (寄存器列表.Length < 2)
        {
            return 0;
        }

        var 值 = 寄存器编解码器.解码(标签数据类型.Float32, 寄存器列表, 字顺序.小字节序);
        return Convert.ToDouble(值, CultureInfo.InvariantCulture);
    }

    private async Task 异步写入字段(int 地址值, double 值, CancellationToken 取消令牌)
    {
        var 寄存器列表 = 寄存器编解码器.编码(标签数据类型.Float32, 值, 字顺序.小字节序);
        await _客户端.异步写入多个寄存器(地址值, 寄存器列表, 取消令牌).ConfigureAwait(false);
    }

    private async Task 异步落盘(CancellationToken 取消令牌)
    {
        var 文件项 = new 辅助点文件 { 点位 = _点位.Select(点项 => 点项.克隆()).ToList() };
        var JSON文本 = System.Text.Json.JsonSerializer.Serialize(文件项, JSON配置.写入);
        await 原子文件.异步写入全部文本(配置路径.其它点, JSON文本, 取消令牌).ConfigureAwait(false);
        已变更?.Invoke(this, EventArgs.Empty);
    }
}
