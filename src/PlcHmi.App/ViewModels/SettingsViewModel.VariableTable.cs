using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using 上位机.应用.服务;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.应用.视图模型;

/// <summary>
/// 系统设置页 · 汇川变量表区（导入 / 浏览，仅管理员）。
///
/// <para>从 <c>SettingsViewModel.cs</c> 拆出：变量表导入是独立功能（读 CSV → 落盘
/// variables.json → 替换内存表），与连接参数、运行模式没有耦合，混在一个文件里只会
/// 让这个本就接近 400 行的界面类继续膨胀（见 <c>tools/check_thresholds.py</c>）。</para>
/// </summary>
public sealed partial class 设置视图模型
{
    [ObservableProperty] private string _变量表条目数文本 = "0 条";

    /// <summary>导入汇川变量表 CSV：解析校验后原子落盘 variables.json 并替换内存表。</summary>
    [RelayCommand]
    private async Task 异步导入变量表()
    {
        if (!可导入变量表)
        {
            _对话框.提示("权限不足", "只有管理员可以导入变量表。");
            return;
        }

        if (_变量表 is null)
        {
            _对话框.提示("不可用", "变量表服务未就绪。");
            return;
        }

        var 文件路径 = _文件对话框.选择打开文件("导入汇川变量表", 文件对话框服务.CSV筛选);

        if (string.IsNullOrEmpty(文件路径))
        {
            return;
        }

        if (!_对话框.确认("导入变量表",
                $"确认从以下文件导入变量表？\n\n{文件路径}\n\n导入会整体替换当前变量表（variables.json）。"))
        {
            return;
        }

        try
        {
            是否忙碌 = true;

            var 结果 = await _变量表.异步导入(文件路径);

            刷新变量表显示();
            _对话框.提示("导入完成", $"{结果.摘要}\n\n绑定该变量的配置会在下次启动时重新回填地址；"
                                     + "OPC UA 模式的符号寻址立即按新表生效。");
        }
        catch (InvalidOperationException 异常)
        {
            _对话框.错误信息("导入失败", 异常.Message);
        }
        catch (System.IO.IOException 异常)
        {
            _日志.LogError(异常, "导入变量表失败：{File}", 文件路径);
            _对话框.错误信息("导入失败", $"读取文件失败：{异常.Message}");
        }
        finally
        {
            是否忙碌 = false;
        }
    }

    /// <summary>浏览当前变量表（搜索 / 只读查看）。</summary>
    [RelayCommand]
    private void 查看变量表()
    {
        if (_变量表 is null)
        {
            _对话框.提示("不可用", "变量表服务未就绪。");
            return;
        }

        // 浏览复用选择对话框：选不选都只影响对话框返回值，不写任何配置
        视图.变量选择对话框.显示(_变量表.条目集合, "变量表浏览");
    }

    private void 刷新变量表显示() =>
        变量表条目数文本 = $"{(_变量表?.条目集合.Count ?? 0)} 条";
}
