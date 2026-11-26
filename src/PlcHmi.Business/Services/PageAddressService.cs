using System.Globalization;
using Microsoft.Extensions.Logging;
using 上位机.通信.Modbus;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// 页面地址服务：把页面上所有与 PLC 交互的元件地址收口成一份可编辑清单，
/// 并在保存时一次落盘（保存部分见 <c>PageAddressService.Save.cs</c>）。
///
/// <para><b>已取消地址范围限制</b>：不再校验轴参数区 / PLC 保留区 / 区间归属，
/// 只保证地址可解析、元素类型正确、本次提交内不重叠。</para>
/// </summary>
public sealed partial class 页面地址服务 : 页面地址服务接口
{
    /// <summary>说明：锁附页的手动轴（标定页复用同样的三根轴）。</summary>

    private readonly Modbus客户端接口 _客户端;
    private readonly 标签写入器 _写入器;
    private readonly 配方配置 _配置;
    private readonly 权限守卫接口 _权限;
    private readonly ILogger<页面地址服务> _日志;

    public 页面地址服务(
        Modbus客户端接口 客户端,
        标签写入器 写入器,
        配方配置 配置对象,
        权限守卫接口 权限,
        ILogger<页面地址服务> 日志)
    {
        _客户端 = 客户端;
        _写入器 = 写入器;
        _配置 = 配置对象;
        _权限 = 权限;
        _日志 = 日志;
    }

    // ── 收集 ────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public IReadOnlyList<地址编辑项> 收集点表页(string 表键)
    {
        var 表 = _配置.点表.查找(表键)
            ?? throw new InvalidOperationException($"未配置点表「{表键}」。");

        var 分组 = 分组名(表);
        var 列表 = new List<地址编辑项>();

        foreach (var 列项 in 表.列)
        {
            列表.Add(new 地址编辑项
            {
                类别 = "点表列",
                键 = 列项.键,
                名称 = $"{列项.显示名称}基址",
                地址文本 = $"D{列项.基址}",
                数据类型文本 = 列项.数据类型.ToString(),
            });
        }

        // 手动区「当前浮高」（2026-10-24）：只有配了地址的表（锁附页）才显示这一行。
        // 固定元件——可改地址，不可删、不可改类型（REAL 两字）。
        if (表.当前浮高地址 > 0)
        {
            列表.Add(new 地址编辑项
            {
                类别 = "手动区",
                键 = "当前浮高",
                名称 = "当前浮高（REAL，占 2 字）",
                地址文本 = $"D{表.当前浮高地址}",
                数据类型文本 = 标签数据类型.Float32.ToString(),
            });
        }

        // 点胶表有指令码列 = 点胶单点参数走结构体数组：此时辅助地址行才显示"结构体成员"勾选框
        var 允许结构体成员 = 表.含指令码;

        foreach (var 项 in _配置.辅助地址.Where(项 => 分组匹配(项.分组, 分组)))
        {
            列表.Add(new 地址编辑项
            {
                类别 = "辅助地址",
                键 = 项.键,
                名称 = 项.显示名称,

                // 成员地址由结构体布局算出（= 第 1 点地址）；非成员就是它自己的固定地址
                地址文本 = 项.结构体成员 ? $"D{项.结构体基址 + 项.结构体内偏移}" : 项.地址,
                数据类型文本 = 项.数据类型.ToString(),
                分组 = 项.分组,
                可删除 = true,
                结构体成员 = 项.结构体成员,
                可设结构体成员 = 允许结构体成员,
            });
        }

        foreach (var 项 in 动作按钮(分组))
        {
            列表.Add(new 地址编辑项
            {
                类别 = "动作按钮",
                键 = 项.键,
                名称 = 项.显示名称,
                地址文本 = 项.地址,
                数据类型文本 = 标签数据类型.布尔.ToString(),
                分组 = 项.分组,

                // 固定按钮（点胶「参数模式」）是页面必备元件：可改地址不可删
                可删除 = !项.固定,
            });
        }

        foreach (var 项 in 本页点动(分组))
        {
            列表.Add(new 地址编辑项
            {
                类别 = "点动",
                键 = $"{项.轴}|正转",
                名称 = $"{项.显示名称}·正转",
                地址文本 = 项.正转地址,
                数据类型文本 = 标签数据类型.布尔.ToString(),
            });

            列表.Add(new 地址编辑项
            {
                类别 = "点动",
                键 = $"{项.轴}|反转",
                名称 = $"{项.显示名称}·反转",
                地址文本 = 项.反转地址,
                数据类型文本 = 标签数据类型.布尔.ToString(),
            });
        }

        foreach (var 轴号 in 本页点动(分组).Select(项 => 项.轴).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var 回零项 = 查找回零(轴号);
            列表.Add(new 地址编辑项
            {
                类别 = "回零",
                键 = 轴号,
                名称 = $"{轴号} 轴回零",
                地址文本 = 回零项?.地址 ?? string.Empty,
                数据类型文本 = 标签数据类型.布尔.ToString(),
            });
        }

        // 顶部固定状态灯：读 M 区（0=各轴未回原，1=各轴回原完成）。固定元件——可改地址不可删。
        foreach (var 灯项 in 状态灯(分组))
        {
            列表.Add(new 地址编辑项
            {
                类别 = "状态灯",
                键 = 灯项.键,
                名称 = 灯项.显示名称,
                地址文本 = 灯项.地址,
                数据类型文本 = 标签数据类型.布尔.ToString(),
                分组 = 灯项.分组,
            });
        }

        return 列表;
    }

    /// <inheritdoc />
    public IReadOnlyList<辅助地址项> 辅助地址(string 分组) =>
        _配置.辅助地址.Where(项 => 分组匹配(项.分组, 分组)).ToList();

    /// <inheritdoc />
    /// <remarks>
    /// <b>固定按钮排首位</b>（2026-10-21 现场要求：点胶页的「参数模式」按钮必须在动作区第一个）。
    /// <c>OrderByDescending</c> 是稳定排序，其余按钮的相对顺序与配置一致。
    /// </remarks>
    public IReadOnlyList<动作按钮项> 动作按钮(string 分组) =>
        _配置.动作按钮
            .Where(项 => 分组匹配(项.分组, 分组))
            .OrderByDescending(项 => 项.固定)
            .ToList();

    /// <inheritdoc />
    /// <remarks>
    /// 配置缺条目时按 <see cref="状态灯项.缺省地址"/> 合成一条（不写回配置）：
    /// 老现场升级后 recipe-config.json 里没有该段，返回空则示教页没灯、管理员也无入口配置。
    /// </remarks>
    public IReadOnlyList<状态灯项> 状态灯(string 分组)
    {
        var 已配 = _配置.状态灯.Where(项 => 分组匹配(项.分组, 分组)).ToList();

        if (已配.Count > 0)
        {
            return 已配;
        }

        return
        [
            new 状态灯项
            {
                键 = "回原状态灯",
                名称 = "各轴回原状态",
                分组 = 分组,
                地址 = 状态灯项.缺省地址,
            },
        ];
    }

    // ── 辅助地址读写 ────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<double> 异步读取辅助地址(辅助地址项 项, CancellationToken 取消令牌 = default)
    {
        ArgumentNullException.ThrowIfNull(项);

        if (项.是开关 || !_客户端.是否已连接)
        {
            return 0;
        }

        if (!尝试解析(项.地址, out var 地址值))
        {
            throw new InvalidOperationException($"辅助地址「{项.显示名称}」的地址「{项.地址}」无效。");
        }

        var 寄存器列表 = await _客户端
            .异步读取保持寄存器(地址值, 项.寄存器数量, 取消令牌)
            .ConfigureAwait(false);

        if (寄存器列表.Length < 项.寄存器数量)
        {
            return 0;
        }

        var 值 = 寄存器编解码器.解码(项.数据类型, 寄存器列表, 字顺序.小字节序);
        return Convert.ToDouble(值, CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public async Task<bool> 异步读取辅助开关(辅助地址项 项, CancellationToken 取消令牌 = default)
    {
        ArgumentNullException.ThrowIfNull(项);

        if (!_客户端.是否已连接)
        {
            return false;
        }

        if (!尝试解析(项.地址, out var 地址值))
        {
            throw new InvalidOperationException($"辅助开关「{项.显示名称}」的地址「{项.地址}」无效。");
        }

        var 线圈列表 = await _客户端
            .异步读取线圈(地址值, 1, 取消令牌)
            .ConfigureAwait(false);

        return 线圈列表.Length > 0 && 线圈列表[0];
    }

    /// <inheritdoc />
    public async Task 异步写入辅助地址(辅助地址项 项, double 值, CancellationToken 取消令牌 = default)
    {
        ArgumentNullException.ThrowIfNull(项);

        _权限.要求(权限.编辑示教);

        if (!_客户端.是否已连接)
        {
            throw new InvalidOperationException("PLC 未连接，无法写入。");
        }

        var 寄存器列表 = 寄存器编解码器.编码(项.数据类型, 值, 字顺序.小字节序);

        await _客户端
            .异步写入多个寄存器(绝对值(项.地址), 寄存器列表, 取消令牌)
            .ConfigureAwait(false);

        _日志.LogInformation("辅助地址 {Name}（{Address}）写入 {Value}", 项.显示名称, 项.地址, 值);
    }

    /// <inheritdoc />
    public async Task 异步写入辅助开关(辅助地址项 项, bool 值, CancellationToken 取消令牌 = default)
    {
        ArgumentNullException.ThrowIfNull(项);

        _权限.要求(权限.编辑示教);

        await _写入器.异步写入线圈地址(项.地址, 值, 取消令牌).ConfigureAwait(false);
        _日志.LogInformation("辅助开关 {Name}（{Address}）写入 {Value}", 项.显示名称, 项.地址, 值);
    }

    // ── 保存与回写 ──────────────────────────────────────────────────────
    // 见 PageAddressService.Save.cs（partial）：把"收集/读写"与"写回配置"分开，
    // 是为守住单文件 400 行的规模门禁。
}
