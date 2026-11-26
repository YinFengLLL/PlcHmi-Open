using System.Globalization;
using Microsoft.Extensions.Logging;
using 上位机.业务.配置;
using 上位机.通信.Modbus;
using 上位机.通信.配方;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// 通用点表服务：锁附示教与点胶示教共用同一套读写。
///
/// <para><b>布局：</b>每张点表按列分区存放，第 i 点某列地址 = 列基址 + i × 字每点位。
/// 读一整列是<b>一次块读</b>；下发一整列是<b>一次块写</b>。</para>
///
/// <para><b>写入时机硬约束：</b>只有在「保存坐标点」或「参数写入」时才写 PLC；
/// 下发前逐点做坐标范围校验，防止从未示教的空点把轴驱动到原点。</para>
/// </summary>
public sealed partial class 点表服务 : 点表服务接口
{
    private readonly Modbus客户端接口 _客户端;
    private readonly 标签值服务接口 _值列表;
    private readonly 配方服务接口 _配方;
    private readonly 配方配置 _配置;
    private readonly 权限守卫接口 _权限;
    private readonly ILogger<点表服务> _日志;

    /// <summary>表键 → 地址映射。</summary>
    private readonly Dictionary<string, 点表地址映射> _映射 = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>表键 → 当前行缓存（永远包含全部点）。</summary>
    private readonly Dictionary<string, List<点值行>> _缓存 = new(StringComparer.OrdinalIgnoreCase);

    public 点表服务(
        Modbus客户端接口 客户端,
        标签值服务接口 值列表,
        配方服务接口 配方,
        配方配置 配置对象,
        权限守卫接口 权限,
        ILogger<点表服务> 日志)
    {
        _客户端 = 客户端;
        _值列表 = 值列表;
        _配方 = 配方;
        _配置 = 配置对象;
        _权限 = 权限;
        _日志 = 日志;

        foreach (var 表项 in _配置.点表.点表)
        {
            _映射[表项.键] = new 点表地址映射(表项);
            _缓存[表项.键] = 全空行(表项);
        }
    }

    /// <inheritdoc />
    public event EventHandler<string>? 点表已变更;

    /// <inheritdoc />
    public IReadOnlyList<点表定义> 全部点表 => _配置.点表.点表;

    /// <inheritdoc />
    public 点表定义 表(string 键) =>
        _配置.点表.查找(键)
        ?? throw new InvalidOperationException(
            $"未配置点表「{键}」（config/recipe-config.json 的 pointTables）。");

    /// <inheritdoc />
    public int 点数(string 键) => _映射[表(键).键].点数;

    /// <inheritdoc />
    /// <remarks>
    /// 返回内部行缓存（列表结构在建表后稳定，仅元素值会变）。
    /// <b>仅限界面线程访问</b>：采集/后台线程请改用事件 + 深拷贝口径。
    /// </remarks>
    public IReadOnlyList<点值行> 当前点表(string 键) => _缓存[表(键).键];

    // ── 初始化与加载 ────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task 异步初始化(CancellationToken 取消令牌 = default)
    {
        foreach (var 表项 in _配置.点表.点表)
        {
            从机种快照填充(表项);
        }

        _日志.LogInformation(
            "已按机种 {No} 的快照载入 {Count} 张点表",
            _配方.当前配方号, _配置.点表.点表.Count);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task 异步加载点表(string 键, CancellationToken 取消令牌 = default)
    {
        var 表项 = 表(键);

        从机种快照填充(表项);
        点表已变更?.Invoke(this, 表项.键);

        _日志.LogDebug("已按机种快照载入点表 {Table}", 表项.显示名称);
        return Task.CompletedTask;
    }

    // ── 内存编辑 ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public void 设置点值(string 键, int 序号, string 列键, double 值)
    {
        var 表项 = 表(键);
        var 映射项 = _映射[表项.键];

        if (!映射项.序号合法(序号))
        {
            return;
        }

        var 行项 = _缓存[表项.键][序号];

        // 变化检测：值没变不发事件。曾经每次调用都触发 点表已变更，
        // 界面为整行重建行 VM——输入框每键触发（即使值相同）都在白做工
        if (行项.取(列键) == 值)
        {
            return;
        }

        行项.设(列键, 值);
        点表已变更?.Invoke(this, 表项.键);
    }

    /// <inheritdoc />
    public void 清空点(string 键, int 序号)
    {
        var 表项 = 表(键);
        var 映射项 = _映射[表项.键];

        if (!映射项.序号合法(序号))
        {
            return;
        }

        _缓存[表项.键][序号] = new 点值行 { 序号 = 序号 };
        _日志.LogInformation("已清空 {Table} 第 {Point} 点", 表项.显示名称, 序号 + 1);
        点表已变更?.Invoke(this, 表项.键);
    }

    /// <inheritdoc />
    public 点值行 读取当前位置(string 键)
    {
        var 表项 = 表(键);
        var 映射项 = _映射[表项.键];

        // 从空行出发：只填"参与保存坐标且带位置标签"的列。
        // 其余列（指令码、浮高，或本表未列入 保存坐标列 的轴）由调用方沿用目标点自身的值，
        // 不能借别的点的缓存来填——否则会把第 1 点的指令码/浮高复制到所选点。
        var 行项 = new 点值行();

        foreach (var 列项 in 表项.坐标列)
        {
            if (string.IsNullOrWhiteSpace(列项.位置标签) || !表项.是否参与保存坐标(列项.键))
            {
                continue;
            }

            // 浮高来自测高地址（D240），不是 Z 轴位置：这里不填，由 异步示教当前位置 单独读
            if (是浮高表(表项)
                && string.Equals(列项.键, 浮高列键, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (_值列表.尝试获取双精度(列项.位置标签, out var 值))
            {
                行项.设(列项.键, 值);
            }
        }

        _ = 映射项;
        return 行项;
    }

    // ── 示教与下发 ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task 异步示教当前位置(string 键, int 序号, CancellationToken 取消令牌 = default)
    {
        _权限.要求(权限.编辑示教);

        var 表项 = 表(键);
        var 映射项 = _映射[表项.键];

        if (!映射项.序号合法(序号))
        {
            throw new ArgumentOutOfRangeException(nameof(序号), 序号, "点序号超出范围。");
        }

        var 采集值 = 读取当前位置(键);
        var 行项 = _缓存[表项.键][序号];

        // 浮高只对锁附表生效（2026-10-24 现场口径：点胶示教不写浮高）。
        // 由测高机构给出（独立地址），不是任何轴的位置：配了地址就必须读成功，
        // 否则宁可不示教——把 Z 轴位置或 0 写进浮高列更难查
        var 需要浮高 = 是浮高表(表项)
                       && 表项.查找列(浮高列键) is not null
                       && 表项.是否参与保存坐标(浮高列键);

        var 浮高值 = 需要浮高 ? await 异步读取浮高(表项, 取消令牌).ConfigureAwait(false) : null;

        if (需要浮高 && 浮高值 is null)
        {
            throw new InvalidOperationException(
                $"读取当前浮高（D{表项.当前浮高地址}）失败，已拒绝示教——避免把错误的浮高写进点表。");
        }

        foreach (var 列项 in 表项.列)
        {
            var 用当前位置 = !string.IsNullOrWhiteSpace(列项.位置标签) && 表项.是否参与保存坐标(列项.键);
            var 值 = 用当前位置 ? 采集值.取(列项.键) : 行项.取(列项.键);

            if (用当前位置
                && 浮高值 is { } 已有浮高
                && string.Equals(列项.键, 浮高列键, StringComparison.OrdinalIgnoreCase))
            {
                值 = 已有浮高;
            }

            if (列项.数据类型 != 标签数据类型.UInt16 && 列项.数据类型 != 标签数据类型.Int16
                && (值 < 表项.最小值 || 值 > 表项.最大值))
            {
                throw new InvalidOperationException(
                    $"当前位置 {列项.显示名称} = {值:0.00} 超出允许范围"
                    + $"（{表项.最小值:0.00} ~ {表项.最大值:0.00}），已拒绝示教。");
            }

            行项.设(列项.键, 值);
        }

        await 异步写入单点(表项, 映射项, 序号, 取消令牌).ConfigureAwait(false);

        // 不带落盘 point-tables.json（点表整段属于机种），但必须把这个点并入机种快照：
        // 否则"示教完切页再回来"同样会看到旧值（与下发同一条数据链）
        await 异步并入机种快照(表项, [序号], 取消令牌).ConfigureAwait(false);

        _日志.LogInformation("{Table} 已示教第 {Point} 点并保存到本机种", 表项.显示名称, 序号 + 1);
        点表已变更?.Invoke(this, 表项.键);
    }

    /// <inheritdoc />
    public async Task 异步写入点表(string 键, CancellationToken 取消令牌 = default)
    {
        _权限.要求(权限.编辑示教);

        var 表项 = 表(键);
        var 映射项 = _映射[表项.键];

        if (!_客户端.是否已连接)
        {
            throw new InvalidOperationException("PLC 未连接，无法下发点表。");
        }

        // 下发前逐点校验：未示教的点缓存的是 0，直接写会把轴驱动到原点
        var 无效 = new List<string>();

        for (var 序号 = 0; 序号 < 映射项.点数; 序号++)
        {
            var 行项 = _缓存[表项.键][序号];

            foreach (var 列项 in 表项.坐标列)
            {
                var 值 = 行项.取(列项.键);

                if (值 < 表项.最小值 || 值 > 表项.最大值)
                {
                    无效.Add($"第 {序号 + 1:00} 点 {列项.显示名称}（{值:0.##}）");
                    break;
                }
            }
        }

        if (无效.Count > 0)
        {
            throw new InvalidOperationException(
                $"点表「{表项.显示名称}」有 {无效.Count} 个点超出坐标范围"
                + $"（{表项.最小值:0.00} ~ {表项.最大值:0.00}），已拒绝下发。请先完成示教：\n"
                + string.Join("\n", 无效.Take(5))
                + (无效.Count > 5 ? $"\n……共 {无效.Count} 个" : string.Empty));
        }

        foreach (var 列项 in 表项.列)
        {
            var 缓冲区 = new ushort[映射项.列块字(列项.键)];

            for (var 序号 = 0; 序号 < 映射项.点数; 序号++)
            {
                var 寄存器列表 = 寄存器编解码器.编码(
                    列项.数据类型, _缓存[表项.键][序号].取(列项.键), 字顺序.小字节序);

                var 偏移量 = 序号 * 列项.字每点位;
                Array.Copy(寄存器列表, 0, 缓冲区, 偏移量, 列项.字每点位);
            }

            await _客户端
                .异步写入多个寄存器(列项.基址, 缓冲区, 取消令牌)
                .ConfigureAwait(false);
        }

        // 写 PLC 成功后并入本机种快照：页面重新进入是从"机种快照"装载的，
        // 不并入就会"下发完切页再回来数据不见了"（2026-10-16 现场问题）
        await 异步并入机种快照(表项, Enumerable.Range(0, 映射项.点数).ToList(), 取消令牌)
            .ConfigureAwait(false);

        _日志.LogInformation("已下发点表 {Table}（{Count} 点）并保存到本机种", 表项.显示名称, 映射项.点数);
    }

    /// <summary>写单个点的全部列（每列一次写，占 1–4 字）。</summary>
    private async Task 异步写入单点(
        点表定义 表项, 点表地址映射 映射项, int 序号, CancellationToken 取消令牌)
    {
        if (!_客户端.是否已连接)
        {
            throw new InvalidOperationException("PLC 未连接，无法写入示教点。");
        }

        foreach (var 列项 in 表项.列)
        {
            var 寄存器列表 = 寄存器编解码器.编码(
                列项.数据类型, _缓存[表项.键][序号].取(列项.键), 字顺序.小字节序);

            var 地址值 = 映射项.地址(序号, 列项.键);

            if (寄存器列表.Length == 1)
            {
                await _客户端.异步写入单个寄存器(地址值, 寄存器列表[0], 取消令牌)
                    .ConfigureAwait(false);
            }
            else
            {
                await _客户端.异步写入多个寄存器(地址值, 寄存器列表, 取消令牌)
                    .ConfigureAwait(false);
            }
        }
    }

    // ── 内部 ────────────────────────────────────────────────────────────

    private static List<点值行> 全空行(点表定义 表项)
    {
        var 列表 = new List<点值行>(表项.点数);

        for (var 序号 = 0; 序号 < 表项.点数; 序号++)
        {
            列表.Add(new 点值行 { 序号 = 序号 });
        }

        return 列表;
    }
}
