using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PlcHmi.App.Services;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.App.ViewModels;

/// <summary>
/// 系统设置页 · 汇川变量表区（导入 / 浏览，仅管理员）。
///
/// <para>从 <c>SettingsViewModel.cs</c> 拆出：变量表导入是独立功能（读 CSV → 落盘
/// variables.json → 替换内存表），与连接参数、运行模式没有耦合，混在一个文件里只会
/// 让这个本就接近 400 行的界面类继续膨胀（见 <c>tools/check_thresholds.py</c>）。</para>
/// </summary>
public sealed partial class SettingsViewModel
{
    [ObservableProperty] private string _variableTableEntryNumberText = "0 条";

    /// <summary>导入汇川变量表 CSV：解析校验后原子落盘 variables.json 并替换内存表。</summary>
    [RelayCommand]
    private async Task ImportVariableTableAsync()
    {
        if (!ImportableVariableTable)
        {
            _dialog.Hint("权限不足", "只有管理员可以导入变量表。");
            return;
        }

        if (_variableTable is null)
        {
            _dialog.Hint("不可用", "变量表服务未就绪。");
            return;
        }

        var filePath = _fileDialog.SelectOpenFile("导入汇川变量表", FileDialogService.csvFilter);

        if (string.IsNullOrEmpty(filePath))
        {
            return;
        }

        if (!_dialog.Confirm("导入变量表",
                $"确认从以下文件导入变量表？\n\n{filePath}\n\n导入会整体替换当前变量表（variables.json）。"))
        {
            return;
        }

        try
        {
            IsBusy = true;

            var result = await _variableTable.ImportAsync(filePath);

            RefreshVariableTableShow();
            _dialog.Hint("导入完成", $"{result.Summary}\n\n绑定该变量的配置会在下次启动时重新回填地址；"
                                     + "OPC UA 模式的符号寻址立即按新表生效。");
        }
        catch (InvalidOperationException exception)
        {
            _dialog.ErrorInfo("导入失败", exception.Message);
        }
        catch (System.IO.IOException exception)
        {
            _log.LogError(exception, "导入变量表失败：{File}", filePath);
            _dialog.ErrorInfo("导入失败", $"读取文件失败：{exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>浏览当前变量表（搜索 / 只读查看）。</summary>
    [RelayCommand]
    private void CheckSeeVariableTable()
    {
        if (_variableTable is null)
        {
            _dialog.Hint("不可用", "变量表服务未就绪。");
            return;
        }

        // 浏览复用选择对话框：选不选都只影响对话框返回值，不写任何配置
        Views.VariableSelectDialog.Show(_variableTable.EntryCollection, "变量表浏览");
    }

    private void RefreshVariableTableShow() =>
        VariableTableEntryNumberText = $"{(_variableTable?.EntryCollection.Count ?? 0)} 条";
}
