using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using 上位机.应用.服务;
using 上位机.核心.模型;

namespace 上位机.应用.视图模型;

/// <summary>
/// <see cref="标定视图模型"/> 的「其它点坐标」部分（partial）：增删改查与 CSV 导入导出。
///
/// <para>从主文件拆出：主文件逼近 400 行规模门禁；这里与"标定示教"是两块独立的
/// 业务，分开更易审查。</para>
/// </summary>
public sealed partial class 标定视图模型
{
    /// <summary>
    /// 当前选中的其它点点码（<b>0 基</b>，写 PLC 的 <c>D653</c>）；未选中为 <c>-1</c>。
    /// <para>PLC 据此知道当前操作的是哪个其它点——这与两个示教页的点码（D651 / D652）同款约定。</para>
    /// </summary>
    [ObservableProperty] private int _选中其它点序号 = -1;

    /// <summary>选中摘要（操作员一眼看到 PLC 侧拿到的是哪个点号）。</summary>
    public string 选中摘要 => 选中其它点序号 < 0
        ? "未选中其它点：点一下左侧行即可选中，PLC 的 D653 会收到该点号（0 基）"
        : $"已选中第 {选中其它点序号 + 1} 点 → PLC D653 = {选中其它点序号}";

    partial void On选中其它点序号Changed(int value) => OnPropertyChanged(nameof(选中摘要));

    /// <summary>按关键字查询其它点。</summary>
    [RelayCommand]
    private void 刷新辅助点() => 重载辅助点(_辅助点.查询(查询关键字));

    /// <summary>读取全部其它点坐标。</summary>
    [RelayCommand]
    private async Task 异步读取全部其它点() => await 异步读取全部辅助点().ConfigureAwait(false);

    private async Task 异步读取全部辅助点()
    {
        foreach (var 行项 in 辅助点集合)
        {
            try
            {
                await 行项.异步读取().ConfigureAwait(false);
            }
            catch (Exception 异常)
            {
                // 单点读取失败不影响其它点；只留痕不打断（PLC 未连接时全部返回 0）
                _日志.LogWarning(异常, "读取其它点 {Name} 失败", 行项.名称);
            }
        }
    }

    /// <summary>写入某个其它点的坐标。</summary>
    [RelayCommand]
    private async Task 异步写入其它点(辅助点行视图模型? 行项)
    {
        if (行项 is null)
        {
            return;
        }

        是否忙碌 = true;

        try
        {
            if (await 界面命令
                    .异步执行(_日志, _对话框, "写入其它点", () => 行项.异步写入())
                    .ConfigureAwait(true))
            {
                _对话框.提示("写入完成", $"「{行项.名称}」坐标已写入 PLC。");
            }
        }
        finally
        {
            是否忙碌 = false;
        }
    }

    /// <summary>新增其它点。</summary>
    [RelayCommand]
    private async Task 异步新增其它点()
    {
        var 候选 = new 辅助点定义
        {
            名称 = "新点位",
            X基址 = _辅助点.配置.允许范围.最小,
            Y基址 = _辅助点.配置.允许范围.最小 + 2,
            Z基址 = _辅助点.配置.允许范围.最小 + 4,
        };

        if (!_对话框.编辑辅助点(候选, 是否新建: true))
        {
            return;
        }

        是否忙碌 = true;

        try
        {
            await 界面命令
                .异步执行(_日志, _对话框, "新增其它点", () => _辅助点.异步新增(候选))
                .ConfigureAwait(true);
        }
        finally
        {
            是否忙碌 = false;
        }
    }

    /// <summary>编辑其它点。</summary>
    [RelayCommand]
    private async Task 异步编辑其它点(辅助点行视图模型? 行项)
    {
        if (行项 is null)
        {
            return;
        }

        var 原名 = 行项.定义.名称;
        var 候选 = 行项.定义.克隆();

        if (!_对话框.编辑辅助点(候选, 是否新建: false))
        {
            return;
        }

        是否忙碌 = true;

        try
        {
            await 界面命令
                .异步执行(_日志, _对话框, "编辑其它点", () => _辅助点.异步编辑(原名, 候选))
                .ConfigureAwait(true);
        }
        finally
        {
            是否忙碌 = false;
        }
    }

    /// <summary>删除其它点。</summary>
    [RelayCommand]
    private async Task 异步删除其它点(辅助点行视图模型? 行项)
    {
        if (行项 is null)
        {
            return;
        }

        if (!_对话框.确认("删除其它点", $"确认删除「{行项.名称}」？", 视图.确认等级.警告, "删除"))
        {
            return;
        }

        是否忙碌 = true;

        try
        {
            await 界面命令
                .异步执行(_日志, _对话框, "删除其它点", () => _辅助点.异步删除(行项.名称))
                .ConfigureAwait(true);
        }
        finally
        {
            是否忙碌 = false;
        }
    }

    // ── 选中与点码（D653） ──────────────────────────────────────────────

    /// <summary>进入页面时把当前点码补写一次：选中态没变化不会触发选中回调，
    /// 但 PLC 侧需要知道"操作员现在回到标定页了"。</summary>
    private Task 异步同步其它点点码() =>
        选中其它点序号 < 0
            ? Task.CompletedTask
            : _页面上下文.异步写入点号(页面上下文寄存器.标定其它点点码, 选中其它点序号);

    /// <summary>在其它点<b>全量列表</b>里取点码（0 基）；找不到返回 -1。</summary>
    private int 序号Of(string 名称值)
    {
        var 全量 = _辅助点.点位集合;

        for (var 序号 = 0; 序号 < 全量.Count; 序号++)
        {
            if (string.Equals(全量[序号].名称, 名称值, StringComparison.OrdinalIgnoreCase))
            {
                return 序号;
            }
        }

        return -1;
    }

    /// <summary>
    /// 选中一个其它点：行高亮 + 把点码（0 基）写进 PLC 的 D653。
    /// <para>写入是 best-effort：PLC 未连接或写入失败不影响选中与后续操作
    /// （页面上下文服务只记日志，见其注释）。</para>
    /// </summary>
    private Task 选中其它点(int 序号)
    {
        if (序号 < 0)
        {
            return Task.CompletedTask;
        }

        选中其它点序号 = 序号;
        刷新选中态();

        return _页面上下文.异步写入点号(页面上下文寄存器.标定其它点点码, 序号);
    }

    /// <summary>按当前点码刷新行高亮（重建列表 / 改名后同样成立）。</summary>
    private void 刷新选中态()
    {
        foreach (var 行项 in 辅助点集合)
        {
            行项.是否当前 = 行项.序号 == 选中其它点序号;
        }
    }

    /// <summary>导出其它点为 CSV。</summary>
    [RelayCommand]
    private async Task 异步导出CSV()
    {
        var 路径 = _文件对话框.选择保存文件(
            "导出其它点坐标", 文件对话框服务.CSV筛选, "其它点坐标.csv");

        if (路径 is null)
        {
            return;
        }

        是否忙碌 = true;

        try
        {
            if (await 界面命令
                    .异步执行(_日志, _对话框, "导出", () => _辅助点.异步导出CSV到文件(路径))
                    .ConfigureAwait(true))
            {
                _对话框.提示("导出完成", $"已导出到：\n{路径}");
            }
        }
        finally
        {
            是否忙碌 = false;
        }
    }

    /// <summary>从 CSV 导入其它点。</summary>
    [RelayCommand]
    private async Task 异步导入CSV()
    {
        var 路径 = _文件对话框.选择打开文件("导入其它点坐标", 文件对话框服务.CSV筛选);

        if (路径 is null)
        {
            return;
        }

        if (!_对话框.确认("导入其它点",
                "导入将整体替换当前的其它点定义列表，确认继续？",
                视图.确认等级.警告, "导入"))
        {
            return;
        }

        是否忙碌 = true;

        try
        {
            var 数量 = 0;

            if (await 界面命令
                    .异步执行(_日志, _对话框, "导入", async () => 数量 = await _辅助点.异步从文件导入CSV(路径))
                    .ConfigureAwait(true))
            {
                _对话框.提示("导入完成", $"已导入 {数量} 个其它点。");
            }
        }
        finally
        {
            是否忙碌 = false;
        }
    }
}
