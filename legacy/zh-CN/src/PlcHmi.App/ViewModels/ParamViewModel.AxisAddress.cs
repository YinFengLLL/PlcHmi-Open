using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using 上位机.核心.模型;

namespace 上位机.应用.视图模型;

/// <summary>
/// 参数设置页 ViewModel 的「轴地址」部分（partial）。
///
/// <para><b>为什么单独一个文件：</b>地址的合成、校验、保存与"有没有未保存改动"是同一件事，
/// 放一处便于一次看全；主文件本身也接近规模门禁。</para>
///
/// <para><b>唯一提交出口：</b>地址栏是"编辑中即改内存"的草稿，服务层才是真相。
/// 按「保存」或「保存轴地址」提交，按「读取」会恢复成配置文件里的值
/// （有未保存改动时会先确认，不静默回退）。</para>
/// </summary>
public sealed partial class 参数视图模型
{
    /// <summary>为指定轴的定位速度选择变量：选中后地址与变量名一并回填行模型。</summary>
    [RelayCommand]
    private void 选择定位速度变量(object? 行参数)
    {
        if (行参数 is not 轴速度行视图模型 行项)
        {
            return;
        }

        if (_对话框.选择变量($"为 {行项.名称} 定位速度选择变量") is not { } 条目)
        {
            return;
        }

        行项.变量文本 = 条目.变量名;
        行项.地址文本 = 条目.软元件地址;
        状态文本 = $"「{行项.名称}」定位速度已绑定变量 {条目.变量名}，保存轴地址后生效";
    }

    /// <summary>
    /// 保存轴地址。把「轴速度」表的地址列与「轴地址」表的五个字段合成一份候选表，
    /// 交给服务层统一校验——页面自己写配置会绕过区间与重叠检查。
    /// <para>保存成功后服务会重建采集计划，新地址立即生效，无需重启程序。</para>
    /// </summary>
    [RelayCommand]
    private async Task 异步保存轴地址()
    {
        if (!可编辑表格)
        {
            _对话框.提示("权限不足", "只有管理员可以修改轴地址。");
            return;
        }

        var 候选 = 构建轴地址候选();
        var 校验项 = _轴地址.校验(候选);

        if (!校验项.是否有效)
        {
            _日志.LogWarning("轴地址校验未通过，保存被拒绝：{Errors}", 校验项.摘要);
            _对话框.错误信息("轴地址校验未通过", string.Join(Environment.NewLine, 校验项.错误集合));
            return;
        }

        if (!_对话框.确认("保存轴地址",
                "确认保存这些地址？\n\n"
                + "保存后会立即重建采集计划。请先确认 PLC 侧的读取地址已同步改好，"
                + "否则采集到的会是错位的数据。"))
        {
            return;
        }

        try
        {
            是否忙碌 = true;

            await _轴地址.异步保存(候选).ConfigureAwait(false);

            在界面线程执行(() =>
            {
                状态文本 = "轴地址已保存并生效（采集计划已重建，无需重启）";
                刷新全部();
            });
        }
        catch (Exception 异常)
        {
            _日志.LogError(异常, "保存轴地址失败");
            在界面线程执行(() => _对话框.错误信息("保存失败", 异常.Message));
        }
        finally
        {
            是否忙碌 = false;
        }
    }

    /// <summary>把「轴速度」表的地址列与「轴地址」表的五个字段合成一份候选表。</summary>
    private 轴地址表格 构建轴地址候选()
    {
        var 候选 = _轴地址.表格.克隆();

        foreach (var 行项 in 轴地址集合)
        {
            if (候选.查找(行项.轴) is { } 条目项)
            {
                行项.应用(条目项);
            }
        }

        // 定位速度地址的唯一编辑入口在上方「轴速度」表
        foreach (var 行项 in 轴速度)
        {
            if (候选.查找(行项.轴键) is { } 条目项)
            {
                条目项.定位速度地址 = 行项.地址文本.Trim();
                条目项.定位速度变量 = 行项.变量文本.Trim();
            }
        }

        return 候选;
    }

    /// <summary>
    /// 界面上的地址是否有未保存的改动（与服务层的当前快照逐项比对）。
    ///
    /// <para>比较前先规范化：<c>1252</c> 与 <c>D1252</c> 是同一个地址，不该因为写法差异
    /// 被判成"有改动"，否则每次点「读取」都会弹一次无谓的确认。</para>
    /// </summary>
    private bool 地址有改动()
    {
        var 快照 = _轴地址.表格;
        var 候选 = 构建轴地址候选();

        return 候选.轴列表.Any(项 =>
        {
            var 原 = 快照.查找(项.轴);
            return 原 is null
                   || 地址不同(项.定位速度地址, 原.定位速度地址)
                   || 地址不同(项.位置地址, 原.位置地址)
                   || 地址不同(项.点动正转地址, 原.点动正转地址)
                   || 地址不同(项.点动反转地址, 原.点动反转地址)
                   || 地址不同(项.点动速度等级地址, 原.点动速度等级地址)
                   || 地址不同(项.回零地址, 原.回零地址);
        });
    }

    /// <summary>两个地址是否不同（忽略大小写、首尾空白与省略的 D 前缀）。</summary>
    private static bool 地址不同(string 当前值, string 原值) =>
        !string.Equals(规范化(当前值), 规范化(原值), StringComparison.OrdinalIgnoreCase);

    /// <summary>把 <c>1252</c> 这类省前缀写法补成 <c>D1252</c>，便于比较（不改动真实地址）。</summary>
    private static string 规范化(string 地址值)
    {
        var 文本 = 地址值.Trim();
        return 文本.Length > 0 && 文本.All(char.IsAsciiDigit) ? "D" + 文本 : 文本;
    }
}
