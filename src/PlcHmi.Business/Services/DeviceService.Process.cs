using System.Globalization;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// <see cref="设备服务"/> 的「加工状态」部分（partial）。
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
public sealed partial class 设备服务
{
    private bool _锁附完成;
    private bool _点胶完成;
    private bool _加工完成;
    private long _已锁附颗数;

    /// <inheritdoc />
    public bool 锁附完成 => _锁附完成;

    /// <inheritdoc />
    public bool 点胶完成 => _点胶完成;

    /// <inheritdoc />
    public bool 加工完成 => _加工完成;

    /// <inheritdoc />
    public long 已锁附颗数 => _已锁附颗数;

    /// <inheritdoc />
    public event EventHandler<bool>? 锁附状态已变更;

    /// <inheritdoc />
    public event EventHandler<bool>? 点胶状态已变更;

    /// <inheritdoc />
    public event EventHandler<bool>? 加工状态已变更;

    /// <inheritdoc />
    public event EventHandler<long>? 已锁附颗数已变更;

    /// <summary>
    /// 完成状态 → 界面文案与颜色键。
    /// <para><b>色义：</b>完成 = 成功色（绿）；未完成 = 次要色（灰）——未完成是<b>正常</b>的中间态，
    /// 不是报警，用橙色会在机器待机时把整条状态条染成一片黄，反而让真正的报警失色。
    /// 文案同时说明状态，颜色不单独承载信息。</para>
    /// </summary>
    /// <remarks>公开静态：界面文案是现场要核对的行为，由回归测试直接钉住。</remarks>
    public static (string 文本, string 颜色键) 完成状态显示(bool 完成) =>
        完成 ? ("完成", "SuccessBrush") : ("未完成", "MutedBrush");

    /// <summary>用当前点位快照补齐加工状态（构造时调用，事件流只推"变化"）。</summary>
    private void 应用加工初始快照()
    {
        _锁附完成 = 读完成标志("ScrewDone");
        _点胶完成 = 读完成标志("GlueDone");
        _加工完成 = 读完成标志("ProcessDone");

        var 颗数项 = _值列表.获取新值("ScrewCount");
        if (颗数项.是否有效)
        {
            _已锁附颗数 = (long)颗数项.缩放后新值;
        }
    }

    /// <summary>把一条点位变化落到对应的加工状态上；不认识的名称直接忽略。</summary>
    private void 应用加工点位(标签新值 值)
    {
        switch (值.名称)
        {
            case "ScrewDone":
                更新完成状态(ref _锁附完成, 值, 锁附状态已变更);
                break;

            case "GlueDone":
                更新完成状态(ref _点胶完成, 值, 点胶状态已变更);
                break;

            case "ProcessDone":
                更新完成状态(ref _加工完成, 值, 加工状态已变更);
                break;

            case "ScrewCount":
                if (!值.是否有效)
                {
                    break;
                }

                var 颗数 = (long)值.缩放后新值;

                // Int16 有符号：负值（PLC 侧溢出/未初始化）照实显示，不夹取——
                // 显示成 0 会掩盖现场问题；操作员看到负数自然会去查 PLC
                if (_已锁附颗数 != 颗数)
                {
                    _已锁附颗数 = 颗数;
                    已锁附颗数已变更?.Invoke(this, _已锁附颗数);
                }

                break;
        }
    }

    private bool 读完成标志(string 名称值)
    {
        var 值项 = _值列表.获取新值(名称值);

        return 值项 is { 是否有效: true, 值: not null }
               && Convert.ToInt32(值项.值, CultureInfo.InvariantCulture) != 0;
    }

    private void 更新完成状态(ref bool 字段值, 标签新值 值, EventHandler<bool>? 事件)
    {
        if (!值.是否有效 || 值.值 is null)
        {
            return;   // 读失败：保持上一次的显示，不要闪成"未完成"
        }

        var 完成 = Convert.ToInt32(值.值, CultureInfo.InvariantCulture) != 0;

        if (字段值 == 完成)
        {
            return;
        }

        字段值 = 完成;
        事件?.Invoke(this, 字段值);
    }
}
