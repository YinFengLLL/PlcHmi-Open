using Microsoft.Extensions.Logging;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// 设备服务。把点位变化汇聚为设备状态、节拍与产量，并封装启停复位急停命令。
/// <para>节拍与产量<b>由 PLC 计算后读取</b>，上位机不自行计时——PLC 的时间基准更可靠，
/// 且不受上位机线程调度影响。</para>
/// </summary>
public sealed partial class 设备服务 : 设备服务接口
{
    /// <summary>命令脉冲宽度。置位后维持该时长再复位。</summary>
    private static readonly TimeSpan 命令脉冲宽度 = TimeSpan.FromMilliseconds(200);

    private readonly 标签值服务 _值列表;
    private readonly 标签写入器 _写入器;
    private readonly 权限守卫接口 _权限;
    private readonly ILogger<设备服务> _日志;

    private 设备状态 _状态值 = 设备状态.未知;
    private double _周期时间秒;
    private long _输出数量;
    private long _目标数量 = 5000;
    private long _良品数量;
    private long _不良品数量;

    /// <summary>
    /// 权限守卫是<b>必填</b>参数（审查标准 R5）：启停/复位/数据清除是设备级命令，
    /// 不能只靠界面隐藏按钮。测试若不覆盖权限分支，请显式传 <see cref="允许全部权限守卫"/>。
    /// </summary>
    public 设备服务(
        标签值服务 值列表, 标签写入器 写入器, 权限守卫接口 权限, ILogger<设备服务> 日志)
    {
        _值列表 = 值列表;
        _写入器 = 写入器;
        _权限 = 权限;
        _日志 = 日志;

        _值列表.新值已变更 += 新值已变更;

        // 事件流只推送“变化”，若本服务在首轮采集之后才被构造，就会永远漏掉初始状态。
        // 因此构造时先主动拉一次当前快照补齐——UI 首屏才不会显示成“未知”。
        应用初始快照();
    }

    /// <summary>用当前点位快照初始化内部状态（不发事件，由使用方按需拉取）。</summary>
    private void 应用初始快照()
    {
        var 状态项 = _值列表.获取新值("RunStatus");
        if (状态项 is { 是否有效: true, 值: not null })
        {
            _状态值 = 映射状态字(Convert.ToInt32(
                状态项.值, System.Globalization.CultureInfo.InvariantCulture));
        }

        var 周期时间 = _值列表.获取新值("CycleTime");
        if (周期时间.是否有效)
        {
            _周期时间秒 = 周期时间.缩放后新值;
        }

        var 输出数量项 = _值列表.获取新值("OutputCount");
        if (输出数量项.是否有效)
        {
            _输出数量 = (long)输出数量项.缩放后新值;
        }

        var 目标数量项 = _值列表.获取新值("TargetCount");
        if (目标数量项.是否有效)
        {
            _目标数量 = (long)目标数量项.缩放后新值;
        }

        var 良品数量项 = _值列表.获取新值("GoodCount");
        if (良品数量项.是否有效)
        {
            _良品数量 = (long)良品数量项.缩放后新值;
        }

        var 不良品数量项 = _值列表.获取新值("BadCount");
        if (不良品数量项.是否有效)
        {
            _不良品数量 = (long)不良品数量项.缩放后新值;
        }

        // 加工状态（锁附/点胶/加工完成 + 已锁附颗数）见 DeviceService.Process.cs
        应用加工初始快照();
    }

    /// <inheritdoc />
    public 设备状态 状态 => _状态值;

    /// <inheritdoc />
    public double 周期时间秒 => _周期时间秒;

    /// <inheritdoc />
    public long 输出数量 => _输出数量;

    /// <inheritdoc />
    public long 目标数量
    {
        get => _目标数量;
        set
        {
            if (_目标数量 == value)
            {
                return;
            }

            _目标数量 = value;
            目标数量已变更?.Invoke(this, value);
        }
    }

    /// <inheritdoc />
    public long 良品数量 => _良品数量;

    /// <inheritdoc />
    public long 不良品数量 => _不良品数量;

    /// <inheritdoc />
    public event EventHandler<设备状态>? 状态已变更;

    /// <inheritdoc />
    public event EventHandler<double>? 周期时间已变更;

    /// <inheritdoc />
    public event EventHandler<long>? 输出数量已变更;

    /// <inheritdoc />
    public event EventHandler<long>? 目标数量已变更;

    /// <inheritdoc />
    public event EventHandler<long>? 良品数量已变更;

    /// <inheritdoc />
    public event EventHandler<long>? 不良品数量已变更;

    /// <inheritdoc />
    public Task 异步启动(CancellationToken 取消令牌 = default)
    {
        // 启停是运行页（操作员可进入）的动作：要求登录且至少有运行页权限。
        // 停止方向同权限——停机是安全动作，不能被权限拦下。
        _权限.要求(权限.视图运行页面);

        _日志.LogInformation("下发启动命令");
        return _写入器.异步脉冲("CmdStart", 命令脉冲宽度, 取消令牌);
    }

    /// <inheritdoc />
    public Task 异步停止(CancellationToken 取消令牌 = default)
    {
        _权限.要求(权限.视图运行页面);

        _日志.LogInformation("下发停止命令");
        return _写入器.异步脉冲("CmdStop", 命令脉冲宽度, 取消令牌);
    }

    /// <inheritdoc />
    public Task 异步重置(CancellationToken 取消令牌 = default)
    {
        _权限.要求(权限.视图运行页面);

        _日志.LogInformation("下发复位命令");
        return _写入器.异步脉冲("CmdReset", 命令脉冲宽度, 取消令牌);
    }

    /// <inheritdoc />
    public Task 异步清空报警(CancellationToken 取消令牌 = default)
    {
        // 与报警页门槛（视图监控页面）一致：操作员及以上
        _权限.要求(权限.视图监控页面);

        _日志.LogInformation("下发报警清除命令");
        return _写入器.异步脉冲("CmdAlarmClear", 命令脉冲宽度, 取消令牌);
    }

    /// <inheritdoc />
    public Task 异步清除数据(CancellationToken 取消令牌 = default)
    {
        // 清零累计数据是调试 / 交接班动作：与界面「可清除数据」同口径（工程师及以上）
        _权限.要求(权限.编辑参数值列表);

        _日志.LogInformation("下发数据清除命令");
        return _写入器.异步脉冲("CmdDataClear", 命令脉冲宽度, 取消令牌);
    }

    private void 新值已变更(object? 发送者, 核心.模型.标签新值 值)
    {
        switch (值.名称)
        {
            case "RunStatus":
                更新状态(值);
                break;

            case "CycleTime":
                if (!值.是否有效)
                {
                    break;
                }

                if (Math.Abs(_周期时间秒 - 值.缩放后新值) > 1e-6)
                {
                    _周期时间秒 = 值.缩放后新值;
                    周期时间已变更?.Invoke(this, _周期时间秒);
                }

                break;

            case "OutputCount":
                if (!值.是否有效)
                {
                    break;
                }

                var 数量值 = (long)值.缩放后新值;
                if (_输出数量 != 数量值)
                {
                    _输出数量 = 数量值;
                    输出数量已变更?.Invoke(this, _输出数量);
                }

                break;

            case "TargetCount":
                if (!值.是否有效)
                {
                    break;
                }

                // PLC 上报的目标产量覆盖应用设置里的初值（地址可在运行页由管理员改）
                目标数量 = (long)值.缩放后新值;
                break;

            case "GoodCount":
                if (!值.是否有效)
                {
                    break;
                }

                var 良品值 = (long)值.缩放后新值;
                if (_良品数量 != 良品值)
                {
                    _良品数量 = 良品值;
                    良品数量已变更?.Invoke(this, _良品数量);
                }

                break;

            case "BadCount":
                if (!值.是否有效)
                {
                    break;
                }

                var 不良品值 = (long)值.缩放后新值;
                if (_不良品数量 != 不良品值)
                {
                    _不良品数量 = 不良品值;
                    不良品数量已变更?.Invoke(this, _不良品数量);
                }

                break;

            default:
                // 加工状态（锁附/点胶/加工完成 + 已锁附颗数）见 DeviceService.Process.cs
                应用加工点位(值);
                break;
        }
    }

    private void 更新状态(核心.模型.标签新值 值)
    {
        var 新状态值 = 值.是否有效 && 值.值 is not null
            ? 映射状态字(Convert.ToInt32(值.值, System.Globalization.CultureInfo.InvariantCulture))
            : 设备状态.未知;

        if (_状态值 == 新状态值)
        {
            return;
        }

        _状态值 = 新状态值;
        _日志.LogInformation("设备状态变为 {State}", 新状态值);
        状态已变更?.Invoke(this, _状态值);
    }

    /// <summary>
    /// 把 PLC 状态字（默认寄存器 D6，运行监控页可改地址）翻译为 <see cref="设备状态"/>。
    ///
    /// <para><b>状态字约定（2026-10-16 现场确认，5 态）：</b>
    /// <c>0=空闲中 1=运行中 2=待机中 3=复位中 4=报警中</c>，其余值视为无效。</para>
    ///
    /// <para><b>为什么不用直接强转：</b>状态字的编号由 PLC 程序决定，而 C# 枚举里
    /// <c>未知=0</c> 是"尚未拿到有效数据"的哨兵值。两者含义不同，必须经映射解耦——
    /// 现场若改了 PLC 状态字编码，只需改这个方法。</para>
    /// </summary>
    public static 设备状态 映射状态字(int 原始值) => 原始值 switch
    {
        0 => 设备状态.空闲,
        1 => 设备状态.运行中,
        2 => 设备状态.待机中,
        3 => 设备状态.复位中,
        4 => 设备状态.报警中,
        _ => 设备状态.未知,
    };

    /// <summary>
    /// 设备状态 → 主监控页显示文案与语义色键（2026-10-16 现场确认的 5 态）。
    /// <para>文案由界面直接展示，色键只做辅助编码——Hero 卡底色深蓝、对比度不达标，
    /// 那里用「图标形状 + 状态文字」双编码（见 <c>设备状态图标转换器</c>），
    /// 颜色不单独承载信息。</para>
    /// </summary>
    /// <remarks>公开静态：界面文案是现场要核对的行为，由回归测试直接钉住。</remarks>
    public static (string 文本, string 颜色键) 状态显示(设备状态 状态值) => 状态值 switch
    {
        设备状态.运行中 => ("运行中", "SuccessBrush"),
        设备状态.空闲 => ("空闲中", "MutedBrush"),
        设备状态.待机中 => ("待机中", "WarningBrush"),
        设备状态.复位中 => ("复位中", "PrimaryLightBrush"),
        设备状态.报警中 => ("报警中", "DangerBrush"),
        _ => ("未知", "MutedBrush"),
    };
}
