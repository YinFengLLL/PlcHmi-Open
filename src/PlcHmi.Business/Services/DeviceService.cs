using Microsoft.Extensions.Logging;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// 设备服务。把点位变化汇聚为设备状态、节拍与产量，并封装启停复位急停命令。
/// <para>节拍与产量<b>由 PLC 计算后读取</b>，上位机不自行计时——PLC 的时间基准更可靠，
/// 且不受上位机线程调度影响。</para>
/// </summary>
public sealed partial class DeviceService : IDeviceService
{
    /// <summary>命令脉冲宽度。置位后维持该时长再复位。</summary>
    private static readonly TimeSpan commandPulseWidth = TimeSpan.FromMilliseconds(200);

    private readonly TagValueService _valueList;
    private readonly TagWriter _writer;
    private readonly IPermissionGuard _permission;
    private readonly ILogger<DeviceService> _log;

    private DeviceState _stateValue = DeviceState.Unknown;
    private double _weekPeriodTimeSeconds;
    private long _outputCount;
    private long _targetCount = 5000;
    private long _goodCount;
    private long _defectiveCount;

    /// <summary>
    /// 权限守卫是<b>必填</b>参数（审查标准 R5）：启停/复位/数据清除是设备级命令，
    /// 不能只靠界面隐藏按钮。测试若不覆盖权限分支，请显式传 <see cref="AllowAllPermissionGuard"/>。
    /// </summary>
    public DeviceService(
        TagValueService ValueList, TagWriter Writer, IPermissionGuard Permission, ILogger<DeviceService> Log)
    {
        _valueList = ValueList;
        _writer = Writer;
        _permission = Permission;
        _log = Log;

        _valueList.NewValueChanged += NewValueChanged;

        // 事件流只推送“变化”，若本服务在首轮采集之后才被构造，就会永远漏掉初始状态。
        // 因此构造时先主动拉一次当前快照补齐——UI 首屏才不会显示成“未知”。
        ApplyInitialSnapshot();
    }

    /// <summary>用当前点位快照初始化内部状态（不发事件，由使用方按需拉取）。</summary>
    private void ApplyInitialSnapshot()
    {
        var stateItem = _valueList.GetNewValue("RunStatus");
        if (stateItem is { IsValid: true, Value: not null })
        {
            _stateValue = MapStatusWord(Convert.ToInt32(
                stateItem.Value, System.Globalization.CultureInfo.InvariantCulture));
        }

        var weekPeriodTime = _valueList.GetNewValue("CycleTime");
        if (weekPeriodTime.IsValid)
        {
            _weekPeriodTimeSeconds = weekPeriodTime.scaleAfterNewValue;
        }

        var outputCountItem = _valueList.GetNewValue("OutputCount");
        if (outputCountItem.IsValid)
        {
            _outputCount = (long)outputCountItem.scaleAfterNewValue;
        }

        var targetCountItem = _valueList.GetNewValue("TargetCount");
        if (targetCountItem.IsValid)
        {
            _targetCount = (long)targetCountItem.scaleAfterNewValue;
        }

        var goodCountItem = _valueList.GetNewValue("GoodCount");
        if (goodCountItem.IsValid)
        {
            _goodCount = (long)goodCountItem.scaleAfterNewValue;
        }

        var defectiveCountItem = _valueList.GetNewValue("BadCount");
        if (defectiveCountItem.IsValid)
        {
            _defectiveCount = (long)defectiveCountItem.scaleAfterNewValue;
        }

        // 加工状态（锁附/点胶/加工完成 + 已锁附颗数）见 DeviceService.Process.cs
        ApplyProcessInitialSnapshot();
    }

    /// <inheritdoc />
    public DeviceState State => _stateValue;

    /// <inheritdoc />
    public double WeekPeriodTimeSeconds => _weekPeriodTimeSeconds;

    /// <inheritdoc />
    public long OutputCount => _outputCount;

    /// <inheritdoc />
    public long TargetCount
    {
        get => _targetCount;
        set
        {
            if (_targetCount == value)
            {
                return;
            }

            _targetCount = value;
            targetCountChanged?.Invoke(this, value);
        }
    }

    /// <inheritdoc />
    public long GoodCount => _goodCount;

    /// <inheritdoc />
    public long DefectiveCount => _defectiveCount;

    /// <inheritdoc />
    public event EventHandler<DeviceState>? StateChanged;

    /// <inheritdoc />
    public event EventHandler<double>? weekPeriodTimeChanged;

    /// <inheritdoc />
    public event EventHandler<long>? outputCountChanged;

    /// <inheritdoc />
    public event EventHandler<long>? targetCountChanged;

    /// <inheritdoc />
    public event EventHandler<long>? goodCountChanged;

    /// <inheritdoc />
    public event EventHandler<long>? defectiveCountChanged;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        // 启停是运行页（操作员可进入）的动作：要求登录且至少有运行页权限。
        // 停止方向同权限——停机是安全动作，不能被权限拦下。
        _permission.WantRequire(Permission.ViewRunPage);

        _log.LogInformation("下发启动命令");
        return _writer.PulseAsync("CmdStart", commandPulseWidth, cancellationToken);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _permission.WantRequire(Permission.ViewRunPage);

        _log.LogInformation("下发停止命令");
        return _writer.PulseAsync("CmdStop", commandPulseWidth, cancellationToken);
    }

    /// <inheritdoc />
    public Task ResetAsync(CancellationToken cancellationToken = default)
    {
        _permission.WantRequire(Permission.ViewRunPage);

        _log.LogInformation("下发复位命令");
        return _writer.PulseAsync("CmdReset", commandPulseWidth, cancellationToken);
    }

    /// <inheritdoc />
    public Task ClearEmptyAlarmAsync(CancellationToken cancellationToken = default)
    {
        // 与报警页门槛（视图监控页面）一致：操作员及以上
        _permission.WantRequire(Permission.ViewMonitorControlPage);

        _log.LogInformation("下发报警清除命令");
        return _writer.PulseAsync("CmdAlarmClear", commandPulseWidth, cancellationToken);
    }

    /// <inheritdoc />
    public Task ClearRemoveDataAsync(CancellationToken cancellationToken = default)
    {
        // 清零累计数据是调试 / 交接班动作：与界面「可清除数据」同口径（工程师及以上）
        _permission.WantRequire(Permission.EditParameterValueList);

        _log.LogInformation("下发数据清除命令");
        return _writer.PulseAsync("CmdDataClear", commandPulseWidth, cancellationToken);
    }

    private void NewValueChanged(object? sender, Core.Models.TagNewValue Value)
    {
        switch (Value.Name)
        {
            case "RunStatus":
                UpdateState(Value);
                break;

            case "CycleTime":
                if (!Value.IsValid)
                {
                    break;
                }

                if (Math.Abs(_weekPeriodTimeSeconds - Value.scaleAfterNewValue) > 1e-6)
                {
                    _weekPeriodTimeSeconds = Value.scaleAfterNewValue;
                    weekPeriodTimeChanged?.Invoke(this, _weekPeriodTimeSeconds);
                }

                break;

            case "OutputCount":
                if (!Value.IsValid)
                {
                    break;
                }

                var countValue = (long)Value.scaleAfterNewValue;
                if (_outputCount != countValue)
                {
                    _outputCount = countValue;
                    outputCountChanged?.Invoke(this, _outputCount);
                }

                break;

            case "TargetCount":
                if (!Value.IsValid)
                {
                    break;
                }

                // PLC 上报的目标产量覆盖应用设置里的初值（地址可在运行页由管理员改）
                TargetCount = (long)Value.scaleAfterNewValue;
                break;

            case "GoodCount":
                if (!Value.IsValid)
                {
                    break;
                }

                var goodValue = (long)Value.scaleAfterNewValue;
                if (_goodCount != goodValue)
                {
                    _goodCount = goodValue;
                    goodCountChanged?.Invoke(this, _goodCount);
                }

                break;

            case "BadCount":
                if (!Value.IsValid)
                {
                    break;
                }

                var defectiveValue = (long)Value.scaleAfterNewValue;
                if (_defectiveCount != defectiveValue)
                {
                    _defectiveCount = defectiveValue;
                    defectiveCountChanged?.Invoke(this, _defectiveCount);
                }

                break;

            default:
                // 加工状态（锁附/点胶/加工完成 + 已锁附颗数）见 DeviceService.Process.cs
                ApplyProcessPoint(Value);
                break;
        }
    }

    private void UpdateState(Core.Models.TagNewValue Value)
    {
        var newStateValue = Value.IsValid && Value.Value is not null
            ? MapStatusWord(Convert.ToInt32(Value.Value, System.Globalization.CultureInfo.InvariantCulture))
            : DeviceState.Unknown;

        if (_stateValue == newStateValue)
        {
            return;
        }

        _stateValue = newStateValue;
        _log.LogInformation("设备状态变为 {State}", newStateValue);
        StateChanged?.Invoke(this, _stateValue);
    }

    /// <summary>
    /// 把 PLC 状态字（默认寄存器 D6，运行监控页可改地址）翻译为 <see cref="DeviceState"/>。
    ///
    /// <para><b>状态字约定（2026-10-16 现场确认，5 态）：</b>
    /// <c>0=空闲中 1=运行中 2=待机中 3=复位中 4=报警中</c>，其余值视为无效。</para>
    ///
    /// <para><b>为什么不用直接强转：</b>状态字的编号由 PLC 程序决定，而 C# 枚举里
    /// <c>未知=0</c> 是"尚未拿到有效数据"的哨兵值。两者含义不同，必须经映射解耦——
    /// 现场若改了 PLC 状态字编码，只需改这个方法。</para>
    /// </summary>
    public static DeviceState MapStatusWord(int SourceValue) => SourceValue switch
    {
        0 => DeviceState.Idle,
        1 => DeviceState.Running,
        2 => DeviceState.Standby,
        3 => DeviceState.Resetting,
        4 => DeviceState.Alarming,
        _ => DeviceState.Unknown,
    };

    /// <summary>
    /// 设备状态 → 主监控页显示文案与语义色键（2026-10-16 现场确认的 5 态）。
    /// <para>文案由界面直接展示，色键只做辅助编码——Hero 卡底色深蓝、对比度不达标，
    /// 那里用「图标形状 + 状态文字」双编码（见 <c>设备状态图标转换器</c>），
    /// 颜色不单独承载信息。</para>
    /// </summary>
    /// <remarks>公开静态：界面文案是现场要核对的行为，由回归测试直接钉住。</remarks>
    public static (string Text, string ColorKey) StateShow(DeviceState StateValue) => StateValue switch
    {
        DeviceState.Running => ("运行中", "SuccessBrush"),
        DeviceState.Idle => ("空闲中", "MutedBrush"),
        DeviceState.Standby => ("待机中", "WarningBrush"),
        DeviceState.Resetting => ("复位中", "PrimaryLightBrush"),
        DeviceState.Alarming => ("报警中", "DangerBrush"),
        _ => ("未知", "MutedBrush"),
    };
}
