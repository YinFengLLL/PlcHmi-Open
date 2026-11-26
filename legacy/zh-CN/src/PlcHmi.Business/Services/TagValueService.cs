using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using 上位机.核心.抽象;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// 点位实时值服务。保存最新值，并<b>仅在值发生变化时</b>推送事件，
/// 避免 UI 因重复赋值而频繁刷新。
/// </summary>
public sealed class 标签值服务 : 标签值服务接口
{
    private readonly ILogger<标签值服务> _日志;
    private readonly ConcurrentDictionary<string, 标签新值> _值列表 = new(StringComparer.OrdinalIgnoreCase);

    public 标签值服务(ILogger<标签值服务> 日志)
    {
        _日志 = 日志;
    }

    /// <inheritdoc />
    public event EventHandler<标签新值>? 新值已变更;

    /// <inheritdoc />
    public 标签新值 获取新值(string 名称值) =>
        _值列表.TryGetValue(名称值, out var 值) ? 值 : 标签新值.无效(名称值);

    /// <inheritdoc />
    public bool 尝试获取双精度(string 名称值, out double 值)
    {
        if (_值列表.TryGetValue(名称值, out var 标签新值) && 标签新值.是否有效 && !double.IsNaN(标签新值.缩放后新值))
        {
            值 = 标签新值.缩放后新值;
            return true;
        }

        值 = double.NaN;
        return false;
    }

    /// <inheritdoc />
    public bool 尝试获取布尔(string 名称值, out bool 值)
    {
        if (_值列表.TryGetValue(名称值, out var 标签新值) && 标签新值.是否有效 && 标签新值.值 is bool 乙)
        {
            值 = 乙;
            return true;
        }

        值 = false;
        return false;
    }

    /// <summary>
    /// 发布一个新值。由轮询引擎调用。
    /// <para>变化检测规则：值与有效性任一发生变化才推送；<c>double</c> 值按 1e-9 容差比较。</para>
    /// </summary>
    public void 发布(标签新值 值)
    {
        var 已变更 = true;

        if (_值列表.TryGetValue(值.名称, out var 上一个))
        {
            已变更 = 上一个.是否有效 != 值.是否有效
                      || 上一个.值?.GetType() != 值.值?.GetType()
                      || !是否近乎相等(上一个, 值);
        }

        _值列表[值.名称] = 值;

        if (已变更)
        {
            try
            {
                新值已变更?.Invoke(this, 值);
            }
            catch (Exception 异常)
            {
                _日志.LogError(异常, "推送点位 {Name} 变化事件时出现异常", 值.名称);
            }
        }
    }

    /// <summary>把全部点位标记为无效（断线时调用，界面据此置灰）。</summary>
    public void 失效全部()
    {
        foreach (var 键值 in _值列表.Keys.ToList())
        {
            // 走 发布() 而不是直接改字典：只有原本有效的点位才会触发
            // 新值已变更 事件，界面才能收到置灰通知（此前静默改值，界面不知情）。
            发布(标签新值.无效(键值));
        }
    }

    /// <summary>
    /// 判断两个点位值是否可视为相等（未变化）。
    /// <para>原始值（尤其 Float32 轴位置）存在位级抖动：位级精确比较会让
    /// 1e-9 容差形同虚设，浮点点位每个轮询周期都误报变化、UI 空转刷新。
    /// 因此浮点点位按<b>缩放后值</b>的容差比较；其余类型精确相等。</para>
    /// </summary>
    private static bool 是否近乎相等(标签新值 上一个, 标签新值 当前) =>
        上一个.值 is float or double
            ? Math.Abs(上一个.缩放后新值 - 当前.缩放后新值) <= 1e-9
            : Equals(上一个.值, 当前.值)
              && Math.Abs(上一个.缩放后新值 - 当前.缩放后新值) <= 1e-9;
}
