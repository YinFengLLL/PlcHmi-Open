using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using 上位机.应用.服务;
using 上位机.核心.抽象;
using 上位机.核心.模型;

namespace 上位机.应用.视图模型;

/// <summary>
/// 手动调试页 ViewModel。
///
/// <para><b>元件配置驱动：</b>气缸 / 按钮 / 指示灯来自 <c>manual-widgets.json</c>，
/// 管理员可在界面上增删改。工程师及以下只能操作不能改配置。</para>
/// </summary>
public sealed partial class 手动视图模型 : 视图模型基类
{
    private readonly 手动控件服务接口 _手动;
    private readonly 手动元件服务接口 _元件;
    private readonly 用户服务接口 _用户项;
    private readonly 标签值服务接口 _值列表;
    private readonly 对话框服务接口 _对话框;
    private readonly PLC连接选项? _PLC选项;
    private readonly ILogger<手动视图模型> _日志;

    [ObservableProperty] private bool _是否手动启用;
    [ObservableProperty] private 轴视图模型? _选中轴;
    [ObservableProperty] private string _当前位置文本 = "--.--";
    [ObservableProperty] private bool _可编辑元件;
    [ObservableProperty] private int _点动速度等级 = 点动速度等级地址.最小等级;

    /// <summary>
    /// 各轴最近一次写入的点动速度档位。
    /// <para><b>这是上位机的记忆，不是 PLC 实时回读</b>——与气缸、参数页按钮表同一约定。
    /// 若有人在 PLC 侧改过档位，界面会与设备不一致，按一次即可纠正。</para>
    /// </summary>
    private readonly Dictionary<string, int> _点动等级 = new(StringComparer.OrdinalIgnoreCase);

    public 手动视图模型(
        手动控件服务接口 手动,
        手动元件服务接口 元件列表,
        用户服务接口 用户,
        标签值服务接口 值列表,
        对话框服务接口 对话框集合,
        配方配置 配置对象,
        ILogger<手动视图模型> 日志,
        PLC连接选项? PLC选项 = null)
    {
        _手动 = 手动;
        _元件 = 元件列表;
        _用户项 = 用户;
        _值列表 = 值列表;
        _对话框 = 对话框集合;
        _PLC选项 = PLC选项;
        _日志 = 日志;

        标题 = "手动调试";
        副标题 = "元件手动与六轴点动 · 需先开启手动使能";

        // 六轴（锁附 X/Y/Z/ZZ + 点胶 X/Z）由配置驱动：点动线圈与位置回读点位都在
        // recipe-config.json 的 jogButtons 里，地址改了页面跟着变，不在代码里写死。
        foreach (var 轴项 in 配置对象.点动
                     .GroupBy(项 => 项.轴, StringComparer.OrdinalIgnoreCase)
                     .Select(组 => 组.First()))
        {
            轴列表.Add(new 轴视图模型(轴项));
        }

        if (轴列表.Count == 0)
        {
            // 配置缺失时不至于空页面崩溃；由界面提示"未配置点动轴"
            轴列表.Add(new 轴视图模型(new 点动按钮项 { 轴 = "X", 名称 = "X 轴" }));
        }

        _选中轴 = 轴列表[0];
        _选中轴.是否选中 = true;

        _值列表.新值已变更 += (_, 值) => 在界面线程执行(() =>
        {
            foreach (var 轴号 in 轴列表.Where(甲 => 甲.位置标签 == 值.名称))
            {
                if (值.是否有效)
                {
                    轴号.位置文本 = 值.缩放后新值.ToString("0.00");
                }
            }

            if (选中轴 is not null && 选中轴.位置标签 == 值.名称 && 值.是否有效)
            {
                当前位置文本 = 选中轴.位置文本;
            }
        });

        _元件.元件已变更 += (_, _) => 在界面线程执行(重建元件);
        _用户项.用户已变更 += (_, _) => 在界面线程执行(刷新权限);

        // 使能开关与示教页共用同一个服务，靠事件保持两页显示一致
        _手动.启用已变更 += (_, 启用) => 在界面线程执行(() => 是否手动启用 = 启用);

        // 只推变化的事件订阅方必须在构造时拉一次快照，否则会漏掉初始状态
        是否手动启用 = _手动.是否启用;

        重建元件();
        刷新权限();
    }

    /// <summary>手动页元件列表（配置驱动）。</summary>
    public ObservableCollection<手动元件视图模型> 元件集合 { get; } = new();

    /// <summary>轴列表。</summary>
    public ObservableCollection<轴视图模型> 轴列表 { get; } = new();

    /// <summary>当前是否有元件（空时界面显示提示）。</summary>
    public bool 有元件 => 元件集合.Count > 0;

    /// <inheritdoc />
    public override Task 异步初始化()
    {
        重建元件();
        刷新权限();
        return Task.CompletedTask;
    }

    // ── 手动使能 ────────────────────────────────────────────────────────

    partial void On是否手动启用Changed(bool value)
    {
        // 与服务状态一致时直接返回：这通常是“由事件回写”的场景，
        // 继续往下会绕圈（服务 → 事件 → 属性 → 服务）。
        if (_手动.是否启用 == value)
        {
            return;
        }

        _手动.设置启用(value);

        // 原写法把 `_ = SyncEnableAsync()` 包在 try/catch 里——捕不到：
        // TagWriter 的写入失败发生在 await 之后，落在 Task 上。交给 AsyncSafe 记录。
        异步安全调用.即发即忘(
            () => _手动.异步同步启用(),
            _日志,
            "同步手动使能状态到 PLC");
    }

    // ── 元件操作 ────────────────────────────────────────────────────────

    /// <summary>选中轴。</summary>
    [RelayCommand]
    private void 选择轴(轴视图模型? 轴号)
    {
        if (轴号 is not null)
        {
            选中轴 = 轴号;
        }
    }

    /// <summary>
    /// 切换选中轴时同步选中标记与位置显示。
    ///
    /// <para>放在属性回调里而不是命令里：这样任何路径改 <see cref="选中轴"/>
    /// 都能保持标记一致，不会出现"两个轴同时高亮"或"点不动"。</para>
    /// </summary>
    partial void On选中轴Changed(轴视图模型? oldValue, 轴视图模型? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.是否选中 = false;
        }

        if (newValue is not null)
        {
            newValue.是否选中 = true;
            当前位置文本 = newValue.位置文本;
        }

        // 档位随轴切换：显示该轴最近一次写入的值
        刷新点动速度等级();
    }

    /// <summary>元件动作：伸出 / 缩回（按当前状态自动反向）。</summary>
    [RelayCommand]
    private async Task 异步切换元件(手动元件视图模型? 元件项)
    {
        if (元件项 is null)
        {
            return;
        }

        var 动作 = 元件项.是否指示灯
            ? null
            : 元件项.是否活动 ? "缩回" : "伸出";

        if (动作 is null)
        {
            return;
        }

        // 不再弹确认框（现场口径 2026-10-13）：这里本来就是"按一下伸出、再按一下缩回"的切换，
        // 每次弹窗既拖慢节奏，也让人对确认框麻木——真出问题时反而看不见。
        // 元件当前状态在按钮上直接可见（活动/停止），比弹窗更直观。
        await 异步运行手动($"{元件项.名称} {动作}", async () =>
        {
            if (元件项.是否活动)
            {
                await 元件项.异步停用().ConfigureAwait(false);
            }
            else
            {
                await 元件项.异步激活().ConfigureAwait(false);
            }
        });
    }

    // ── 元件配置管理（仅管理员） ────────────────────────────────────────

    /// <summary>新增元件。</summary>
    [RelayCommand]
    private async Task 异步添加元件()
    {
        if (!可编辑元件)
        {
            _对话框.提示("权限不足", "只有管理员可以增删手动页元件。");
            return;
        }

        var 元件项 = new 手动元件定义
        {
            标识 = _元件.新标识(手动元件类型.气缸),
            类型 = 手动元件类型.气缸,
            名称 = "新气缸",
            顺序 = 元件集合.Count + 1,
            地址 = "M",
        };

        if (!_对话框.编辑手动元件(元件项))
        {
            return;
        }

        var 校验项 = _元件.校验(元件项);

        if (!校验项.是否有效)
        {
            _对话框.错误信息("校验失败", 校验项.摘要);
            return;
        }

        _元件.添加(元件项);

        try
        {
            await _元件.异步保存().ConfigureAwait(false);
            _对话框.提示("已添加", $"元件「{元件项.名称}」已添加并保存。");
        }
        catch (Exception 异常)
        {
            _日志.LogError(异常, "保存元件失败");
            _对话框.错误信息("保存失败", 异常.Message);
        }
    }

    /// <summary>编辑元件（改名称、类型、绑定地址）。</summary>
    [RelayCommand]
    private async Task 异步编辑元件(手动元件视图模型? 元件项)
    {
        if (元件项 is null)
        {
            return;
        }

        if (!可编辑元件)
        {
            _对话框.提示("权限不足", "只有管理员可以修改元件的绑定地址。");
            return;
        }

        var 已编辑 = 元件项.定义.克隆();
        已编辑.标识 = 元件项.标识;

        if (!_对话框.编辑手动元件(已编辑))
        {
            return;
        }

        var 校验项 = _元件.校验(已编辑);

        if (!校验项.是否有效)
        {
            _对话框.错误信息("校验失败", 校验项.摘要);
            return;
        }

        _元件.更新(已编辑);

        try
        {
            await _元件.异步保存().ConfigureAwait(false);
            _对话框.提示("已保存", $"元件「{已编辑.名称}」已更新。");
        }
        catch (Exception 异常)
        {
            _日志.LogError(异常, "保存元件失败");
            _对话框.错误信息("保存失败", 异常.Message);
        }
    }

    /// <summary>删除元件。</summary>
    [RelayCommand]
    private async Task 异步移除元件(手动元件视图模型? 元件项)
    {
        if (元件项 is null)
        {
            return;
        }

        if (!可编辑元件)
        {
            _对话框.提示("权限不足", "只有管理员可以删除手动页元件。");
            return;
        }

        if (!_对话框.确认("删除元件", $"确认删除元件「{元件项.名称}」？\n\n此操作只影响界面配置，不会改动 PLC。"))
        {
            return;
        }

        _元件.移除(元件项.标识);

        try
        {
            await _元件.异步保存().ConfigureAwait(false);
        }
        catch (Exception 异常)
        {
            _日志.LogError(异常, "保存元件失败");
            _对话框.错误信息("保存失败", 异常.Message);
        }
    }

    // ── JOG（按住才动、松开即停） ───────────────────────────────────────

    /// <summary>
    /// 按住方向按钮：启动点动。
    /// <para>刻意不做成 Command——"按住 / 松开"需要 MouseDown / MouseUp，
    /// 由 View 的 <c>JogGesture</c> 手势助手调用本方法。</para>
    /// </summary>
    public void 开始点动(int 方向) =>
        异步安全调用.即发即忘(
            () => 异步启动点动核心(方向),
            _日志,
            $"{选中轴?.名称 ?? "当前轴"}启动点动");

    /// <summary>
    /// 松开方向按钮：停止点动。
    /// <para><b>无条件生效</b>：刻意不走 <c>RunManualAsync</c>（那会先检查手动使能）。
    /// 若使能意外断开就拒绝停止，轴会一直动下去——那是安全事故。</para>
    /// </summary>
    public void 结束点动() =>
        异步安全调用.即发即忘(
            异步停止点动核心,
            _日志,
            $"{选中轴?.名称 ?? "当前轴"}停止点动");

    private async Task 异步启动点动核心(int 方向)
    {
        if (选中轴 is null)
        {
            return;
        }

        var 轴号 = 选中轴.轴键;
        var 标签名 = 方向 > 0 ? "正向" : "负向";

        await 异步运行手动($"{轴号} 轴{标签名}点动", () =>
            _手动.异步点动轴(轴号, 方向)).ConfigureAwait(false);
    }

    private async Task 异步停止点动核心()
    {
        if (选中轴 is null)
        {
            return;
        }

        try
        {
            await _手动.异步停止点动轴(选中轴.轴键).ConfigureAwait(false);
        }
        catch (Exception 异常)
        {
            _日志.LogWarning(异常, "停止点动失败");
        }
    }

    // ── 点动速度档位（随轴切换） ────────────────────────────────────────

    /// <summary>低速档是否选中。</summary>
    public bool 是否点动低 => 点动速度等级 == 点动速度等级地址.最小等级;

    /// <summary>中速档是否选中。</summary>
    public bool 是否点动中等 => 点动速度等级 == 点动速度等级地址.最小等级 + 1;

    /// <summary>高速档是否选中。</summary>
    public bool 是否点动高 => 点动速度等级 == 点动速度等级地址.最大等级;

    partial void On点动速度等级Changed(int value)
    {
        OnPropertyChanged(nameof(是否点动低));
        OnPropertyChanged(nameof(是否点动中等));
        OnPropertyChanged(nameof(是否点动高));
    }

    /// <summary>把档位显示同步成当前轴记忆的值（没有记忆时回到低速）。</summary>
    private void 刷新点动速度等级()
    {
        var 轴号 = 选中轴?.轴键 ?? string.Empty;

        点动速度等级 = _点动等级.TryGetValue(轴号, out var 等级)
            ? 等级
            : 点动速度等级地址.最小等级;
    }

    /// <summary>
    /// 写入点动速度档位：1 低速 / 2 中速 / 3 高速。
    /// <para>档位是参数设定而非动作机构，所以不要求手动使能（与参数页延时/按钮表一致）。</para>
    /// </summary>
    /// <param name="level">档位值文本，由按钮的 <c>CommandParameter</c> 传入。</param>
    [RelayCommand]
    private async Task 异步设置点动速度(string 等级)
    {
        if (选中轴 is null)
        {
            return;
        }

        if (!int.TryParse(等级, NumberStyles.Integer, CultureInfo.InvariantCulture, out var 已解析)
            || !点动速度等级地址.是否有效等级(已解析))
        {
            return;
        }

        var 轴号 = 选中轴.轴键;

        try
        {
            是否忙碌 = true;

            await _手动.异步设置点动速度等级(轴号, 已解析).ConfigureAwait(false);

            _点动等级[轴号] = 已解析;

            在界面线程执行(() => 点动速度等级 = 已解析);
            _日志.LogInformation("{Axis} 轴点动速度档位：{Level}", 轴号, 已解析);
        }
        catch (Exception 异常)
        {
            _日志.LogError(异常, "{Axis} 轴点动速度档位写入失败", 轴号);
            在界面线程执行(() => _对话框.错误信息("档位写入失败", 异常.Message));
        }
        finally
        {
            是否忙碌 = false;
        }
    }

    // ── 内部 ────────────────────────────────────────────────────────────

    /// <summary>按配置重建元件列表。</summary>
    private void 重建元件()
    {
        元件集合.Clear();

        var 变量寻址 = 绑定显示.是否变量寻址(_PLC选项);

        foreach (var 定义项 in _元件.元件集合)
        {
            元件集合.Add(new 手动元件视图模型(定义项, _手动, 变量寻址));
        }

        OnPropertyChanged(nameof(有元件));
    }

    private void 刷新权限() => 可编辑元件 = _用户项.有(权限.编辑手动元件);

    private async Task 异步运行手动(string 动作, Func<Task> 命令)
    {
        if (!是否手动启用)
        {
            _对话框.提示("操作被拒绝", "请先开启手动使能，再执行手动动作。");
            return;
        }

        try
        {
            是否忙碌 = true;
            await 命令().ConfigureAwait(false);
            _日志.LogInformation("已执行：{Action}", 动作);
        }
        catch (Exception 异常)
        {
            _日志.LogError(异常, "{Action} 执行失败", 动作);
            _对话框.错误信息("操作失败", 异常.Message);
        }
        finally
        {
            是否忙碌 = false;
        }
    }
}
