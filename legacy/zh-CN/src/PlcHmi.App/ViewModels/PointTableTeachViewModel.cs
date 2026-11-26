using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using 上位机.应用.服务;
using 上位机.核心.抽象;
using 上位机.核心.模型;

namespace 上位机.应用.视图模型;

/// <summary>
/// 点表示教页 ViewModel 基类。锁附示教与点胶示教共用同一套读写、
/// 点动、回零与坐标示教逻辑，仅点表配置不同（列不同）。
///
/// <para><b>写入时机：</b>「坐标示教」只写当前点，「参数写入」把机种区段 D2500–D3999 整段写 PLC 并存机种；
/// 输入框改动只改内存。</para>
/// </summary>
public abstract partial class 点表示教视图模型 : 视图模型基类, 动作按钮页接口
{
    private readonly 手动控件服务接口 _手动;
    private readonly 用户服务接口 _用户项;
    private readonly 标签值服务接口 _值列表;
    private readonly 对话框服务接口 _对话框;
    private readonly 页面上下文服务接口 _页面上下文;
    private readonly ILogger _日志;

    [ObservableProperty] private int _选中序号;
    [ObservableProperty] private 示教轴视图模型? _选中轴;
    [ObservableProperty] private bool _是否手动启用;
    [ObservableProperty] private bool _可编辑地址;
    [ObservableProperty] private string _序号摘要 = string.Empty;

    /// <summary>
    /// 辅助地址区顶部的"当前点"提示（结构体数组模式）：如
    /// <c>当前为第 3 点 · 地址 D3516–D3523（基址 D3500 + 点序号 × 8）</c>。
    /// <para>固定地址页面（如锁附页的拍照点）为空，界面上不占位。</para>
    /// </summary>
    [ObservableProperty] private string _辅助地址摘要 = string.Empty;
    [ObservableProperty] private string _当前位置文本 = "--.--";

    protected 点表示教视图模型(
        string 表键值,
        int 点码寄存器值,
        点表服务接口 点表服务项,
        页面地址服务接口 页面地址服务项,
        配方配置 配置对象,
        手动控件服务接口 手动,
        用户服务接口 用户,
        标签值服务接口 值列表,
        对话框服务接口 对话框,
        页面上下文服务接口 页面上下文,
        ILogger 日志)
    {
        表键 = 表键值;
        点码寄存器 = 点码寄存器值;
        点表服务 = 点表服务项;
        页面地址服务 = 页面地址服务项;
        _手动 = 手动;
        _用户项 = 用户;
        _值列表 = 值列表;
        _对话框 = 对话框;
        _页面上下文 = 页面上下文;
        _日志 = 日志;

        // 允许写入的判定读的是本页的"手动使能"（与点动 / 回零同一道门）
        _辅助地址写入 = new 辅助地址实时写入手柄(日志, 对话框, () => 是否手动启用);

        表定义 = 点表服务.表(表键);
        分组 = 表定义.点动分组.Length == 0 ? 表定义.键 : 表定义.点动分组;

        for (var 序号 = 0; 序号 < 表定义.点数; 序号++)
        {
            行集合.Add(new 点值行视图模型(表定义, 序号, 点表服务, 异步选中点位));
        }

        // 只显示本页分组的轴：锁附页 = 锁附 X/Y/Z/ZZ；点胶页 = 点胶 X/Y/Z（Y 与锁附 Y 共轴）
        foreach (var 项 in 配置对象.点动.Where(项 => 页面分组.匹配(项.分组, 分组)))
        {
            轴列表.Add(new 示教轴视图模型(项));
        }

        foreach (var 项 in 页面地址服务.动作按钮(分组))
        {
            动作按钮集合.Add(new 动作按钮视图模型(项));
        }

        foreach (var 项 in 页面地址服务.辅助地址(分组))
        {
            辅助地址集合.Add(创建辅助地址行(项));
        }

        if (轴列表.Count > 0)
        {
            _选中轴 = 轴列表[0];
            _选中轴.是否选中 = true;
        }

        点表服务.点表已变更 += (_, 键) =>
        {
            if (string.Equals(键, 表键值, StringComparison.OrdinalIgnoreCase))
            {
                在界面线程执行(刷新行);
            }
        };

        _值列表.新值已变更 += (_, 值) => 在界面线程执行(() => 标签已变更(值));
        _手动.启用已变更 += (_, 启用) => 在界面线程执行(() => 是否手动启用 = 启用);
        _用户项.用户已变更 += (_, _) => 在界面线程执行(刷新权限);

        是否手动启用 = _手动.是否启用;
        刷新权限();
        刷新序号摘要();
        刷新行();
    }

    /// <summary>本页对应的点表键。</summary>
    protected string 表键 { get; }

    /// <summary>本页的元件分组（锁附 / 点胶）。</summary>
    protected string 分组 { get; }

    /// <summary>点表服务。</summary>
    protected 点表服务接口 点表服务 { get; }

    /// <summary>页面地址服务（地址编辑与辅助地址读写）。</summary>
    protected 页面地址服务接口 页面地址服务 { get; }

    /// <summary>点表定义。</summary>
    public 点表定义 表定义 { get; }

    /// <summary>点表行集合。</summary>
    public ObservableCollection<点值行视图模型> 行集合 { get; } = new();

    /// <summary>点动轴列表（配置驱动，按本页分组过滤）。</summary>
    public ObservableCollection<示教轴视图模型> 轴列表 { get; } = new();

    /// <summary>动作按钮（按本页分组过滤）。</summary>
    public 批量可观察集合<动作按钮视图模型> 动作按钮集合 { get; } = new();

    /// <summary>辅助地址（按本页分组过滤，管理员可增删改）。</summary>
    public ObservableCollection<辅助地址行视图模型> 辅助地址集合 { get; } = new();

    // ── 初始化 ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    public override async Task 异步初始化()
    {
        await 点表服务.异步加载点表(表键).ConfigureAwait(false);
        await 异步读取辅助地址().ConfigureAwait(false);
        await 异步刷新本页状态().ConfigureAwait(false);   // 状态灯 + 动作按钮（见 *.Lamp.cs）
        await 异步同步当前点号().ConfigureAwait(false);

        在界面线程执行(() =>
        {
            刷新权限();
            刷新行();
            刷新轴位置快照();
        });
    }

    // ── 序号与选中 ──────────────────────────────────────────────────────

    partial void On选中序号Changed(int value) => 刷新序号摘要();

    /// <summary>上一个点。</summary>
    [RelayCommand]
    private void 上一个序号() => 移动序号(-1);

    /// <summary>下一个点。</summary>
    [RelayCommand]
    private void 下一个序号() => 移动序号(1);

    private void 移动序号(int 增量)
    {
        var 总数 = 表定义.点数;
        var 下一个 = (选中序号 + 增量 + 总数) % 总数;
        选中序号 = 下一个;
        刷新行();
    }

    /// <summary>点击点表行：把该行设为当前点。</summary>
    private Task 异步选中点位(int 序号)
    {
        选中序号 = 序号;
        刷新行();
        return Task.CompletedTask;
    }

    // ── 点动轴 ──────────────────────────────────────────────────────────

    /// <summary>选中要点动的轴。</summary>
    [RelayCommand]
    private void 选择轴(示教轴视图模型? 轴项)
    {
        if (轴项 is not null)
        {
            选中轴 = 轴项;
        }
    }

    partial void On选中轴Changed(示教轴视图模型? oldValue, 示教轴视图模型? newValue)
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
    }

    /// <summary>
    /// 手动使能开关（与手动调试页共用同一个服务）。
    /// </summary>
    partial void On是否手动启用Changed(bool value)
    {
        if (_手动.是否启用 == value)
        {
            return;
        }

        _手动.设置启用(value);
        异步安全调用.即发即忘(() => _手动.异步同步启用(), _日志, "同步手动使能状态到 PLC");

        // 辅助地址的当前值也归手动使能管：未使能只能看，开了才能改（2026-11-12）
        刷新辅助地址可编辑();
    }

    // ── JOG ─────────────────────────────────────────────────────────────

    /// <summary>按住方向按钮：启动点动。</summary>
    public void 开始点动(int 方向)
    {
        if (选中轴 is null)
        {
            return;
        }

        异步安全调用.即发即忘(
            () => 异步启动点动核心(选中轴.轴键, 方向),
            _日志,
            $"{选中轴.名称}启动点动");
    }

    /// <summary>松开方向按钮：停止点动（无条件生效）。</summary>
    public void 结束点动()
    {
        if (选中轴 is null)
        {
            return;
        }

        异步安全调用.即发即忘(
            () => _手动.异步停止点动轴(选中轴.轴键),
            _日志,
            $"{选中轴.名称}停止点动");
    }

    /// <summary>按住指定轴的方向按钮：启动点动（统一方向键盘，Tag="轴键,±1"）。</summary>
    public void 开始点动(string 轴键, int 方向)
    {
        异步安全调用.即发即忘(
            () => 异步启动点动核心(轴键, 方向),
            _日志,
            $"{轴键} 启动点动");
    }

    /// <summary>松开指定轴的方向按钮：停止点动（无条件生效）。</summary>
    public void 结束点动(string 轴键)
    {
        异步安全调用.即发即忘(
            () => _手动.异步停止点动轴(轴键),
            _日志,
            $"{轴键} 停止点动");
    }

    private async Task 异步启动点动核心(string 轴键, int 方向)
    {
        if (!是否手动启用)
        {
            _对话框.提示("操作被拒绝", "请先开启手动使能，再执行点动。");
            return;
        }

        await 界面命令.异步执行(_日志, _对话框, $"{轴键} 点动", () => _手动.异步点动轴(轴键, 方向));
    }

    /// <summary>回零（二次确认后只置位 M 区线圈）。</summary>
    [RelayCommand]
    private async Task 异步回零()
    {
        if (选中轴 is null)
        {
            return;
        }

        if (!是否手动启用)
        {
            _对话框.提示("操作被拒绝", "请先开启手动使能，再执行回零。");
            return;
        }

        if (!_对话框.确认("轴回零",
                $"确认让「{选中轴.名称}」执行回零？\n\n回零过程中该轴将移动，确认现场无人员处于动作范围。",
                视图.确认等级.警告, "回零"))
        {
            return;
        }

        是否忙碌 = true;

        try
        {
            if (await 界面命令
                    .异步执行(_日志, _对话框, "回零", () => _手动.异步回零(选中轴.轴键))
                    .ConfigureAwait(true))
            {
                _对话框.提示("已下发", $"「{选中轴.名称}」回零命令已下发，请观察轴动作。");
            }
        }
        finally
        {
            是否忙碌 = false;
        }
    }

    // ── 辅助地址：见 PointTableTeachViewModel.Aux.cs（结构体数组按点寻址）──

    /// <summary>
    /// 按当前配置重建动作按钮（改地址后刷新用）。
    /// <para><b>必须重建</b>：管理员可在「编辑地址」里新增 / 删除动作按钮，
    /// 只重建辅助地址会漏掉按钮区的增删，表现为"保存了但按钮没出来"。</para>
    /// </summary>
    protected void 重建动作按钮()
    {
        动作按钮集合.替换全部(页面地址服务.动作按钮(分组)
            .Select(项 => new 动作按钮视图模型(项)));
    }

    // ── 刷新 ────────────────────────────────────────────────────────────

    /// <summary>刷新管理员权限可见性。</summary>
    protected void 刷新权限() => 可编辑地址 = _用户项.有(权限.编辑绑定地址);

    private void 刷新序号摘要()
    {
        序号摘要 = $"第 {选中序号 + 1} 点 ／ 共 {表定义.点数} 点";

        // 序号一变就同步点码给 PLC（本页自己的寄存器）：点选 / 上一下一点 / 点击行都在这里汇合
        异步安全调用.即发即忘(() => _页面上下文.异步写入点号(点码寄存器, 选中序号), _日志, "写入点码");

        // 结构体数组（点胶单点工艺参数）随点切换：地址换一份、值重新读一遍
        刷新辅助地址点号();

        // 指令码下拉跟当前点走（见 *.InstructionCode.cs）
        同步指令码下拉();
    }


    private void 刷新行()
    {
        var 点列表 = 点表服务.当前点表(表键);

        for (var 条目 = 0; 条目 < 行集合.Count; 条目++)
        {
            var 行项 = 行集合[条目];
            var 点项 = 条目 < 点列表.Count ? 点列表[条目] : new 点值行 { 序号 = 条目 };

            行项.加载(点项);
            行项.是否当前 = 行项.序号 == 选中序号;
        }

        // 点表数据变了（清空 / 下发 / 载入机种）：下拉框跟着刷新，不停在旧值
        同步指令码下拉();
    }

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

        if (选中轴 is not null && 选中轴.位置标签 == 值.名称)
        {
            当前位置文本 = 文本;
        }
    }
}
