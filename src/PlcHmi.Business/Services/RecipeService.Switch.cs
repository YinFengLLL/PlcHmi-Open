using Microsoft.Extensions.Logging;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// <see cref="RecipeService"/> 的「切换机种」部分（partial）。
///
/// <para><b>为什么拆成 partial 文件：</b>主文件已接近 400 行规模门禁基线，
/// 切换机种是独立职责（下发快照 → 提交机种号 → 留底回滚）。</para>
/// </summary>
public sealed partial class RecipeService
{
    /// <inheritdoc />
    public async Task SwitchAsync(int RecipeNumber, CancellationToken cancellationToken = default)
    {
        _permission.WantRequire(Permission.SwitchRecipe);

        ValidateRecipeNumber(RecipeNumber);

        _log.LogInformation("切换机种：{From} → {To}", _currentNumber, RecipeNumber);

        // 1. 先把该机种的 D2500–D3999 区段快照下发到 PLC，成功后才提交机种号。
        //    顺序很关键："先提交、后下发"会在无数据机种或 PLC 异常时留下半截状态：
        //    界面显示机种 N，PLC 里仍是旧机种、机种号寄存器也没写。
        //    全局参数（D1200 段等）不在这儿下发，它们不随机种变动。
        //    允许空机种：新建的机种本来就没有数据，切换后区段整段清零即可开始示教
        //    （2026-10-21 现场报障：空机种"切不过去"）。
        await SendMachineModelCoreAsync(RecipeNumber, cancellationToken, allowEmptyMachineModel: true).ConfigureAwait(false);

        // 2. 下发成功：提交内存与存储层真相（旧号先留着，留底失败要回滚）
        var oldNumber = _currentNumber;
        _currentNumber = RecipeNumber;
        _store.SetCurrent(RecipeNumber);

        try
        {
            // 3. 本地留底（当前机种号）
            await _store.SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 留底失败：回滚内存与存储真相。PLC 已是新机种，界面跟着保持一致，
            // 比"界面新、存储旧"两份真相分裂好；下次启动以 PLC 机种号为准
            _currentNumber = oldNumber;
            _store.SetCurrent(oldNumber);
            throw;
        }

        _log.LogInformation("机种 {No} 已切换并下发区段快照完成", RecipeNumber);
        recipeChanged?.Invoke(this, EventArgs.Empty);
    }
}
