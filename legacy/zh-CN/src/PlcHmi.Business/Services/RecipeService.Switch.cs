using Microsoft.Extensions.Logging;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// <see cref="配方服务"/> 的「切换机种」部分（partial）。
///
/// <para><b>为什么拆成 partial 文件：</b>主文件已接近 400 行规模门禁基线，
/// 切换机种是独立职责（下发快照 → 提交机种号 → 留底回滚）。</para>
/// </summary>
public sealed partial class 配方服务
{
    /// <inheritdoc />
    public async Task 异步切换(int 配方号, CancellationToken 取消令牌 = default)
    {
        _权限.要求(权限.切换配方);

        校验配方号(配方号);

        _日志.LogInformation("切换机种：{From} → {To}", _当前号, 配方号);

        // 1. 先把该机种的 D2500–D3999 区段快照下发到 PLC，成功后才提交机种号。
        //    顺序很关键："先提交、后下发"会在无数据机种或 PLC 异常时留下半截状态：
        //    界面显示机种 N，PLC 里仍是旧机种、机种号寄存器也没写。
        //    全局参数（D1200 段等）不在这儿下发，它们不随机种变动。
        //    允许空机种：新建的机种本来就没有数据，切换后区段整段清零即可开始示教
        //    （2026-10-21 现场报障：空机种"切不过去"）。
        await 异步下发机种核心(配方号, 取消令牌, 允许空机种: true).ConfigureAwait(false);

        // 2. 下发成功：提交内存与存储层真相（旧号先留着，留底失败要回滚）
        var 旧号 = _当前号;
        _当前号 = 配方号;
        _存储.设置当前(配方号);

        try
        {
            // 3. 本地留底（当前机种号）
            await _存储.异步保存(取消令牌).ConfigureAwait(false);
        }
        catch
        {
            // 留底失败：回滚内存与存储真相。PLC 已是新机种，界面跟着保持一致，
            // 比"界面新、存储旧"两份真相分裂好；下次启动以 PLC 机种号为准
            _当前号 = 旧号;
            _存储.设置当前(旧号);
            throw;
        }

        _日志.LogInformation("机种 {No} 已切换并下发区段快照完成", 配方号);
        配方已变更?.Invoke(this, EventArgs.Empty);
    }
}
