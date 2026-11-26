using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PlcHmi.Business.Services;

/// <summary>
/// <see cref="PopupMonitorService"/> 的「配置装载 + 触发轮询」部分（partial）。
///
/// <para>从主文件拆出：主文件已顶到项目规模门禁（400 行）。这里的职责单一——
/// 把 <c>config/popups.json</c> 读进内存，并常驻轮询各弹窗的触发线圈；
/// 按钮写入与配置保存留在主文件（那是"操作"，这里是"监视"）。</para>
/// </summary>
public sealed partial class PopupMonitorService
{
    // ── 内部：装载与轮询 ────────────────────────────────────────────────

    private void InstallLoadConfig()
    {
        try
        {
            if (!File.Exists(storePath))
            {
                _log.LogInformation("未配置弹窗（{Path} 不存在），弹窗功能静默待命", storePath);
                return;
            }

            var file = JsonSerializer.Deserialize<PopupFile>(
                File.ReadAllText(storePath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            _popup.Clear();

            if (file is not null)
            {
                _popup.AddRange(file.PopupCollection.Where(Definition => !string.IsNullOrWhiteSpace(Definition.TriggerAddress)));
            }

            _log.LogInformation("已装载 {Count} 个可配置弹窗", _popup.Count);

            // 装载即校验触发地址：地址写错时现场只会看到"弹窗不出现"，
            // 必须在日志里明确说清是哪个弹窗的哪个地址没解析出来。
            foreach (var Definition in _popup)
            {
                if (ParseCoil(Definition.TriggerAddress) is not { } BaseAddress)
                {
                    _log.LogWarning(
                        "弹窗「{Name}」的触发地址「{Address}」不是有效的 M 区线圈，该弹窗不会被触发",
                        Definition.Name, Definition.TriggerAddress);
                    continue;
                }

                _log.LogInformation(
                    "弹窗「{Name}」监听触发地址 {Address}（线圈 {Base}）",
                    Definition.Name, Definition.TriggerAddress, BaseAddress);
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // 收窄为具体类型（读文件 + 反序列化可能抛出的全集），语义不变：
            // 装载失败只停用弹窗功能，不带崩进程
            _log.LogError(exception, "装载弹窗配置失败（{Path}），弹窗功能停用", storePath);
        }
    }

    /// <summary>
    /// 常驻轮询触发线圈（600 ms 一轮）。
    ///
    /// <para><b>为什么不等连接事件：</b>见构造函数注释——连接可能在服务创建之前就已建立，
    /// 等事件会让循环永不启动。这里每一轮自己判断连接状态：未连接就跳过本轮，
    /// 连上以后自然开始读；断开期间不报错，重连后自动恢复。</para>
    ///
    /// <para><b>为什么 try/catch 在循环内：</b>放在循环外时，一次通信异常会永久终止轮询
    /// （弹窗从此彻底失灵，且日志里只有一条 Warning）。放在循环内则"本轮跳过、下轮继续"。</para>
    /// </summary>
    private async Task PollingLoopRing()
    {
        using var weekPeriod = new PeriodicTimer(TimeSpan.FromMilliseconds(600));

        try
        {
            while (await weekPeriod.WaitForNextTickAsync().ConfigureAwait(false))
            {
                if (!_client.IsConnected)
                {
                    continue;
                }

                try
                {
                    await PollingOneRound().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    // 轮询线程绝不能带崩进程：通信抖动会导致弹窗暂时失灵，下轮自动恢复
                    _log.LogWarning(exception, "弹窗触发轮询异常，本轮跳过");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 程序退出
        }
    }

    private async Task PollingOneRound()
    {
        List<CoilPositioning> positioningList;

        lock (_sync)
        {
            positioningList = _popup
                .Select(Definition =>
                {
                    var BaseAddress = ParseCoil(Definition.TriggerAddress);
                    return new CoilPositioning(Definition.Key, BaseAddress ?? -1, BaseAddress is not null);
                })
                .ToList();
        }

        string? newTriggerKey = null;

        foreach (var positioning in positioningList)
        {
            if (!positioning.parseSuccess)
            {
                continue;
            }

            try
            {
                var coilList = await _client
                    .ReadCoilAsync(positioning.BaseAddress, 1)
                    .ConfigureAwait(false);

                if (coilList.Length > 0 && coilList[0])
                {
                    newTriggerKey = positioning.Key;
                    break;
                }
            }
            catch (Exception exception)
            {
                _log.LogWarning(exception, "读取弹窗触发线圈 D 基址 {Base} 失败", positioning.BaseAddress);
            }
        }

        if (newTriggerKey == CurrentTriggerKey)
        {
            return;
        }

        CurrentTriggerKey = newTriggerKey;

        // 触发/消失都记一条：现场排查"为什么没弹窗"时，日志要能一眼看出
        // 上位机到底有没有读到那个线圈。
        if (newTriggerKey is null)
        {
            _log.LogInformation("弹窗触发线圈已断开，弹窗收起");
        }
        else
        {
            var Definition = PopupCollection.FirstOrDefault(Entry => Entry.Key == newTriggerKey);

            _log.LogInformation(
                "弹窗「{Name}」触发（{Address} 闭合），主界面已显示",
                Definition?.Name ?? newTriggerKey, Definition?.TriggerAddress ?? "?");
        }

        triggerChanged?.Invoke(this, newTriggerKey);
    }
}
