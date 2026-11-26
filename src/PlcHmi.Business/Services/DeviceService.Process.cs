using System.Globalization;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// <see cref="DeviceService"/> 的「加工状态」部分（partial）。
///
/// <para><b>2026-10-16 现场要求：</b>主页面左下角在通讯状态灯之后加三盏指示灯
/// （锁附 / 点胶 / 加工完成，<c>0</c> = 未完成、<c>1</c> = 完成）与一个「已锁附颗数」显示，
/// 地址均由管理员在运行监控页「编辑地址」里改。</para>
///
/// <para><b>为什么归设备服务：</b>它们与节拍、产量一样是"点位 → 设备语义"的汇聚，
/// 界面只订阅事件即可；也便于在业务层直接回归（不必起界面）。</para>
///
/// <para><b>为什么完成状态用 bool 而不是原样透传 0/1：</b>PLC 侧写 2、写 -1 之类的脏值不该
/// 让界面显示成"完成度 2"。统一按"非 0 即完成"归一，含义稳定、界面只需两种文案。</para>
/// </summary>
public sealed partial class DeviceService
{
    private bool _screwComplete;
    private bool _dispenseComplete;
    private bool _processComplete;
    private long _screwedPieceCount;

    /// <inheritdoc />
    public bool ScrewComplete => _screwComplete;

    /// <inheritdoc />
    public bool DispenseComplete => _dispenseComplete;

    /// <inheritdoc />
    public bool ProcessComplete => _processComplete;

    /// <inheritdoc />
    public long ScrewedPieceCount => _screwedPieceCount;

    /// <inheritdoc />
    public event EventHandler<bool>? screwStateChanged;

    /// <inheritdoc />
    public event EventHandler<bool>? dispenseStateChanged;

    /// <inheritdoc />
    public event EventHandler<bool>? processStateChanged;

    /// <inheritdoc />
    public event EventHandler<long>? screwedPieceCountChanged;

    /// <summary>
    /// 完成状态 → 界面文案与颜色键。
    /// <para><b>色义：</b>完成 = 成功色（绿）；未完成 = 次要色（灰）——未完成是<b>正常</b>的中间态，
    /// 不是报警，用橙色会在机器待机时把整条状态条染成一片黄，反而让真正的报警失色。
    /// 文案同时说明状态，颜色不单独承载信息。</para>
    /// </summary>
    /// <remarks>公开静态：界面文案是现场要核对的行为，由回归测试直接钉住。</remarks>
    public static (string Text, string ColorKey) CompleteStateShow(bool complete) =>
        complete ? ("完成", "SuccessBrush") : ("未完成", "MutedBrush");

    /// <summary>用当前点位快照补齐加工状态（构造时调用，事件流只推"变化"）。</summary>
    private void ApplyProcessInitialSnapshot()
    {
        _screwComplete = ReadCompleteMarkLog("ScrewDone");
        _dispenseComplete = ReadCompleteMarkLog("GlueDone");
        _processComplete = ReadCompleteMarkLog("ProcessDone");

        var pieceCountItem = _valueList.GetNewValue("ScrewCount");
        if (pieceCountItem.IsValid)
        {
            _screwedPieceCount = (long)pieceCountItem.scaleAfterNewValue;
        }
    }

    /// <summary>把一条点位变化落到对应的加工状态上；不认识的名称直接忽略。</summary>
    private void ApplyProcessPoint(TagNewValue Value)
    {
        switch (Value.Name)
        {
            case "ScrewDone":
                UpdateCompleteState(ref _screwComplete, Value, screwStateChanged);
                break;

            case "GlueDone":
                UpdateCompleteState(ref _dispenseComplete, Value, dispenseStateChanged);
                break;

            case "ProcessDone":
                UpdateCompleteState(ref _processComplete, Value, processStateChanged);
                break;

            case "ScrewCount":
                if (!Value.IsValid)
                {
                    break;
                }

                var pieceCount = (long)Value.scaleAfterNewValue;

                // Int16 有符号：负值（PLC 侧溢出/未初始化）照实显示，不夹取——
                // 显示成 0 会掩盖现场问题；操作员看到负数自然会去查 PLC
                if (_screwedPieceCount != pieceCount)
                {
                    _screwedPieceCount = pieceCount;
                    screwedPieceCountChanged?.Invoke(this, _screwedPieceCount);
                }

                break;
        }
    }

    private bool ReadCompleteMarkLog(string nameValue)
    {
        var valueItem = _valueList.GetNewValue(nameValue);

        return valueItem is { IsValid: true, Value: not null }
               && Convert.ToInt32(valueItem.Value, CultureInfo.InvariantCulture) != 0;
    }

    private void UpdateCompleteState(ref bool fieldValue, TagNewValue Value, EventHandler<bool>? Event)
    {
        if (!Value.IsValid || Value.Value is null)
        {
            return;   // 读失败：保持上一次的显示，不要闪成"未完成"
        }

        var complete = Convert.ToInt32(Value.Value, CultureInfo.InvariantCulture) != 0;

        if (fieldValue == complete)
        {
            return;
        }

        fieldValue = complete;
        Event?.Invoke(this, fieldValue);
    }
}
