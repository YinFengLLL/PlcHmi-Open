using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using 上位机.核心.模型;

namespace 上位机.应用.视图模型;

/// <summary>
/// 参数设置页 ViewModel 的「延时表 / 按钮表管理」部分（partial）。
///
/// <para><b>为什么单独一个文件：</b>增删改条目与按钮置位/复位是同一类操作
/// （都对参数表做写操作、都需要权限与忙碌态），放一处便于整体阅读；
/// 主文件已接近项目规模门禁。</para>
/// </summary>
public sealed partial class 参数视图模型
{
    // ── 延时表管理（仅管理员） ─────────────────────────────────────────

    [RelayCommand]
    private Task 异步添加延迟() => 异步添加条目(参数表格类别.延迟, "延时");

    [RelayCommand]
    private Task 异步编辑延迟(参数条目行视图模型? 行项) =>
        异步编辑条目(参数表格类别.延迟, 行项, "延时");

    [RelayCommand]
    private Task 异步移除延迟(参数条目行视图模型? 行项) =>
        异步移除条目(参数表格类别.延迟, 行项, "延时");

    // ── 按钮表管理（仅管理员） ─────────────────────────────────────────

    [RelayCommand]
    private Task 异步添加按钮() => 异步添加条目(参数表格类别.按钮, "按钮");

    [RelayCommand]
    private Task 异步编辑按钮(参数条目行视图模型? 行项) =>
        异步编辑条目(参数表格类别.按钮, 行项, "按钮");

    [RelayCommand]
    private Task 异步移除按钮(参数条目行视图模型? 行项) =>
        异步移除条目(参数表格类别.按钮, 行项, "按钮");

    // ── 按钮置位 / 复位（工程师及以上） ─────────────────────────────────

    /// <summary>把按钮点位置位（开启）。即时写 M 区。</summary>
    [RelayCommand]
    private Task 异步置按钮为开(参数条目行视图模型? 行项) => 异步内部设置按钮(行项, true);

    /// <summary>把按钮点位复位（关闭）。即时写 M 区。</summary>
    [RelayCommand]
    private Task 异步置按钮为关(参数条目行视图模型? 行项) => 异步内部设置按钮(行项, false);

    private async Task 异步内部设置按钮(参数条目行视图模型? 行项, bool on)
    {
        if (行项 is null)
        {
            return;
        }

        if (!可操作值列表)
        {
            _对话框.提示("权限不足", "只有工程师及以上可以操作按钮的置位与复位。");
            return;
        }

        if (!行项.定义.启用)
        {
            _对话框.提示("条目已停用", $"「{行项.名称}」已停用，请先在编辑里启用后再操作。");
            return;
        }

        try
        {
            是否忙碌 = true;
            await _表格.异步设置按钮(行项.定义, on).ConfigureAwait(false);
            行项.标记按钮状态(on);

            状态文本 = $"「{行项.名称}」已{(on ? "置位" : "复位")}（{行项.地址}）";
            _日志.LogInformation("按钮「{Name}」{Action}", 行项.名称, on ? "置位" : "复位");
        }
        catch (Exception 异常)
        {
            _日志.LogError(异常, "按钮「{Name}」{Action}失败", 行项.名称, on ? "置位" : "复位");
            _对话框.错误信息("操作失败", 异常.Message);
        }
        finally
        {
            是否忙碌 = false;
        }
    }
}
