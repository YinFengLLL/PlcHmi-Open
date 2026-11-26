using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using 上位机.应用.服务;
using 上位机.核心.抽象;
using 上位机.核心.模型;

namespace 上位机.应用.视图模型;

/// <summary>
/// 机种列表里的一行。
/// <para>包一层是为了承载「是否当前机种」这个纯界面状态——<see cref="配方信息"/> 是 Core 模型，
/// 不该知道界面选中状态。</para>
/// </summary>
public sealed partial class 配方行视图模型 : ObservableObject
{
    [ObservableProperty] private bool _是否当前;

    public 配方行视图模型(配方信息 信息项, bool 是否当前)
    {
        信息 = 信息项;
        _是否当前 = 是否当前;
    }

    /// <summary>底层机种信息。</summary>
    public 配方信息 信息 { get; }

    public int 号 => 信息.号;
    public string 显示名称 => 信息.显示名称;
    /// <summary>机种数据量（区段快照中的寄存器项数）。</summary>
    public string 数据摘要 => 信息.数据摘要;

    public bool 有数据 => 信息.有数据;
}

/// <summary>
/// 机种管理页 ViewModel。
///
/// <para><b>存储分工：</b>一个机种 = <b>D2500–D3999 区段的一份寄存器快照</b>，
/// 存在上位机 <c>recipes.json</c>（主存）；PLC 里只有当前机种生效的一份。
/// 区段之外的 D 区数据（轴速度参数等）是<b>全局记忆</b>，不随机种变动。</para>
///
/// <para><b>切换机种的后果：</b>写机种号 + 自动把该机种的区段快照下发到 PLC（切完即生效）。
/// 这是需求明确要求的"切换后自动触发下发"。</para>
///
/// <para><b>保存机种：</b>把 PLC 当前 D2500–D3999 的实际值读回来存成该机种——
/// 现场在示教页调好点位后，回本页点「保存机种」。</para>
/// </summary>
public sealed partial class 配方视图模型 : 视图模型基类
{
    private readonly 配方服务接口 _配方;
    private readonly 对话框服务接口 _对话框;
    private readonly 文件对话框服务接口 _文件对话框;
    private readonly ILogger<配方视图模型> _日志;

    [ObservableProperty] private int _当前号 = 1;
    [ObservableProperty] private string _当前名称 = string.Empty;
    [ObservableProperty] private string _一致性文本 = string.Empty;
    [ObservableProperty] private bool _有一致性问题;

    public 配方视图模型(
        配方服务接口 配方项,
        对话框服务接口 对话框集合,
        文件对话框服务接口 文件对话框,
        ILogger<配方视图模型> 日志)
    {
        _配方 = 配方项;
        _对话框 = 对话框集合;
        _文件对话框 = 文件对话框;
        _日志 = 日志;

        标题 = "机种管理";
        副标题 = $"一个机种 = D{机种区段.起始}–D{机种区段.结束} 的一份数据快照 · 切换后自动下发";


        _配方.配方已变更 += (_, _) => 在界面线程执行(刷新全部);

        刷新全部();
    }

    /// <summary>机种列表。</summary>
    public 批量可观察集合<配方行视图模型> 配方集合 { get; } = new();

    /// <inheritdoc />
    public override Task 异步初始化()
    {
        刷新全部();
        return Task.CompletedTask;
    }

    // ── 命令 ───────────────────────────────────────────────────────────

    /// <summary>切换到指定机种（含自动下发）。</summary>
    [RelayCommand]
    private async Task 异步切换配方(配方行视图模型? 行项)
    {
        if (行项 is null)
        {
            return;
        }

        if (行项.号 == _配方.当前配方号)
        {
            _对话框.提示("无需切换", $"当前已经是机种 {行项.号:00}。");
            return;
        }

        // 空机种（从未保存过数据）也可以切换——新机种本来就是空白，
        // 切换后区段整段清零；现场要先能切过去才谈得上示教
        var 空提示 = 行项.有数据
            ? string.Empty
            : "\n\n该机种还没有保存过数据：切换后 PLC 的机种区段会被整段清零，可随即在示教页开始示教。";

        if (!_对话框.确认("切换机种",
                $"确认切换到机种 {行项.号:00}「{行项.显示名称}」？{空提示}",
                视图.确认等级.警告, "切换",
                $"切换后会把该机种的 D{机种区段.起始}–D{机种区段.结束} 数据写入 PLC，"
                + "现场点位立即改变。轴速度等全局参数不受影响。"))
        {
            return;
        }

        try
        {
            是否忙碌 = true;
            await _配方.异步切换(行项.号).ConfigureAwait(false);

            在界面线程执行(刷新全部);

            _日志.LogInformation("已切换到机种 {No}", 行项.号);
            _对话框.提示("切换完成", $"已切换到机种 {行项.号:00}「{行项.显示名称}」，数据已下发。");
        }
        catch (Exception 异常)
        {
            _日志.LogError(异常, "切换机种失败");
            _对话框.错误信息("切换失败", 异常.Message);
        }
        finally
        {
            是否忙碌 = false;
        }
    }

    /// <summary>保存当前机种名。</summary>
    [RelayCommand]
    private async Task 异步保存名称()
    {
        try
        {
            是否忙碌 = true;
            await _配方.异步重命名(_配方.当前配方号, 当前名称).ConfigureAwait(false);

            在界面线程执行(刷新全部);
            _对话框.提示("保存成功", $"机种 {_配方.当前配方号:00} 已命名为「{当前名称}」。");
        }
        catch (Exception 异常)
        {
            _日志.LogError(异常, "保存机种名失败");
            _对话框.错误信息("保存失败", 异常.Message);
        }
        finally
        {
            是否忙碌 = false;
        }
    }

    /// <summary>
    /// 保存机种：把 PLC 当前 D2500–D3999 的实际值读回来，存成当前机种。
    /// <para>现场流程：示教页调好点位 → 回本页点「保存机种」→ 数据持久化到 recipes.json。</para>
    /// </summary>
    [RelayCommand]
    private async Task 异步保存机种()
    {
        if (!_对话框.确认("保存机种",
                $"把 PLC 当前的 D{机种区段.起始}–D{机种区段.结束} 数据"
                + $"保存为机种 {_配方.当前配方号:00}「{_配方.当前配方名称}」？",
                视图.确认等级.中性, "保存",
                "这会覆盖该机种已保存的数据（不影响 PLC 里的当前值）。"))
        {
            return;
        }

        是否忙碌 = true;

        try
        {
            var 成功 = await 界面命令
                .异步执行(_日志, _对话框, "保存机种", () => _配方.异步保存机种(_配方.当前配方号))
                .ConfigureAwait(true);

            if (成功)
            {
                在界面线程执行(刷新全部);
                _对话框.提示("保存完成", $"机种 {_配方.当前配方号:00} 已保存当前 PLC 数据。");
            }
        }
        finally
        {
            是否忙碌 = false;
        }
    }

    /// <summary>下发机种：把当前机种的快照写到 PLC 的 D2500–D3999（不切换机种号）。</summary>
    [RelayCommand]
    private async Task 异步下发机种()
    {
        if (!_对话框.确认("下发机种",
                $"把机种 {_配方.当前配方号:00}「{_配方.当前配方名称}」的数据下发到 PLC？",
                视图.确认等级.警告, "下发",
                $"PLC 的 D{机种区段.起始}–D{机种区段.结束} 会被整段覆盖，"
                + "确认现场无人员处于设备动作范围。"))
        {
            return;
        }

        是否忙碌 = true;

        try
        {
            if (await 界面命令
                    .异步执行(_日志, _对话框, "下发机种", () => _配方.异步下发机种(_配方.当前配方号))
                    .ConfigureAwait(true))
            {
                _对话框.提示("下发完成", $"机种 {_配方.当前配方号:00} 的数据已写入 PLC。");
            }
        }
        finally
        {
            是否忙碌 = false;
        }
    }

    /// <summary>重新从本地文件加载机种清单。</summary>
    [RelayCommand]
    private void 刷新() => 刷新全部();

    // ── 内部 ───────────────────────────────────────────────────────────

    private void 刷新全部()
    {
        当前号 = _配方.当前配方号;
        当前名称 = _配方.当前配方名称;

        配方集合.替换全部(_配方.配方集合.Select(信息项 =>
            new 配方行视图模型(信息项, 信息项.号 == _配方.当前配方号)));

        var 上报 = _配方.最后一次一致性报告;

        if (上报 is null)
        {
            一致性文本 = "尚未做启动一致性校验。";
            有一致性问题 = false;
            return;
        }

        一致性文本 = 上报.摘要;
        有一致性问题 = !上报.是否一致;
    }
}
