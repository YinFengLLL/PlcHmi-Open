using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using 上位机.应用.服务;
using 上位机.核心.模型;

namespace 上位机.应用.视图模型;

/// <summary>
/// <see cref="标定视图模型"/> 的「动作按钮 / 辅助地址」部分（partial）。
///
/// <para>现场要求标定页采用与锁附示教页相同的按钮与地址配置方式：动作按钮与辅助地址
/// 同样按「标定」分组纳入统一地址编辑入口，管理员可新增、改名、改址与删除。</para>
/// </summary>
public sealed partial class 标定视图模型
{
    /// <summary>标定页的辅助地址 / 动作按钮分组名（与 recipe-config.json 保持一致）。</summary>
    private const string 标定分组 = "标定";

    /// <summary>
    /// 轴位置取自「锁附」分组的点动配置（标定页动的是锁附 X/Y/Z 三轴，
    /// 与锁附示教页同一套地址与位置点位）。
    /// </summary>
    private const string 锁附分组 = "锁附";

    /// <summary>
    /// 本页要显示的轴位置：锁附 X / Y / Z。与标定页的点动按钮同一组轴——
    /// 标定只动 X/Y/Z（<c>recipe-config.json</c> 里 ZZ 是锁附专有的第二 Z，标定不涉及）。
    /// </summary>
    private static readonly string[] 显示轴 = ["X", "Y", "Z"];

    /// <summary>
    /// 动作按钮写入版本：按钮每成功写一次自增（见 <see cref="异步触发动作"/>）。
    /// <para>周期回读在读之前取一份，读完发现被改过就丢弃这批快照——
    /// 否则"读在按之前、写在按之后"的时序会把刚点亮的按钮又翻回未激活。</para>
    /// </summary>
    private int _动作写入版本;

    /// <summary>动作按钮（按「标定」分组过滤；默认配置为空，管理员可新增）。</summary>
    public ObservableCollection<动作按钮视图模型> 动作按钮集合 { get; } = new();

    /// <summary>
    /// 锁附 X / Y / Z 轴当前位置（与锁附示教页同一套配置、同一套显示风格）。
    /// <para>标定时要一边看中心点数值一边看轴走到哪，原先这一页看不到实时位置。</para>
    /// </summary>
    public ObservableCollection<示教轴视图模型> 轴列表 { get; } = new();

    /// <summary>
    /// 按配置重建轴列表（与锁附示教页取同一份 <c>recipe-config.json</c> 点动配置）。
    /// <para><b>必须同时按分组过滤</b>：配置里点胶 X 轴的键也是 <c>X</c>，
    /// 只按轴键筛会得到两个 X（一个锁附、一个点胶），位置标签还各不相同。</para>
    /// </summary>
    private void 重建轴列表()
    {
        轴列表.Clear();

        foreach (var 项 in _配置.点动.Where(项 =>
                     页面分组.匹配(项.分组, 锁附分组)
                     && 显示轴.Contains(项.轴, StringComparer.OrdinalIgnoreCase)))
        {
            轴列表.Add(new 示教轴视图模型(项));
        }
    }

    /// <summary>轮询推送轴位置变化时刷新对应轴的显示（与锁附示教页同一套匹配方式）。</summary>
    private void 标签已变更(标签新值 值)
    {
        if (!值.是否有效)
        {
            return;
        }

        var 文本 = 坐标辅助.格式化(值.缩放后新值);

        foreach (var 轴项 in 轴列表.Where(甲 => 甲.位置标签 == 值.名称))
        {
            轴项.位置文本 = 文本;
        }
    }

    /// <summary>
    /// 进入页面时拉一次轴位置快照。
    /// <para>只订阅 <c>新值已变更</c> 是不够的：那是"变化才推送"，轴停着不动时页面会一直显示
    /// <c>--.--</c>（审查标准 R2：订阅事件流的页面必须在初始拉一次快照）。</para>
    /// </summary>
    private void 刷新轴位置快照()
    {
        foreach (var 轴项 in 轴列表)
        {
            if (轴项.位置标签.Length > 0 && _值列表.尝试获取双精度(轴项.位置标签, out var 位置值))
            {
                轴项.位置文本 = 坐标辅助.格式化(位置值);
            }
        }
    }

    /// <summary>
    /// 从 PLC 回读各动作按钮的线圈状态，让切换型按钮显示真实状态。
    /// <para>与两个示教页同一套做法：单项容错，读不到的按钮只跳过，不拦住整页初始化。
    /// 进入页面调一次，页面停留期间由 <see cref="主视图模型"/> 周期调一次
    /// （见 <see cref="动作按钮页接口"/>）——PLC 侧复位后不必离开页面再进来。</para>
    /// </summary>
    private async Task 异步同步动作按钮状态()
    {
        var 版本 = _动作写入版本;
        var 快照 = new List<(动作按钮视图模型 按钮, bool 激活)>();

        // 2026-11-19 性能改造：连续 M 区线圈合并成一次读，避免一页按钮连发独占串口闸锁。
        IReadOnlyList<bool?> 状态列表;

        try
        {
            状态列表 = await _手动
                .异步批量读取线圈(动作按钮集合.Select(按钮 => 按钮.定义.地址).ToList())
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException 异常)
        {
            _日志.LogWarning(异常, "批量回读动作按钮状态失败，本轮跳过");
            return;
        }
        catch (System.IO.IOException 异常)
        {
            _日志.LogWarning(异常, "批量回读动作按钮状态失败，本轮跳过");
            return;
        }
        catch (System.Net.Sockets.SocketException 异常)
        {
            _日志.LogWarning(异常, "批量回读动作按钮状态失败，本轮跳过");
            return;
        }

        for (var 序号 = 0; 序号 < 动作按钮集合.Count && 序号 < 状态列表.Count; 序号++)
        {
            if (状态列表[序号] is { } 激活)
            {
                快照.Add((动作按钮集合[序号], 激活));
            }
        }

        if (快照.Count == 0)
        {
            return;
        }

        // 读的途中操作员按过按钮：这批快照是"按之前"的旧值，应用它会把刚点亮的按钮又翻回去
        if (版本 != _动作写入版本)
        {
            return;
        }

        在界面线程执行(() =>
        {
            foreach (var (按钮, 激活) in 快照)
            {
                按钮.已激活 = 激活;
            }
        });
    }

    /// <inheritdoc />
    public Task 异步刷新动作按钮状态() => 异步同步动作按钮状态();

    /// <summary>辅助地址的周期回读（与动作按钮分开跑，见 <see cref="动作按钮页接口"/>）。</summary>
    public Task 异步刷新辅助地址状态() => _辅助地址写入.异步刷新值(辅助地址集合);

    /// <summary>
    /// 手动使能共享 + 写 M666（见 <see cref="手动使能寄存器"/>）。
    ///
    /// <para><b>2026-10-15 现场反馈：</b>原先标定页只有 <c>是否手动启用</c> 这个本页属性，
    /// 没接共享服务——在标定页打开使能，锁附示教页看不到（反之亦然），PLC 侧的 M666 也不置位。
    /// 现在与两个示教页同一套做法：改服务状态（三页靠事件一起变）+ 立即写一次 PLC，
    /// 之后由 <c>手动使能保持服务</c> 持续维持。</para>
    /// </summary>
    partial void On是否手动启用Changed(bool value)
    {
        // 与服务状态一致时直接返回：这通常是"由事件回写"的场景，继续往下会绕圈
        if (_手动.是否启用 == value)
        {
            return;
        }

        _手动.设置启用(value);
        异步安全调用.即发即忘(() => _手动.异步同步启用(), _日志, "同步手动使能状态到 PLC");

        // 辅助地址的当前值也归手动使能管（2026-11-12）
        _辅助地址写入.刷新可编辑(辅助地址集合);
    }

    /// <summary>辅助地址（按「标定」分组过滤；默认配置为空，管理员可新增）。</summary>
    public ObservableCollection<辅助地址行视图模型> 辅助地址集合 { get; } = new();

    /// <summary>按当前配置重建动作按钮与辅助地址行（构造、初始化与改地址后调用）。</summary>
    private void 重建元件区()
    {
        动作按钮集合.Clear();

        foreach (var 项 in _页面地址.动作按钮(标定分组))
        {
            动作按钮集合.Add(new 动作按钮视图模型(项));
        }

        辅助地址集合.Clear();

        foreach (var 项 in _页面地址.辅助地址(标定分组))
        {
            辅助地址集合.Add(_辅助地址写入.创建(项, _页面地址));
        }
    }

    /// <summary>读取全部辅助地址的当前值。</summary>
    private Task 异步读取辅助地址() =>
        // 2026-11-19 性能改造：与周期回读共用同一套合并读取
        辅助地址行视图模型.异步批量刷新(辅助地址集合);

    // ── 命令 ────────────────────────────────────────────────────────────

    /// <summary>读取全部辅助地址。</summary>
    [RelayCommand]
    private async Task 异步读取辅助地址值()
    {
        是否忙碌 = true;

        try
        {
            await 界面命令
                .异步执行(_日志, _对话框, "读取辅助地址", 异步读取辅助地址)
                .ConfigureAwait(true);
        }
        finally
        {
            是否忙碌 = false;
        }
    }

    // 辅助地址的写入不再有按钮：填完回车 / 失焦即写（见 辅助地址实时写入手柄 与 输入提交手势）。

    /// <summary>
    /// 切换一个动作按钮：首次按下<b>置位</b>（激活），再次按下<b>复位</b>（取消激活）。
    /// <para>与两个示教页同一套交互、同一套按钮外观（切换型、无二次确认）。</para>
    /// </summary>
    [RelayCommand]
    private async Task 异步触发动作(动作按钮视图模型? 按钮项)
    {
        if (按钮项 is null)
        {
            return;
        }

        // 目标状态 = 取反：按一次切换一次
        var 目标激活 = !按钮项.已激活;

        是否忙碌 = true;

        try
        {
            if (await 界面命令
                    .异步执行(
                        _日志,
                        _对话框,
                        目标激活 ? $"激活「{按钮项.名称}」" : $"取消激活「{按钮项.名称}」",
                        () => _手动.异步写入线圈(按钮项.定义.地址, 目标激活))
                    .ConfigureAwait(true))
            {
                // 写入成功才改按钮状态：失败时按钮不该假装已激活
                按钮项.已激活 = 目标激活;
                _动作写入版本++;
            }
        }
        finally
        {
            是否忙碌 = false;
        }
    }
}
