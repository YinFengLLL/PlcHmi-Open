using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using 上位机.应用.服务;
using 上位机.核心.抽象;

namespace 上位机.应用.视图模型;

/// <summary>
/// <see cref="配方视图模型"/> 的「CSV 导入导出」部分（partial）。
///
/// <para>CSV 是交换格式：给 Excel 编辑、机种之间拷贝用；主存仍是 recipes.json，
/// 导入后要生效还需「切换机种」或「下发机种」。拆出来是为了守住单文件 400 行门禁。</para>
/// </summary>
public sealed partial class 配方视图模型
{
    // ── CSV 导入导出 ───────────────────────────────────────────────────

    /// <summary>把当前机种导出为 CSV（可在 Excel 里编辑）。</summary>
    [RelayCommand]
    private async Task 异步导出CSV()
    {
        var 文件名称 = string.IsNullOrWhiteSpace(_配方.当前配方名称)
            ? $"recipe-{_配方.当前配方号:00}.csv"
            : $"recipe-{_配方.当前配方号:00}-{_配方.当前配方名称}.csv";

        var 文件路径 = _文件对话框.选择保存文件("导出机种配方", 文件对话框服务.CSV筛选, 文件名称);

        if (文件路径 is null)
        {
            return;
        }

        try
        {
            是否忙碌 = true;
            await _配方.异步导出CSV到文件(_配方.当前配方号, 文件路径).ConfigureAwait(false);

            _日志.LogInformation("已导出机种 {No} 到 {Path}", _配方.当前配方号, 文件路径);
            _对话框.提示("导出成功",
                $"机种 {_配方.当前配方号:00} 已导出：\n\n{文件路径}\n\n" +
                "可直接用 Excel 打开编辑，改完再通过「导入 CSV」写回。");
        }
        catch (Exception 异常)
        {
            _日志.LogError(异常, "导出 CSV 失败");
            _对话框.错误信息("导出失败", 异常.Message);
        }
        finally
        {
            是否忙碌 = false;
        }
    }

    /// <summary>把全部机种批量导出到一个目录。</summary>
    [RelayCommand]
    private async Task 异步导出全部CSV()
    {
        var 目录 = _文件对话框.选择文件夹("选择导出目录");

        if (目录 is null)
        {
            return;
        }

        try
        {
            是否忙碌 = true;
            var 文件列表 = await _配方.异步导出全部CSV到目录(目录).ConfigureAwait(false);

            _对话框.提示("批量导出完成",
                $"已导出 {文件列表.Count} 个机种的 CSV 到：\n\n{目录}\n\n" +
                "没有数据的机种也会导出为空白模板，方便先填后导。");
        }
        catch (Exception 异常)
        {
            _日志.LogError(异常, "批量导出 CSV 失败");
            _对话框.错误信息("导出失败", 异常.Message);
        }
        finally
        {
            是否忙碌 = false;
        }
    }

    /// <summary>从 CSV 导入机种配方（只更新本机数据，不写 PLC）。</summary>
    [RelayCommand]
    private async Task 异步导入CSV()
    {
        var 文件路径 = _文件对话框.选择打开文件("导入机种配方", 文件对话框服务.CSV筛选);

        if (文件路径 is null)
        {
            return;
        }

        try
        {
            是否忙碌 = true;

            // 目标机种号以文件里写的为准 —— 这样导出的文件可以直接导回原机种，
            // 也可以先切到别的机种再导入，实现「拷贝」
            var 结果值 = await _配方.异步从文件导入CSV(文件路径).ConfigureAwait(false);

            if (!结果值.成功 || 结果值.配方 is null)
            {
                _对话框.错误信息("导入失败", string.Join(Environment.NewLine, 结果值.错误集合));
                return;
            }

            在界面线程执行(刷新全部);

            var 消息 = 结果值.摘要;

            if (结果值.警告.Count > 0)
            {
                消息 += "\n\n提示：\n" + string.Join(
                    Environment.NewLine, 结果值.警告.Take(6));
            }

            消息 += "\n\n注意：导入只更新本机数据与界面，需按「下发到 PLC」才写入设备。";
            消息 += $"\n若要让当前机种生效，请在列表中切换到机种 {结果值.配方.号:00}。";

            _对话框.提示("导入完成", 消息);
        }
        catch (Exception 异常)
        {
            _日志.LogError(异常, "导入 CSV 失败");
            _对话框.错误信息("导入失败", 异常.Message);
        }
        finally
        {
            是否忙碌 = false;
        }
    }

    /// <summary>把本地机种数据下发给当前机种（用于一致性差异后的手动同步）。</summary>
    [RelayCommand]
    private async Task 异步推送到PLC()
    {
        if (!_对话框.确认("下发参数",
                $"确认把本机数据（机种 {_配方.当前配方号:00}）下发到 PLC？",
                视图.确认等级.警告, "下发",
                "这会覆盖 PLC 参数区中当前的值。"))
        {
            return;
        }

        try
        {
            是否忙碌 = true;
            await _配方.异步写入参数().ConfigureAwait(false);

            在界面线程执行(刷新全部);
            _对话框.提示("下发完成", "本机数据已写入 PLC。");
        }
        catch (Exception 异常)
        {
            _日志.LogError(异常, "下发参数失败");
            _对话框.错误信息("下发失败", 异常.Message);
        }
        finally
        {
            是否忙碌 = false;
        }
    }

    /// <summary>
    /// 从 PLC 读取当前参数（与「下发」相反方向）。
    ///
    /// <para>用途：现场直接在 PLC 侧改了参数（如调试时用 PLC 编程软件改定位速度），
    /// 用此功能把 PLC 的值同步回上位机并留底，避免本地与 PLC 各持一份互相覆盖。</para>
    /// </summary>
    [RelayCommand]
    private async Task 异步从PLC拉取()
    {
        if (!_对话框.确认("从 PLC 读取参数",
                $"确认读取 PLC 参数区当前值（机种 {_配方.当前配方号:00}）？\n\n"
                + "这会以 PLC 的值覆盖界面与本地当前机种数据（不写 PLC）。\n"
                + "若 PLC 值超出允许范围将被整体拒绝，本地保持不变。"))
        {
            return;
        }

        try
        {
            是否忙碌 = true;

            var 变更 = await _配方.异步读取PLC参数().ConfigureAwait(false);

            在界面线程执行(刷新全部);

            _对话框.提示(
                "读取完成",
                变更.Count == 0
                    ? "PLC 参数与本地一致，无变化。"
                    : $"已按 PLC 值更新 {变更.Count} 项：\n\n" + string.Join("\n", 变更));
        }
        catch (InvalidOperationException 异常)
        {
            // 服务层范围校验拒绝：整体未生效，本地保持原值
            _对话框.错误信息("读取被拒绝", 异常.Message);
        }
        catch (Exception 异常)
        {
            _日志.LogError(异常, "从 PLC 读取参数失败");
            _对话框.错误信息("读取失败", 异常.Message);
        }
        finally
        {
            是否忙碌 = false;
        }
    }

}
