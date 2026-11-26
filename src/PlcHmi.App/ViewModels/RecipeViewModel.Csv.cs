using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PlcHmi.App.Services;
using PlcHmi.Core.Abstractions;

namespace PlcHmi.App.ViewModels;

/// <summary>
/// <see cref="RecipeViewModel"/> 的「CSV 导入导出」部分（partial）。
///
/// <para>CSV 是交换格式：给 Excel 编辑、机种之间拷贝用；主存仍是 recipes.json，
/// 导入后要生效还需「切换机种」或「下发机种」。拆出来是为了守住单文件 400 行门禁。</para>
/// </summary>
public sealed partial class RecipeViewModel
{
    // ── CSV 导入导出 ───────────────────────────────────────────────────

    /// <summary>把当前机种导出为 CSV（可在 Excel 里编辑）。</summary>
    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        var fileName = string.IsNullOrWhiteSpace(_recipe.CurrentRecipeName)
            ? $"recipe-{_recipe.CurrentRecipeNumber:00}.csv"
            : $"recipe-{_recipe.CurrentRecipeNumber:00}-{_recipe.CurrentRecipeName}.csv";

        var filePath = _fileDialog.SelectSaveFile("导出机种配方", FileDialogService.csvFilter, fileName);

        if (filePath is null)
        {
            return;
        }

        try
        {
            IsBusy = true;
            await _recipe.ExportCsvToFileAsync(_recipe.CurrentRecipeNumber, filePath).ConfigureAwait(false);

            _log.LogInformation("已导出机种 {No} 到 {Path}", _recipe.CurrentRecipeNumber, filePath);
            _dialog.Hint("导出成功",
                $"机种 {_recipe.CurrentRecipeNumber:00} 已导出：\n\n{filePath}\n\n" +
                "可直接用 Excel 打开编辑，改完再通过「导入 CSV」写回。");
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "导出 CSV 失败");
            _dialog.ErrorInfo("导出失败", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>把全部机种批量导出到一个目录。</summary>
    [RelayCommand]
    private async Task ExportAllCsvAsync()
    {
        var dir = _fileDialog.SelectFileFixture("选择导出目录");

        if (dir is null)
        {
            return;
        }

        try
        {
            IsBusy = true;
            var fileList = await _recipe.ExportAllCsvToDirectoryAsync(dir).ConfigureAwait(false);

            _dialog.Hint("批量导出完成",
                $"已导出 {fileList.Count} 个机种的 CSV 到：\n\n{dir}\n\n" +
                "没有数据的机种也会导出为空白模板，方便先填后导。");
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "批量导出 CSV 失败");
            _dialog.ErrorInfo("导出失败", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>从 CSV 导入机种配方（只更新本机数据，不写 PLC）。</summary>
    [RelayCommand]
    private async Task ImportCsvAsync()
    {
        var filePath = _fileDialog.SelectOpenFile("导入机种配方", FileDialogService.csvFilter);

        if (filePath is null)
        {
            return;
        }

        try
        {
            IsBusy = true;

            // 目标机种号以文件里写的为准 —— 这样导出的文件可以直接导回原机种，
            // 也可以先切到别的机种再导入，实现「拷贝」
            var resultValue = await _recipe.FromFileImportCsvAsync(filePath).ConfigureAwait(false);

            if (!resultValue.Success || resultValue.Recipe is null)
            {
                _dialog.ErrorInfo("导入失败", string.Join(Environment.NewLine, resultValue.ErrorCollection));
                return;
            }

            AtUiThreadExecute(RefreshAll);

            var Message = resultValue.Summary;

            if (resultValue.Warning.Count > 0)
            {
                Message += "\n\n提示：\n" + string.Join(
                    Environment.NewLine, resultValue.Warning.Take(6));
            }

            Message += "\n\n注意：导入只更新本机数据与界面，需按「下发到 PLC」才写入设备。";
            Message += $"\n若要让当前机种生效，请在列表中切换到机种 {resultValue.Recipe.Number:00}。";

            _dialog.Hint("导入完成", Message);
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "导入 CSV 失败");
            _dialog.ErrorInfo("导入失败", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>把本地机种数据下发给当前机种（用于一致性差异后的手动同步）。</summary>
    [RelayCommand]
    private async Task PushToPlcAsync()
    {
        if (!_dialog.Confirm("下发参数",
                $"确认把本机数据（机种 {_recipe.CurrentRecipeNumber:00}）下发到 PLC？",
                Views.ConfirmLevel.Warning, "下发",
                "这会覆盖 PLC 参数区中当前的值。"))
        {
            return;
        }

        try
        {
            IsBusy = true;
            await _recipe.WriteParameterAsync().ConfigureAwait(false);

            AtUiThreadExecute(RefreshAll);
            _dialog.Hint("下发完成", "本机数据已写入 PLC。");
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "下发参数失败");
            _dialog.ErrorInfo("下发失败", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 从 PLC 读取当前参数（与「下发」相反方向）。
    ///
    /// <para>用途：现场直接在 PLC 侧改了参数（如调试时用 PLC 编程软件改定位速度），
    /// 用此功能把 PLC 的值同步回上位机并留底，避免本地与 PLC 各持一份互相覆盖。</para>
    /// </summary>
    [RelayCommand]
    private async Task FromPlcPullGetAsync()
    {
        if (!_dialog.Confirm("从 PLC 读取参数",
                $"确认读取 PLC 参数区当前值（机种 {_recipe.CurrentRecipeNumber:00}）？\n\n"
                + "这会以 PLC 的值覆盖界面与本地当前机种数据（不写 PLC）。\n"
                + "若 PLC 值超出允许范围将被整体拒绝，本地保持不变。"))
        {
            return;
        }

        try
        {
            IsBusy = true;

            var Change = await _recipe.ReadPlcParameterAsync().ConfigureAwait(false);

            AtUiThreadExecute(RefreshAll);

            _dialog.Hint(
                "读取完成",
                Change.Count == 0
                    ? "PLC 参数与本地一致，无变化。"
                    : $"已按 PLC 值更新 {Change.Count} 项：\n\n" + string.Join("\n", Change));
        }
        catch (InvalidOperationException exception)
        {
            // 服务层范围校验拒绝：整体未生效，本地保持原值
            _dialog.ErrorInfo("读取被拒绝", exception.Message);
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "从 PLC 读取参数失败");
            _dialog.ErrorInfo("读取失败", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

}
