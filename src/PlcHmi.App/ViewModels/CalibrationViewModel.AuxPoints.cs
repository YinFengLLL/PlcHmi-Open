using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PlcHmi.App.Services;
using PlcHmi.Core.Models;

namespace PlcHmi.App.ViewModels;

/// <summary>
/// <see cref="CalibrationViewModel"/> 的「其它点坐标」部分（partial）：增删改查与 CSV 导入导出。
///
/// <para>从主文件拆出：主文件逼近 400 行规模门禁；这里与"标定示教"是两块独立的
/// 业务，分开更易审查。</para>
/// </summary>
public sealed partial class CalibrationViewModel
{
    /// <summary>
    /// 当前选中的其它点点码（<b>0 基</b>，写 PLC 的 <c>D653</c>）；未选中为 <c>-1</c>。
    /// <para>PLC 据此知道当前操作的是哪个其它点——这与两个示教页的点码（D651 / D652）同款约定。</para>
    /// </summary>
    [ObservableProperty] private int _selectedOtherPointIndex = -1;

    /// <summary>选中摘要（操作员一眼看到 PLC 侧拿到的是哪个点号）。</summary>
    public string SelectedSummary => SelectedOtherPointIndex < 0
        ? "未选中其它点：点一下左侧行即可选中，PLC 的 D653 会收到该点号（0 基）"
        : $"已选中第 {SelectedOtherPointIndex + 1} 点 → PLC D653 = {SelectedOtherPointIndex}";

    partial void OnSelectedOtherPointIndexChanged(int value) => OnPropertyChanged(nameof(SelectedSummary));

    /// <summary>按关键字查询其它点。</summary>
    [RelayCommand]
    private void RefreshAuxPoint() => ReloadAuxPoint(_auxPoint.Query(QueryKeyword));

    /// <summary>读取全部其它点坐标。</summary>
    [RelayCommand]
    private async Task ReadAllOtherPointAsync() => await ReadAllAuxPointAsync().ConfigureAwait(false);

    private async Task ReadAllAuxPointAsync()
    {
        foreach (var rowItem in AuxPointCollection)
        {
            try
            {
                await rowItem.ReadAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // 单点读取失败不影响其它点；只留痕不打断（PLC 未连接时全部返回 0）
                _log.LogWarning(exception, "读取其它点 {Name} 失败", rowItem.Name);
            }
        }
    }

    /// <summary>写入某个其它点的坐标。</summary>
    [RelayCommand]
    private async Task WriteOtherPointAsync(AuxPointRowViewModel? rowItem)
    {
        if (rowItem is null)
        {
            return;
        }

        IsBusy = true;

        try
        {
            if (await UiCommand
                    .ExecuteAsync(_log, _dialog, "写入其它点", () => rowItem.WriteAsync())
                    .ConfigureAwait(true))
            {
                _dialog.Hint("写入完成", $"「{rowItem.Name}」坐标已写入 PLC。");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>新增其它点。</summary>
    [RelayCommand]
    private async Task AddOtherPointAsync()
    {
        var candidate = new AuxPointDefinition
        {
            Name = "新点位",
            XBaseAddress = _auxPoint.Config.AllowRange.Min,
            YBaseAddress = _auxPoint.Config.AllowRange.Min + 2,
            ZBaseAddress = _auxPoint.Config.AllowRange.Min + 4,
        };

        if (!_dialog.EditAuxPoint(candidate, isNewBuild: true))
        {
            return;
        }

        IsBusy = true;

        try
        {
            await UiCommand
                .ExecuteAsync(_log, _dialog, "新增其它点", () => _auxPoint.AddAsync(candidate))
                .ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>编辑其它点。</summary>
    [RelayCommand]
    private async Task EditOtherPointAsync(AuxPointRowViewModel? rowItem)
    {
        if (rowItem is null)
        {
            return;
        }

        var originalName = rowItem.Definition.Name;
        var candidate = rowItem.Definition.Clone();

        if (!_dialog.EditAuxPoint(candidate, isNewBuild: false))
        {
            return;
        }

        IsBusy = true;

        try
        {
            await UiCommand
                .ExecuteAsync(_log, _dialog, "编辑其它点", () => _auxPoint.EditAsync(originalName, candidate))
                .ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>删除其它点。</summary>
    [RelayCommand]
    private async Task DeleteOtherPointAsync(AuxPointRowViewModel? rowItem)
    {
        if (rowItem is null)
        {
            return;
        }

        if (!_dialog.Confirm("删除其它点", $"确认删除「{rowItem.Name}」？", Views.ConfirmLevel.Warning, "删除"))
        {
            return;
        }

        IsBusy = true;

        try
        {
            await UiCommand
                .ExecuteAsync(_log, _dialog, "删除其它点", () => _auxPoint.DeleteAsync(rowItem.Name))
                .ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── 选中与点码（D653） ──────────────────────────────────────────────

    /// <summary>进入页面时把当前点码补写一次：选中态没变化不会触发选中回调，
    /// 但 PLC 侧需要知道"操作员现在回到标定页了"。</summary>
    private Task SyncOtherPointPointCodeAsync() =>
        SelectedOtherPointIndex < 0
            ? Task.CompletedTask
            : _pageContext.WritePointNumberAsync(PageContextRegister.calibrationOtherPointPointCode, SelectedOtherPointIndex);

    /// <summary>在其它点<b>全量列表</b>里取点码（0 基）；找不到返回 -1。</summary>
    private int IndexOf(string nameValue)
    {
        var full = _auxPoint.PointCollection;

        for (var Sequence = 0; Sequence < full.Count; Sequence++)
        {
            if (string.Equals(full[Sequence].Name, nameValue, StringComparison.OrdinalIgnoreCase))
            {
                return Sequence;
            }
        }

        return -1;
    }

    /// <summary>
    /// 选中一个其它点：行高亮 + 把点码（0 基）写进 PLC 的 D653。
    /// <para>写入是 best-effort：PLC 未连接或写入失败不影响选中与后续操作
    /// （页面上下文服务只记日志，见其注释）。</para>
    /// </summary>
    private Task SelectedOtherPoint(int Sequence)
    {
        if (Sequence < 0)
        {
            return Task.CompletedTask;
        }

        SelectedOtherPointIndex = Sequence;
        RefreshSelectedState();

        return _pageContext.WritePointNumberAsync(PageContextRegister.calibrationOtherPointPointCode, Sequence);
    }

    /// <summary>按当前点码刷新行高亮（重建列表 / 改名后同样成立）。</summary>
    private void RefreshSelectedState()
    {
        foreach (var rowItem in AuxPointCollection)
        {
            rowItem.IsCurrent = rowItem.Sequence == SelectedOtherPointIndex;
        }
    }

    /// <summary>导出其它点为 CSV。</summary>
    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        var path = _fileDialog.SelectSaveFile(
            "导出其它点坐标", FileDialogService.csvFilter, "其它点坐标.csv");

        if (path is null)
        {
            return;
        }

        IsBusy = true;

        try
        {
            if (await UiCommand
                    .ExecuteAsync(_log, _dialog, "导出", () => _auxPoint.ExportCsvToFileAsync(path))
                    .ConfigureAwait(true))
            {
                _dialog.Hint("导出完成", $"已导出到：\n{path}");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>从 CSV 导入其它点。</summary>
    [RelayCommand]
    private async Task ImportCsvAsync()
    {
        var path = _fileDialog.SelectOpenFile("导入其它点坐标", FileDialogService.csvFilter);

        if (path is null)
        {
            return;
        }

        if (!_dialog.Confirm("导入其它点",
                "导入将整体替换当前的其它点定义列表，确认继续？",
                Views.ConfirmLevel.Warning, "导入"))
        {
            return;
        }

        IsBusy = true;

        try
        {
            var Count = 0;

            if (await UiCommand
                    .ExecuteAsync(_log, _dialog, "导入", async () => Count = await _auxPoint.FromFileImportCsvAsync(path))
                    .ConfigureAwait(true))
            {
                _dialog.Hint("导入完成", $"已导入 {Count} 个其它点。");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }
}
