using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using 上位机.业务.配置;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// <see cref="页面地址服务"/> 的保存与回写部分（partial）。
///
/// <para>从主文件拆出：主文件逼近 400 行规模门禁；"收集/读写 PLC"与"写回 JSON 配置"
/// 是两类不同职责，分开更易审查。</para>
/// </summary>
public sealed partial class 页面地址服务
{
    // ── 保存 ────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task 异步保存点表页(
        string 表键, 地址编辑请求 请求, CancellationToken 取消令牌 = default)
    {
        ArgumentNullException.ThrowIfNull(请求);

        _权限.要求(权限.编辑绑定地址);

        var 表 = _配置.点表.查找(表键)
            ?? throw new InvalidOperationException($"未配置点表「{表键}」。");

        var 分组 = 分组名(表);

        移除(请求);

        foreach (var 项 in 请求.项集合)
        {
            switch (项.类别)
            {
                case "点表列":
                    var 列项 = 表.查找列(项.键)
                        ?? throw new InvalidOperationException($"点表「{表.显示名称}」不存在列「{项.键}」。");
                    列项.基址 = 解析D区(项);
                    break;

                case "手动区":
                    // 手动区的固定显示元件（当前浮高）：只改地址，类型固定 REAL
                    if (string.Equals(项.键, "当前浮高", StringComparison.OrdinalIgnoreCase))
                    {
                        表.当前浮高地址 = 解析D区(项);
                    }

                    break;

                case "辅助地址":
                    应用辅助地址(项, 分组);
                    break;

                case "动作按钮":
                    应用动作按钮(项, 分组);
                    break;

                case "点动":
                    应用点动(项, 分组);
                    break;

                case "回零":
                    应用回零(项);
                    break;

                case "状态灯":
                    应用状态灯(项, 分组);
                    break;

                default:
                    break;
            }
        }

        // 结构体成员统一重排（2026-10-21）：勾选 / 取消勾选、改数据类型都会改变成员偏移与结构体长度。
        // 此前这些参数只从 JSON 读、保存时不重算 —— 现场把「抬针高度」改成 INT16 后，
        // 后面的「点胶速度」仍停在旧偏移（显示 D3506，PLC 实际已是 D3505）。
        var 结构体长度 = 辅助地址结构布局.重新排布(_配置.辅助地址, 分组);

        // 兜底：统计的是应用完新增/修改后的 _配置.辅助地址 全量状态，落盘前拒绝超标配置。
        校验辅助地址上限();

        await 落盘(取消令牌).ConfigureAwait(false);

        _日志.LogInformation(
            "点表页 {Table} 的地址已保存（结构体成员 {Count} 个，长度 {Length} 字）",
            表.显示名称, 辅助地址结构布局.成员(_配置.辅助地址, 分组).Count, 结构体长度);
    }

    /// <inheritdoc />
    public async Task 异步保存标定页(地址编辑请求 请求, CancellationToken 取消令牌 = default)
    {
        ArgumentNullException.ThrowIfNull(请求);

        _权限.要求(权限.编辑绑定地址);

        移除(请求);

        foreach (var 项 in 请求.项集合)
        {
            switch (项.类别)
            {
                case "标定字段":
                    应用标定字段(项);
                    break;

                case "辅助地址":
                    应用辅助地址(项, "标定");
                    break;

                case "动作按钮":
                    应用动作按钮(项, "标定");
                    break;

                case "标定按钮":
                    // 标定页有两个启动按钮（「移至指定位置」2026-10-15 删除，
                    // 与锁附分组动作按钮「移全指定」同址重复）；下相机 2026-10-17 新增
                    if (项.键 == "启动")
                    {
                        _配置.标定.启动地址 = 项.地址文本.Trim();
                    }
                    else if (项.键 == "下相机启动")
                    {
                        _配置.标定.下相机启动地址 = 项.地址文本.Trim();
                    }

                    break;

                case "点动":
                    应用点动(项, "锁附");
                    break;

                case "回零":
                    应用回零(项);
                    break;

                default:
                    break;
            }
        }

        // 兜底：统计的是应用完新增/修改后的 _配置.辅助地址 全量状态，落盘前拒绝超标配置。
        校验辅助地址上限();

        await 落盘(取消令牌).ConfigureAwait(false);
        _日志.LogInformation("标定页的地址已保存");
    }

    // ── 应用（写回配置） ────────────────────────────────────────────────

    private void 移除(地址编辑请求 请求)
    {
        foreach (var 项 in 请求.已删除项)
        {
            // 键在「同一分组内唯一」：项.分组 非空时只删本组同名项，避免误删跨组的同名项；
            // 为空则保持全局匹配以兼容老请求（老编辑器没给分组）。
            var 限定分组 = !string.IsNullOrWhiteSpace(项.分组);

            switch (项.类别)
            {
                case "辅助地址":
                    _配置.辅助地址.RemoveAll(甲 =>
                        string.Equals(甲.键, 项.键, StringComparison.OrdinalIgnoreCase)
                        && (!限定分组 || 分组匹配(甲.分组, 项.分组)));
                    break;

                case "动作按钮":
                    // 动作按钮是管理员自定义的现场动作入口，允许删除；
                    // 但"固定按钮"（点胶参数模式）是页面必备元件，界面上本就没有删除入口，
                    // 这里再挡一道：配置被手工删掉也不会让按钮消失（下次加载还会被补齐）
                    _配置.动作按钮.RemoveAll(甲 =>
                        !甲.固定
                        && string.Equals(甲.键, 项.键, StringComparison.OrdinalIgnoreCase)
                        && (!限定分组 || 分组匹配(甲.分组, 项.分组)));
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>应用一个动作按钮（支持新增与改名改址）。</summary>
    private void 应用动作按钮(地址编辑项 项, string 分组)
    {
        // 键的唯一性契约是「同一分组内唯一」，旧逻辑全局按键查找会命中别的分组的同名键并就地改掉。
        var 现有 = _配置.动作按钮.FirstOrDefault(甲 =>
            分组匹配(甲.分组, 分组)
            && string.Equals(甲.键, 项.键, StringComparison.OrdinalIgnoreCase));

        if (现有 is null)
        {
            现有 = new 动作按钮项
            {
                键 = 项.键,
                分组 = string.IsNullOrWhiteSpace(项.分组) ? 分组 : 项.分组,

                // 自定义动作默认按"脉冲 + 需二次确认"处理：现场新增的按钮不动作为默认，
                // 想常按的按钮由管理员在 JSON 里把 是否脉冲 改成 false。
                是否脉冲 = true,
                是否危险 = true,
            };

            _配置.动作按钮.Add(现有);
        }

        现有.名称 = 项.名称;
        现有.地址 = 项.地址文本.Trim();
    }

    private void 应用辅助地址(地址编辑项 项, string 分组)
    {
        // 键的唯一性契约是「同一分组内唯一」，旧逻辑全局按键查找会命中别的分组的同名键并就地改掉。
        var 现有 = _配置.辅助地址.FirstOrDefault(甲 =>
            分组匹配(甲.分组, 分组)
            && string.Equals(甲.键, 项.键, StringComparison.OrdinalIgnoreCase));

        var 目标 = 现有 ?? new 辅助地址项();

        目标.键 = 项.键;
        目标.名称 = 项.名称;
        目标.分组 = string.IsNullOrWhiteSpace(项.分组) ? 分组 : 项.分组;
        目标.数据类型 = 解析类型(项.数据类型文本);

        if (项.结构体成员)
        {
            // 成员的真实地址由 辅助地址结构布局.重新排布 统一算（保存末尾执行）：
            // 这里只保证基址存在，地址栏填的值不参与寻址
            目标.结构体成员 = true;

            if (目标.结构体基址 <= 0)
            {
                目标.结构体基址 = 辅助地址结构布局.缺省基址;
            }
        }
        else
        {
            // 取消勾选 = 退回固定地址。结构体参数必须清零：
            // 留着旧基址/长度，下次加载时的老配置补齐会把它又认回成员（勾选框自己变了回去）
            目标.结构体成员 = false;
            目标.结构体基址 = 0;
            目标.结构体内偏移 = 0;
            目标.结构体长度 = 0;

            if (!int.TryParse(项.地址文本.Trim().TrimStart('D', 'd'), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var 序号) || 序号 <= 0)
            {
                throw new InvalidOperationException(
                    $"辅助地址「{项.名称}」的地址「{项.地址文本}」无效（应为 D 区序号）。");
            }

            目标.地址 = "D" + 序号.ToString(CultureInfo.InvariantCulture);
        }

        if (现有 is null)
        {
            _配置.辅助地址.Add(目标);
        }
    }

    /// <summary>
    /// 应用一条状态灯地址（缺失即新增）。
    /// <para><b>只接受 M 区</b>：状态灯用 FC01 读线圈取布尔，写成 D 区会去读一个字的位——
    /// 语义完全不同的两种元件，宁可在保存时明确报错，也不要让它"能存但读错"。</para>
    /// </summary>
    private void 应用状态灯(地址编辑项 项, string 分组)
    {
        var 地址 = 项.地址文本.Trim();
        var 整数部分 = 地址.TrimStart('M', 'm');

        if (地址.Length == 整数部分.Length
            || !int.TryParse(整数部分, NumberStyles.Integer, CultureInfo.InvariantCulture, out var 序号)
            || 序号 <= 0)
        {
            throw new InvalidOperationException(
                $"状态灯「{项.名称}」的地址「{项.地址文本}」无效（应为 M 区线圈，如 M470）。");
        }

        var 现有 = _配置.状态灯.FirstOrDefault(甲 =>
            string.Equals(甲.键, 项.键, StringComparison.OrdinalIgnoreCase)
            && string.Equals(甲.分组, 项.分组, StringComparison.OrdinalIgnoreCase));

        var 目标 = 现有 ?? new 状态灯项();

        if (现有 is null)
        {
            目标.键 = string.IsNullOrWhiteSpace(项.键) ? "回原状态灯" : 项.键;
            目标.分组 = string.IsNullOrWhiteSpace(项.分组) ? 分组 : 项.分组;
            _配置.状态灯.Add(目标);
        }

        目标.名称 = 项.名称;
        目标.地址 = "M" + 序号.ToString(CultureInfo.InvariantCulture);
    }

    // ── 内部（收集 / 保存共用） ─────────────────────────────────────────

    private static int 解析D区(地址编辑项 项)
    {
        if (!int.TryParse(项.地址文本.Trim().TrimStart('D', 'd'), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var 序号) || 序号 <= 0)
        {
            throw new InvalidOperationException($"「{项.名称}」的地址「{项.地址文本}」无效（应为 D 区序号）。");
        }

        return 序号;
    }

    private static int 绝对值(string 地址值)
    {
        if (!尝试解析(地址值, out var 地址))
        {
            throw new InvalidOperationException($"地址「{地址值}」无效。");
        }

        return 地址;
    }

    /// <summary>
    /// 解析 D 区地址（绝对值）。<b>只接受 D 区</b>：本方法的结果经
    /// <c>异步写入多个寄存器</c> 写保持寄存器，此前把 'M','m' 也一起 TrimStart，
    /// "M200" 与 "D200" 都解析成 200——手改 JSON 把辅助地址写成 M 区时，
    /// 值会被静默写进 D200（另一个语义完全不同的寄存器）。
    /// </summary>
    private static bool 尝试解析(string 地址值, out int 地址) =>
        int.TryParse(地址值.Trim().TrimStart('D', 'd'), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out 地址);

    private static 标签数据类型 解析类型(string 类型文本) =>
        Enum.TryParse<标签数据类型>(类型文本, ignoreCase: true, out var 类型) ? 类型 : 标签数据类型.Float32;

    private string 分组名(点表定义 表) =>
        string.IsNullOrWhiteSpace(表.点动分组) ? 表.键 : 表.点动分组;

    private static bool 分组匹配(string 元件分组, string 页面分组) =>
        string.IsNullOrWhiteSpace(元件分组)
        || string.IsNullOrWhiteSpace(页面分组)
        || string.Equals(元件分组, 页面分组, StringComparison.OrdinalIgnoreCase);

    private IReadOnlyList<点动按钮项> 本页点动(string 分组) =>
        _配置.点动.Where(项 => 分组匹配(项.分组, 分组)).ToList();

    private 点动按钮项? 查找点动(string 轴号, string 分组) =>
        _配置.点动.FirstOrDefault(项 =>
            string.Equals(项.轴, 轴号, StringComparison.OrdinalIgnoreCase) && 分组匹配(项.分组, 分组));

    private 回零按钮项? 查找回零(string 轴号) =>
        _配置.回零.FirstOrDefault(项 => string.Equals(项.轴, 轴号, StringComparison.OrdinalIgnoreCase));

    private 动作按钮项? 查找动作按钮(string 键) =>
        _配置.动作按钮.FirstOrDefault(项 => string.Equals(项.键, 键, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 落盘前兜底校验：每个分组的辅助地址不得超过 <see cref="辅助地址项.每组上限"/>。
    /// <para><b>统计口径</b>：按 <see cref="辅助地址项.分组"/> 原样分组（<c>string.Empty</c>
    /// 作为独立的「全局」组），空分组项不摊进其它分组——它与页面规则一致（空分组视为全局、
    /// 所有页面都能看到），单独统计最安全，不会让某个具名分组莫名超标。</para>
    /// <para>界面侧已在新增前拦截；这里再挡一次，覆盖手工构造请求 / 直接改配置的场景。</para>
    /// </summary>
    private void 校验辅助地址上限()
    {
        foreach (var 组 in _配置.辅助地址.GroupBy(项 => 项.分组, StringComparer.OrdinalIgnoreCase))
        {
            if (组.Count() > 辅助地址项.每组上限)
            {
                var 显示分组名 = string.IsNullOrWhiteSpace(组.Key) ? "全局" : 组.Key;
                throw new InvalidOperationException(
                    $"「{显示分组名}」分组的辅助地址超过 {辅助地址项.每组上限} 个上限，已拒绝保存。");
            }
        }
    }

    private async Task 落盘(CancellationToken 取消令牌)
    {
        var JSON文本 = JsonSerializer.Serialize(_配置, JSON配置.写入人工风格);
        await 原子文件.异步写入全部文本(配置路径.配方配置, JSON文本, 取消令牌).ConfigureAwait(false);
    }
}
