using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// 点位实时值服务。保存最新值，并<b>仅在值发生变化时</b>推送事件，
/// 避免 UI 因重复赋值而频繁刷新。
/// </summary>
public sealed class TagValueService : ITagValueService
{
    private readonly ILogger<TagValueService> _log;
    private readonly ConcurrentDictionary<string, TagNewValue> _valueList = new(StringComparer.OrdinalIgnoreCase);

    public TagValueService(ILogger<TagValueService> Log)
    {
        _log = Log;
    }

    /// <inheritdoc />
    public event EventHandler<TagNewValue>? NewValueChanged;

    /// <inheritdoc />
    public TagNewValue GetNewValue(string nameValue) =>
        _valueList.TryGetValue(nameValue, out var Value) ? Value : TagNewValue.Invalid(nameValue);

    /// <inheritdoc />
    public bool TryGetDouble(string nameValue, out double Value)
    {
        if (_valueList.TryGetValue(nameValue, out var TagNewValue) && TagNewValue.IsValid && !double.IsNaN(TagNewValue.scaleAfterNewValue))
        {
            Value = TagNewValue.scaleAfterNewValue;
            return true;
        }

        Value = double.NaN;
        return false;
    }

    /// <inheritdoc />
    public bool TryGetBoolean(string nameValue, out bool Value)
    {
        if (_valueList.TryGetValue(nameValue, out var TagNewValue) && TagNewValue.IsValid && TagNewValue.Value is bool item2)
        {
            Value = item2;
            return true;
        }

        Value = false;
        return false;
    }

    /// <summary>
    /// 发布一个新值。由轮询引擎调用。
    /// <para>变化检测规则：值与有效性任一发生变化才推送；<c>double</c> 值按 1e-9 容差比较。</para>
    /// </summary>
    public void RaiseLayout(TagNewValue Value)
    {
        var changed = true;

        if (_valueList.TryGetValue(Value.Name, out var Previous))
        {
            changed = Previous.IsValid != Value.IsValid
                      || Previous.Value?.GetType() != Value.Value?.GetType()
                      || !IsAlmostEqual(Previous, Value);
        }

        _valueList[Value.Name] = Value;

        if (changed)
        {
            try
            {
                NewValueChanged?.Invoke(this, Value);
            }
            catch (Exception exception)
            {
                _log.LogError(exception, "推送点位 {Name} 变化事件时出现异常", Value.Name);
            }
        }
    }

    /// <summary>把全部点位标记为无效（断线时调用，界面据此置灰）。</summary>
    public void FailEffectAll()
    {
        foreach (var keyValue in _valueList.Keys.ToList())
        {
            // 走 发布() 而不是直接改字典：只有原本有效的点位才会触发
            // 新值已变更 事件，界面才能收到置灰通知（此前静默改值，界面不知情）。
            RaiseLayout(TagNewValue.Invalid(keyValue));
        }
    }

    /// <summary>
    /// 判断两个点位值是否可视为相等（未变化）。
    /// <para>原始值（尤其 Float32 轴位置）存在位级抖动：位级精确比较会让
    /// 1e-9 容差形同虚设，浮点点位每个轮询周期都误报变化、UI 空转刷新。
    /// 因此浮点点位按<b>缩放后值</b>的容差比较；其余类型精确相等。</para>
    /// </summary>
    private static bool IsAlmostEqual(TagNewValue Previous, TagNewValue Current) =>
        Previous.Value is float or double
            ? Math.Abs(Previous.scaleAfterNewValue - Current.scaleAfterNewValue) <= 1e-9
            : Equals(Previous.Value, Current.Value)
              && Math.Abs(Previous.scaleAfterNewValue - Current.scaleAfterNewValue) <= 1e-9;
}
