using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace 上位机.业务.服务;

/// <summary>
/// <see cref="弹窗监控服务"/> 的「配置装载 + 触发轮询」部分（partial）。
///
/// <para>从主文件拆出：主文件已顶到项目规模门禁（400 行）。这里的职责单一——
/// 把 <c>config/popups.json</c> 读进内存，并常驻轮询各弹窗的触发线圈；
/// 按钮写入与配置保存留在主文件（那是"操作"，这里是"监视"）。</para>
/// </summary>
public sealed partial class 弹窗监控服务
{
    // ── 内部：装载与轮询 ────────────────────────────────────────────────

    private void 装载配置()
    {
        try
        {
            if (!File.Exists(存储路径))
            {
                _日志.LogInformation("未配置弹窗（{Path} 不存在），弹窗功能静默待命", 存储路径);
                return;
            }

            var 文件 = JsonSerializer.Deserialize<弹窗文件>(
                File.ReadAllText(存储路径), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            _弹窗.Clear();

            if (文件 is not null)
            {
                _弹窗.AddRange(文件.弹窗集合.Where(定义 => !string.IsNullOrWhiteSpace(定义.触发地址)));
            }

            _日志.LogInformation("已装载 {Count} 个可配置弹窗", _弹窗.Count);

            // 装载即校验触发地址：地址写错时现场只会看到"弹窗不出现"，
            // 必须在日志里明确说清是哪个弹窗的哪个地址没解析出来。
            foreach (var 定义 in _弹窗)
            {
                if (解析线圈(定义.触发地址) is not { } 基址)
                {
                    _日志.LogWarning(
                        "弹窗「{Name}」的触发地址「{Address}」不是有效的 M 区线圈，该弹窗不会被触发",
                        定义.名称, 定义.触发地址);
                    continue;
                }

                _日志.LogInformation(
                    "弹窗「{Name}」监听触发地址 {Address}（线圈 {Base}）",
                    定义.名称, 定义.触发地址, 基址);
            }
        }
        catch (Exception 异常) when (异常 is JsonException or IOException or UnauthorizedAccessException)
        {
            // 收窄为具体类型（读文件 + 反序列化可能抛出的全集），语义不变：
            // 装载失败只停用弹窗功能，不带崩进程
            _日志.LogError(异常, "装载弹窗配置失败（{Path}），弹窗功能停用", 存储路径);
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
    private async Task 轮询循环()
    {
        using var 周期 = new PeriodicTimer(TimeSpan.FromMilliseconds(600));

        try
        {
            while (await 周期.WaitForNextTickAsync().ConfigureAwait(false))
            {
                if (!_客户端.是否已连接)
                {
                    continue;
                }

                try
                {
                    await 轮询一轮().ConfigureAwait(false);
                }
                catch (Exception 异常)
                {
                    // 轮询线程绝不能带崩进程：通信抖动会导致弹窗暂时失灵，下轮自动恢复
                    _日志.LogWarning(异常, "弹窗触发轮询异常，本轮跳过");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 程序退出
        }
    }

    private async Task 轮询一轮()
    {
        List<线圈定位> 定位列表;

        lock (_同步)
        {
            定位列表 = _弹窗
                .Select(定义 =>
                {
                    var 基址 = 解析线圈(定义.触发地址);
                    return new 线圈定位(定义.键, 基址 ?? -1, 基址 is not null);
                })
                .ToList();
        }

        string? 新触发键 = null;

        foreach (var 定位 in 定位列表)
        {
            if (!定位.解析成功)
            {
                continue;
            }

            try
            {
                var 线圈列表 = await _客户端
                    .异步读取线圈(定位.基址, 1)
                    .ConfigureAwait(false);

                if (线圈列表.Length > 0 && 线圈列表[0])
                {
                    新触发键 = 定位.键;
                    break;
                }
            }
            catch (Exception 异常)
            {
                _日志.LogWarning(异常, "读取弹窗触发线圈 D 基址 {Base} 失败", 定位.基址);
            }
        }

        if (新触发键 == 当前触发键)
        {
            return;
        }

        当前触发键 = 新触发键;

        // 触发/消失都记一条：现场排查"为什么没弹窗"时，日志要能一眼看出
        // 上位机到底有没有读到那个线圈。
        if (新触发键 is null)
        {
            _日志.LogInformation("弹窗触发线圈已断开，弹窗收起");
        }
        else
        {
            var 定义 = 弹窗集合.FirstOrDefault(条目 => 条目.键 == 新触发键);

            _日志.LogInformation(
                "弹窗「{Name}」触发（{Address} 闭合），主界面已显示",
                定义?.名称 ?? 新触发键, 定义?.触发地址 ?? "?");
        }

        触发已变更?.Invoke(this, 新触发键);
    }
}
