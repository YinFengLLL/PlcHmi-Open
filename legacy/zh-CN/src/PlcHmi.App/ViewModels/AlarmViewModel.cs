using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using 上位机.应用.服务;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.应用.视图模型;

/// <summary>报警行的视图模型。</summary>
public sealed partial class 报警条目视图模型 : ObservableObject
{
    [ObservableProperty] private bool _是否已确认;

    public 报警条目视图模型(报警条目 模型对象)
    {
        模型 = 模型对象;
        _是否已确认 = 模型对象.是否已确认;
    }

    public 报警条目 模型 { get; }

    public string 代码 => 模型.代码;
    public string 消息 => 模型.消息;
    public string 时间文本 => 模型.已触发.ToString("HH:mm:ss");
    public string 已清除文本 => 模型.已清除?.ToString("HH:mm:ss") ?? "--";

    /// <summary>等级中文名。</summary>
    public string 等级文本 => 模型.等级 switch
    {
        报警等级.紧急 => "紧急",
        报警等级.警告 => "警告",
        _ => "提示",
    };

    /// <summary>等级前景色资源键。</summary>
    public string 等级颜色键 => 模型.等级 switch
    {
        报警等级.紧急 => "DangerBrush",
        报警等级.警告 => "WarningBrush",
        _ => "PrimaryLightBrush",
    };

    /// <summary>等级背景色资源键。</summary>
    public string 等级背景键 => 模型.等级 switch
    {
        报警等级.紧急 => "DangerSoftBrush",
        报警等级.警告 => "WarningSoftBrush",
        _ => "PrimarySoftBrush",
    };

    partial void On是否已确认Changed(bool value) => 模型.是否已确认 = value;
}

/// <summary>报警管理页 ViewModel。</summary>
public sealed partial class 报警视图模型 : 视图模型基类
{
    private readonly 报警服务接口 _报警;
    private readonly 报警定义服务接口 _定义;
    private readonly 用户服务接口 _用户项;
    private readonly 对话框服务接口 _对话框;
    private readonly ILogger<报警视图模型> _日志;

    [ObservableProperty] private int _活动数量;
    [ObservableProperty] private bool _显示历史;
    [ObservableProperty] private bool _可编辑报警;

    public 报警视图模型(
        报警服务接口 报警列表,
        报警定义服务接口 定义列表,
        用户服务接口 用户,
        对话框服务接口 对话框集合,
        ILogger<报警视图模型> 日志)
    {
        _报警 = 报警列表;
        _定义 = 定义列表;
        _用户项 = 用户;
        _对话框 = 对话框集合;
        _日志 = 日志;

        标题 = "报警记录";
        副标题 = "活动报警与历史记录 · 报警源为 PLC 的 D550–D554 位与 M500–M600";

        _报警.报警已触发 += (_, _) => 在界面线程执行(刷新);
        _报警.报警已清除 += (_, _) => 在界面线程执行(刷新);
        _定义.定义已变更 += (_, _) => 在界面线程执行(重建定义);
        _用户项.用户已变更 += (_, _) => 在界面线程执行(刷新权限);

        刷新();
        重建定义();
        刷新权限();
    }

    /// <summary>报警定义（PLC 地址 → 报警文本的映射）。</summary>
    public 批量可观察集合<报警定义> 定义集合 { get; } = new();

    /// <summary>已启用的报警条数。</summary>
    public string 定义摘要 =>
        $"共 {定义集合.Count} 条，已启用 {定义集合.Count(条目项 => 条目项.启用)} 条（上限 100 条）";

    /// <summary>活动报警。</summary>
    /// <remarks>
    /// 用 <see cref="批量可观察集合{T}"/>：报警事件会整表重建，
    /// 逐条 Add 在报警风暴时会放大成每秒数百次布局。
    /// </remarks>
    public 批量可观察集合<报警条目视图模型> 活动报警 { get; } = new();

    /// <summary>历史报警（报警风暴时同样整表重建，见 <see cref="刷新"/>）。</summary>
    public 批量可观察集合<报警条目视图模型> 历史 { get; } = new();

    /// <inheritdoc />
    public override Task 异步初始化()
    {
        刷新();
        重建定义();
        刷新权限();
        return Task.CompletedTask;
    }

    // ── 报警定义管理（仅管理员） ────────────────────────────────────────

    /// <summary>新增一条报警定义。</summary>
    [RelayCommand]
    private async Task 异步添加报警()
    {
        if (!可编辑报警)
        {
            _对话框.提示("权限不足", "只有管理员可以修改报警配置。");
            return;
        }

        var 报警项 = new 报警定义
        {
            标识 = _定义.新标识(),
            等级 = 报警等级.警告,
            消息 = "新报警",
            地址 = "M500",
            位 = -1,
            启用 = true,
        };

        if (!_对话框.编辑报警定义(报警项, 是否新建: true))
        {
            return;
        }

        var 校验项 = _定义.校验(报警项);

        if (!校验项.是否有效)
        {
            _对话框.错误信息("校验失败", 校验项.摘要);
            return;
        }

        _定义.添加(报警项);
        await 异步保存定义($"已新增报警「{报警项.标识}」。").ConfigureAwait(false);
    }

    /// <summary>编辑一条报警定义（改文本、改地址、改等级；编号是主键，编辑时锁定）。</summary>
    [RelayCommand]
    private async Task 异步编辑报警(报警定义? 报警项)
    {
        if (报警项 is null)
        {
            return;
        }

        if (!可编辑报警)
        {
            _对话框.提示("权限不足", "只有管理员可以修改报警配置。");
            return;
        }

        var 已编辑 = 报警项.克隆();

        if (!_对话框.编辑报警定义(已编辑, 是否新建: false))
        {
            return;
        }

        // isUpdate: true —— 编辑的是克隆副本，同 Id 的条目就是自身，不能报「已被占用」。
        var 校验项 = _定义.校验(已编辑, 是否更新: true);

        if (!校验项.是否有效)
        {
            _对话框.错误信息("校验失败", 校验项.摘要);
            return;
        }

        _定义.更新(已编辑);
        await 异步保存定义($"报警「{已编辑.标识}」已更新。").ConfigureAwait(false);
    }

    /// <summary>删除一条报警定义。</summary>
    [RelayCommand]
    private async Task 异步移除报警(报警定义? 报警项)
    {
        if (报警项 is null)
        {
            return;
        }

        if (!可编辑报警)
        {
            _对话框.提示("权限不足", "只有管理员可以修改报警配置。");
            return;
        }

        if (!_对话框.确认("删除报警",
                $"确认删除报警「{报警项.标识} {报警项.消息}」？\n\n此操作只影响上位机的报警配置，不会改动 PLC。"))
        {
            return;
        }

        _定义.移除(报警项.标识);
        await 异步保存定义($"报警「{报警项.标识}」已删除。").ConfigureAwait(false);
    }

    /// <summary>启用 / 停用一条报警。</summary>
    [RelayCommand]
    private async Task 异步切换报警(报警定义? 报警项)
    {
        if (报警项 is null || !可编辑报警)
        {
            return;
        }

        var 已编辑 = 报警项.克隆();
        已编辑.启用 = !已编辑.启用;

        _定义.更新(已编辑);
        await 异步保存定义(null).ConfigureAwait(false);
    }

    private async Task 异步保存定义(string? 成功消息)
    {
        var 成功 = await 界面命令
            .异步执行(_日志, _对话框, "保存", () => _定义.异步保存(), "报警配置")
            .ConfigureAwait(true);

        if (成功 && 成功消息 is not null)
        {
            _对话框.提示("已保存", 成功消息);
        }
    }

    private void 重建定义()
    {
        // 整表替换只发一次 Reset 通知（逐条 Add 会触发 N 次布局）
        定义集合.替换全部(_定义.定义集合);

        OnPropertyChanged(nameof(定义摘要));
    }

    private void 刷新权限() => 可编辑报警 = _用户项.有(权限.编辑绑定地址);

    // ── 命令 ───────────────────────────────────────────────────────────

    [RelayCommand]
    private void 显示活动() => 显示历史 = false;

    [RelayCommand]
    private void 显示历史列表() => 显示历史 = true;

    [RelayCommand]
    private void 确认(报警条目视图模型? 条目)
    {
        if (条目 is null)
        {
            return;
        }

        _报警.确认(条目.代码);
        条目.是否已确认 = true;
    }

    [RelayCommand]
    private void 确认全部()
    {
        // 批量操作也确认：误触会把未处理的报警一并标成已处理
        if (!_对话框.确认("全部确认",
                $"确认把 {活动报警.Count} 条活动报警全部标记为已确认？",
                视图.确认等级.中性, "全部确认"))
        {
            return;
        }

        _报警.确认全部();

        foreach (var 条目 in 活动报警)
        {
            条目.是否已确认 = true;
        }

        _对话框.提示("确认完成", "已确认全部活动报警。");
    }

    [RelayCommand]
    private async Task 异步重置()
    {
        if (!_对话框.确认("报警复位",
                "确认向 PLC 下发报警复位命令？",
                视图.确认等级.警告, "下发复位",
                "复位会清除 PLC 内的报警锁存位，设备可能在报警原因未排除的情况下恢复动作。"))
        {
            return;
        }

        await 界面命令
            .异步执行(_日志, _对话框, "复位", () => _报警.异步重置(), "报警复位")
            .ConfigureAwait(true);
    }

    [RelayCommand]
    private void 清空历史()
    {
        if (!_对话框.确认("清除历史",
                "确认清除全部历史报警记录？",
                视图.确认等级.危险, "清除",
                "历史记录将永久删除，不可撤销；追溯故障时将无据可查。"))
        {
            return;
        }

        _报警.清空历史();

        // 页签隐藏时 刷新() 不会重建历史表（按需装载），这里直接清空界面
        历史.Clear();
        _历史已装载 = true;

        刷新();
    }

    private void 刷新()
    {
        var 活动 = _报警.活动报警;

        var 活动行 = new List<报警条目视图模型>(活动.Count);
        foreach (var 条目 in 活动)
        {
            活动行.Add(new 报警条目视图模型(条目));
        }

        活动报警.替换全部(活动行);
        活动数量 = 活动.Count;

        // 历史表：仅在用户正在看历史页签（或首次装载）时才重建。
        // 曾经每次触发/清除事件都为全部历史行重新分配行 VM——历史上千条时
        // = O(N) 分配/次，报警风暴时放大成可感知的卡顿。
        if (!显示历史 && _历史已装载)
        {
            return;
        }

        装载历史();
    }

    /// <summary>历史表是否已按当前数据装载过（页签隐藏时跳过事件驱动的重建）。</summary>
    private bool _历史已装载;

    /// <summary>重建历史表行。</summary>
    private void 装载历史()
    {
        var 历史列表 = _报警.历史;
        var 历史行 = new List<报警条目视图模型>(历史列表.Count);
        foreach (var 条目 in 历史列表)
        {
            历史行.Add(new 报警条目视图模型(条目));
        }

        历史.替换全部(历史行);
        _历史已装载 = true;
    }

    /// <summary>切到历史页签时补一次装载（隐藏期间的变更在这里一次性呈现）。</summary>
    partial void On显示历史Changed(bool value)
    {
        if (value)
        {
            装载历史();
        }
    }
}
