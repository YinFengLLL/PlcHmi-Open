using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PlcHmi.App.Services;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Models;

namespace PlcHmi.App.ViewModels;

/// <summary>
/// 机种列表里的一行。
/// <para>包一层是为了承载「是否当前机种」这个纯界面状态——<see cref="RecipeInfo"/> 是 Core 模型，
/// 不该知道界面选中状态。</para>
/// </summary>
public sealed partial class RecipeRowViewModel : ObservableObject
{
    [ObservableProperty] private bool _isCurrent;

    public RecipeRowViewModel(RecipeInfo infoItem, bool IsCurrent)
    {
        Info = infoItem;
        _isCurrent = IsCurrent;
    }

    /// <summary>底层机种信息。</summary>
    public RecipeInfo Info { get; }

    public int Number => Info.Number;
    public string DisplayName => Info.DisplayName;
    /// <summary>机种数据量（区段快照中的寄存器项数）。</summary>
    public string DataSummary => Info.DataSummary;

    public bool HasData => Info.HasData;
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
public sealed partial class RecipeViewModel : ViewModelBase
{
    private readonly IRecipeService _recipe;
    private readonly IDialogService _dialog;
    private readonly IFileDialogService _fileDialog;
    private readonly ILogger<RecipeViewModel> _log;

    [ObservableProperty] private int _currentNumber = 1;
    [ObservableProperty] private string _currentName = string.Empty;
    [ObservableProperty] private string _consistencyText = string.Empty;
    [ObservableProperty] private bool _hasConsistencyIssue;

    public RecipeViewModel(
        IRecipeService recipeItem,
        IDialogService dialogCollection,
        IFileDialogService FileDialog,
        ILogger<RecipeViewModel> Log)
    {
        _recipe = recipeItem;
        _dialog = dialogCollection;
        _fileDialog = FileDialog;
        _log = Log;

        Title = "机种管理";
        Subtitle = $"一个机种 = D{MachineModelSection.StartAt}–D{MachineModelSection.end} 的一份数据快照 · 切换后自动下发";


        _recipe.recipeChanged += (_, _) => AtUiThreadExecute(RefreshAll);

        RefreshAll();
    }

    /// <summary>机种列表。</summary>
    public BatchObservableCollection<RecipeRowViewModel> RecipeCollection { get; } = new();

    /// <inheritdoc />
    public override Task InitializeAsync()
    {
        RefreshAll();
        return Task.CompletedTask;
    }

    // ── 命令 ───────────────────────────────────────────────────────────

    /// <summary>切换到指定机种（含自动下发）。</summary>
    [RelayCommand]
    private async Task SwitchRecipeAsync(RecipeRowViewModel? rowItem)
    {
        if (rowItem is null)
        {
            return;
        }

        if (rowItem.Number == _recipe.CurrentRecipeNumber)
        {
            _dialog.Hint("无需切换", $"当前已经是机种 {rowItem.Number:00}。");
            return;
        }

        // 空机种（从未保存过数据）也可以切换——新机种本来就是空白，
        // 切换后区段整段清零；现场要先能切过去才谈得上示教
        var emptyHint = rowItem.HasData
            ? string.Empty
            : "\n\n该机种还没有保存过数据：切换后 PLC 的机种区段会被整段清零，可随即在示教页开始示教。";

        if (!_dialog.Confirm("切换机种",
                $"确认切换到机种 {rowItem.Number:00}「{rowItem.DisplayName}」？{emptyHint}",
                Views.ConfirmLevel.Warning, "切换",
                $"切换后会把该机种的 D{MachineModelSection.StartAt}–D{MachineModelSection.end} 数据写入 PLC，"
                + "现场点位立即改变。轴速度等全局参数不受影响。"))
        {
            return;
        }

        try
        {
            IsBusy = true;
            await _recipe.SwitchAsync(rowItem.Number).ConfigureAwait(false);

            AtUiThreadExecute(RefreshAll);

            _log.LogInformation("已切换到机种 {No}", rowItem.Number);
            _dialog.Hint("切换完成", $"已切换到机种 {rowItem.Number:00}「{rowItem.DisplayName}」，数据已下发。");
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "切换机种失败");
            _dialog.ErrorInfo("切换失败", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>保存当前机种名。</summary>
    [RelayCommand]
    private async Task SaveNameAsync()
    {
        try
        {
            IsBusy = true;
            await _recipe.RenameAsync(_recipe.CurrentRecipeNumber, CurrentName).ConfigureAwait(false);

            AtUiThreadExecute(RefreshAll);
            _dialog.Hint("保存成功", $"机种 {_recipe.CurrentRecipeNumber:00} 已命名为「{CurrentName}」。");
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "保存机种名失败");
            _dialog.ErrorInfo("保存失败", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 保存机种：把 PLC 当前 D2500–D3999 的实际值读回来，存成当前机种。
    /// <para>现场流程：示教页调好点位 → 回本页点「保存机种」→ 数据持久化到 recipes.json。</para>
    /// </summary>
    [RelayCommand]
    private async Task SaveMachineModelAsync()
    {
        if (!_dialog.Confirm("保存机种",
                $"把 PLC 当前的 D{MachineModelSection.StartAt}–D{MachineModelSection.end} 数据"
                + $"保存为机种 {_recipe.CurrentRecipeNumber:00}「{_recipe.CurrentRecipeName}」？",
                Views.ConfirmLevel.Neutral, "保存",
                "这会覆盖该机种已保存的数据（不影响 PLC 里的当前值）。"))
        {
            return;
        }

        IsBusy = true;

        try
        {
            var Success = await UiCommand
                .ExecuteAsync(_log, _dialog, "保存机种", () => _recipe.SaveMachineModelAsync(_recipe.CurrentRecipeNumber))
                .ConfigureAwait(true);

            if (Success)
            {
                AtUiThreadExecute(RefreshAll);
                _dialog.Hint("保存完成", $"机种 {_recipe.CurrentRecipeNumber:00} 已保存当前 PLC 数据。");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>下发机种：把当前机种的快照写到 PLC 的 D2500–D3999（不切换机种号）。</summary>
    [RelayCommand]
    private async Task SendMachineModelAsync()
    {
        if (!_dialog.Confirm("下发机种",
                $"把机种 {_recipe.CurrentRecipeNumber:00}「{_recipe.CurrentRecipeName}」的数据下发到 PLC？",
                Views.ConfirmLevel.Warning, "下发",
                $"PLC 的 D{MachineModelSection.StartAt}–D{MachineModelSection.end} 会被整段覆盖，"
                + "确认现场无人员处于设备动作范围。"))
        {
            return;
        }

        IsBusy = true;

        try
        {
            if (await UiCommand
                    .ExecuteAsync(_log, _dialog, "下发机种", () => _recipe.SendMachineModelAsync(_recipe.CurrentRecipeNumber))
                    .ConfigureAwait(true))
            {
                _dialog.Hint("下发完成", $"机种 {_recipe.CurrentRecipeNumber:00} 的数据已写入 PLC。");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>重新从本地文件加载机种清单。</summary>
    [RelayCommand]
    private void Refresh() => RefreshAll();

    // ── 内部 ───────────────────────────────────────────────────────────

    private void RefreshAll()
    {
        CurrentNumber = _recipe.CurrentRecipeNumber;
        CurrentName = _recipe.CurrentRecipeName;

        RecipeCollection.ReplaceAll(_recipe.RecipeCollection.Select(infoItem =>
            new RecipeRowViewModel(infoItem, infoItem.Number == _recipe.CurrentRecipeNumber)));

        var Report = _recipe.LastOnceConsistencyReport;

        if (Report is null)
        {
            ConsistencyText = "尚未做启动一致性校验。";
            HasConsistencyIssue = false;
            return;
        }

        ConsistencyText = Report.Summary;
        HasConsistencyIssue = !Report.IsConsistent;
    }
}
