using Microsoft.Extensions.Logging;
using PlcHmi.Core.Enums;
using PlcHmi.Communication.Modbus;

namespace PlcHmi.Communication.Simulation;

/// <summary>
/// 虚拟 PLC 设备。按一个简化的装配流程周期性刷新内存映像，
/// 让上位机在没有真实 PLC 的情况下也能跑通完整逻辑。
/// </summary>
/// <remarks>
/// <para>模拟的软元件布局（小型 PLC 地址，与 <c>config/tags.json</c>、<c>config/io-points.json</c>、
/// <c>config/recipe-config.json</c> 保持一致）：</para>
/// <list type="bullet">
///   <item>D6 设备状态字（<b>0=空闲中 1=运行中 2=待机中 3=复位中 4=报警中</b>）</item>
///   <item>D1100 节拍(ms)、D1102 当前产量、D1104 目标产量、D1106 良品、D1108 不良品</item>
///   <item>M490 锁附完成 / M491 点胶完成 / M492 加工完成（<b>M 区位</b>，0=未完成 1=完成）、D1116 已锁附颗数（INT）</item>
///   <item>D1221/D1321/D1421/D1521 四轴当前位置（<b>Float32</b>，各占 2 字；轴参数区偏移 21）</item>
///   <item>D2500–D3299 示教点表（按轴分区：X 2500 / Y 2700 / Z 2900 / R 3100，各 200 字）</item>
///   <item>轴参数区 D1200–D1799（每轴 100 字，2026-09-17 按 PLC 地址分配表）：
///     定位速度 <b>Float32</b> X=D1202 / Y=D1302 / Z=D1402 / R=D1502，
///     点动档位 D1212 / D1312 / D1412 / D1512（每轴 1 字，写 1/2/3），</item>
///   <item>点动方向命令位：D1210.2/.3、D1310.2/.3、D1410.2/.3、D1510.2/.3（D 字内位）</item>
///   <item>D3320–D3329 面启用标志（0=停用，1=启用；默认全停用）</item>
///   <item>D3330–D3333 机种号 / 机种总数 / 示教索引 / 当前面号</item>
///   <item>M200–M203 运行命令（启动/停止/复位/报警清除）、M204 急停信号（物理按钮锁存）、M1000 数据清除</item>
///   <item>X0 上料到位、X2 安全门、X7 急停按钮、X10 料仓满料；Y0/Y1 气缸、Y2 运行灯</item>
/// </list>
/// <para><b>注意：</b>X（物理输入）写在<b>离散输入区</b>，用 FC02 读；Y（输出）写在<b>线圈区</b>，
/// 用 FC01 读——与真机的功能码分工一致。混写会让模拟器自测通过、真机全错。</para>
/// </remarks>
public sealed partial class VirtualPlcDevice : IAsyncDisposable
{
    // ── 软元件地址常量（小型 PLC）───────────────────────────────────────
    private const int coilM200CommandStart = 200;
    private const int coilM201CommandStop = 201;
    private const int coilM202CommandReset = 202;
    private const int coilM203CommandAlarmClearRemove = 203;
    private const int coilM204EmergencyStopSignal = 204;   // 物理急停按钮锁存位（电平保持，复位命令解除）
    private const int coilM1000CommandDataClearRemove = 1000;

    // X 是物理输入：写<b>离散输入区</b>（FC02），与真机口径一致（见 汇川软元件规格表）。
    // 曾写成线圈区，于是模拟器自测通过、真机全错——模拟器必须复刻真机的功能码分工。
    private const int xInputBaseAddress = 63488;     // X0
    private const int yCoilBaseAddress = 64512;     // Y0

    /// <summary>
    /// 报警位起始地址（M500）。
    ///
    /// <para><b>报警区约定（需求方划定）：</b></para>
    /// <list type="bullet">
    ///   <item><b>M500 – M600</b>：101 个位，每位一条报警。</item>
    ///   <item><b>D550 – D554</b>：5 个字 × 16 位 = 80 个报警位，按 <c>D550.1</c> 形式引用。</item>
    /// </list>
    /// <para>上位机 <c>alarms.json</c> 只能在这两段里选地址，最多 100 条报警。</para>
    /// </summary>
    private const int alarmBitBaseAddress = 500;

    private const int registerD6RunState = 6;      // D6 设备状态字
    private const int registerD1100WeekPeriodTime = 1100; // D1100 节拍(ms)
    private const int registerD1102OutputCount = 1102; // D1102 当前产量
    private const int registerD1104TargetOutput = 1104; // D1104 目标产量
    private const int registerD1106GoodCount = 1106; // D1106 良品数量
    private const int registerD1108DefectiveCount = 1108; // D1108 不良品数量

    // ── 工位完成状态与已锁附颗数（主页面左下三灯 + 颗数）─────────────────
    //    三盏灯是 M 区<b>位</b>（FC01 读线圈）；颗数是 D 区 INT。
    //    地址对齐现场 tags.json（2026-10-21 出厂配置更新）：M4004 / M4003 / M4005、D1250。
    private const int coilM4004ScrewComplete = 4004;   // 锁附完成（0/1）
    private const int coilM4003DispenseComplete = 4003;   // 点胶完成（0/1）
    private const int coilM4005ProcessComplete = 4005;   // 加工完成（0/1）
    private const int registerD1250ScrewedPieceCount = 1250; // 已锁附颗数（INT，1 字）
    private const int registerD200AxisTarget = 200;   // D200 轴目标位置

    // ── 轴当前位置（Float32，各占 2 字，CDAB）────────────────────────────
    //    地址对齐现场 tags.json：X/Y/Z/ZZ/GlueZ/GlueX = D100/102/104/106/108/110，
    //    老四轴 R 仍在 D1521（现场未改）。
    private const int registerAxisXPosition = 100;
    private const int registerAxisYPosition = 102;
    private const int registerAxisZPosition = 104;
    private const int registerAxisZZPosition = 106;
    private const int registerAxisGlueZPosition = 108;
    private const int registerAxisGlueXPosition = 110;
    private const int registerAxisRPosition = 1521;

    // ── 机种参数区 / 面启用 / 控制区 ────────────────────────────────────
    // 定位速度：现场配方把六轴速度集中在 D1400 起（每项 2 字）。
    private const int registerXPositioningSpeed = 1400;
    private const int registerYPositioningSpeed = 1402;
    private const int registerZPositioningSpeed = 1404;
    private const int registerZZPositioningSpeed = 1406;
    private const int registerGlueZPositioningSpeed = 1408;
    private const int registerGlueXPositioningSpeed = 1410;
    private const int registerFaceEnableBaseAddress = 3320;   // 每面 1 字：0=停用 1=启用
    private const int registerFaceCount = 10;
    private const int registerRecipeNumber = 3330;
    private const int registerRecipeCount = 3331;
    private const int registerTeachIndex = 3332;
    private const int registerTeachFace = 3333;

    // ── 设备状态字（与 config/tags.json 的 RunStatus 注释一致）────────────
    private const ushort stateIdle = 0;      // 空闲中
    private const ushort stateRunning = 1;   // 运行中
    private const ushort stateAlarming = 4;   // 报警中（急停触发）

    /// <summary>一个完整节拍的模拟时长。</summary>
    private static readonly TimeSpan weekPeriodWeekPeriod = TimeSpan.FromSeconds(8);

    private readonly VirtualPlcMemory _memory = new();
    private readonly ILogger<VirtualPlcDevice> _log;

    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _workerThread;

    private DateTime _weekPeriodStartTimestamp = DateTime.UtcNow;
    private long _outputCount;
    private long _goodCount;
    private long _defectiveCount;
    private long _screwedPieceCount;
    private bool _running;
    private bool _emergencyStop;
    private double _axisPosition;

    public VirtualPlcDevice(ILogger<VirtualPlcDevice> Log)
    {
        _log = Log;
        InitializeMemory();
    }

    /// <summary>虚拟内存映像。</summary>
    public VirtualPlcMemory memory => _memory;

    /// <summary>启动模拟。</summary>
    public void Start()
    {
        if (_workerThread is not null)
        {
            return;
        }

        _cancellationTokenSource = new CancellationTokenSource();
        _workerThread = Task.Run(() => RunAsync(_cancellationTokenSource.Token));
        _log.LogInformation("虚拟 PLC 已启动（节拍周期 {Period} 秒）", weekPeriodWeekPeriod.TotalSeconds);
    }

    /// <summary>停止模拟。</summary>
    public async Task StopAsync()
    {
        if (_cancellationTokenSource is null || _workerThread is null)
        {
            return;
        }

        await _cancellationTokenSource.CancelAsync().ConfigureAwait(false);

        var waitTimeout = TimeSpan.FromSeconds(5);

        try
        {
            // 带超时：模拟循环若卡住，不能拖住程序退出
            await _workerThread.WaitAsync(waitTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 5 秒内没退出 = 模拟循环卡住的征兆，不是正常关闭，必须留下痕迹
            _log.LogWarning("等待虚拟 PLC 模拟循环退出超时（{Seconds}s），已放弃等待", waitTimeout.TotalSeconds);
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
        catch (Exception exception)
        {
            // 循环因自身 bug 挂掉：记录但不影响退出流程
            _log.LogWarning(exception, "虚拟 PLC 模拟循环异常退出");
        }
        finally
        {
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = null;
            _workerThread = null;
        }
    }

    // 内存初始化（初始软元件值）见 VirtualPlcDevice.Memory.cs（partial）。

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                _memory.RunAloneOccupy(Tick);
            }
            catch (Exception exception)
            {
                _log.LogError(exception, "虚拟 PLC 模拟循环出现异常");
            }
        }
    }

    private void Tick()
    {
        // ── 1. 处理命令 ─────────────────────────────────────────────────
        if (_memory.UnsafeGetCoil(coilM200CommandStart))
        {
            _memory.UnsafeSetCoil(coilM200CommandStart, false);
            if (!_emergencyStop)
            {
                _running = true;
                _weekPeriodStartTimestamp = DateTime.UtcNow;
            }
        }

        if (_memory.UnsafeGetCoil(coilM201CommandStop))
        {
            _memory.UnsafeSetCoil(coilM201CommandStop, false);
            _running = false;
        }

        if (_memory.UnsafeGetCoil(coilM202CommandReset))
        {
            _memory.UnsafeSetCoil(coilM202CommandReset, false);
            _running = false;
            _emergencyStop = false;
            _outputCount = 0;
            _goodCount = 0;
            _defectiveCount = 0;
            _screwedPieceCount = 0;
            _axisPosition = 0;
            _weekPeriodStartTimestamp = DateTime.UtcNow;
            SetUInt32(registerD1102OutputCount, 0);
            SetUInt32(registerD1106GoodCount, 0);
            SetUInt32(registerD1108DefectiveCount, 0);
            SetUInt32(registerD1100WeekPeriodTime, 0);

            // 复位解除急停锁存（物理急停按钮松开后由复位清掉信号位）
            _memory.UnsafeSetCoil(coilM204EmergencyStopSignal, false);
        }

        if (_memory.UnsafeGetCoil(coilM203CommandAlarmClearRemove))
        {
            // 脉冲命令：报警锁存由 PLC 程序处理，模拟器直接消费即可
            _memory.UnsafeSetCoil(coilM203CommandAlarmClearRemove, false);
        }

        if (_memory.UnsafeGetCoil(coilM1000CommandDataClearRemove))
        {
            _memory.UnsafeSetCoil(coilM1000CommandDataClearRemove, false);
            _outputCount = 0;
            _goodCount = 0;
            _defectiveCount = 0;
            _screwedPieceCount = 0;
            SetUInt32(registerD1102OutputCount, 0);
            SetUInt32(registerD1106GoodCount, 0);
            SetUInt32(registerD1108DefectiveCount, 0);
        }

        // 急停信号为电平保持：物理急停按钮按下即置位，直到复位命令解除
        _emergencyStop = _memory.UnsafeGetCoil(coilM204EmergencyStopSignal);
        if (_emergencyStop)
        {
            _running = false;
        }

        // ── 2. 运行状态寄存器（状态字约定：0=空闲中 1=运行中 2=待机中 3=复位中 4=报警中）──
        var stateItem = _emergencyStop ? stateAlarming : _running ? stateRunning : stateIdle;
        _memory.UnsafeSetHoldingRegister(registerD6RunState, stateItem);

        // ── 3. 轴运动模拟 ───────────────────────────────────────────────
        var target = _memory.UnsafeGetHoldingRegister(registerD200AxisTarget);
        if (_running)
        {
            _axisPosition += (target - _axisPosition) * 0.15;
            if (Math.Abs(target - _axisPosition) < 0.05)
            {
                _axisPosition = target;
            }
        }

        // 六轴位置按 Float32 写入（CDAB：低字在前，占 2 字）
        // X 轴跟随目标位置；Z 轴模拟下压，取 X 行程的三成；其余保持 0
        SetFloat(registerAxisXPosition, (float)Math.Round(_axisPosition, 2));
        SetFloat(registerAxisYPosition, 0f);
        SetFloat(registerAxisZPosition, (float)Math.Round(-_axisPosition * 0.3, 2));
        SetFloat(registerAxisZZPosition, 0f);
        SetFloat(registerAxisGlueZPosition, 0f);
        SetFloat(registerAxisGlueXPosition, 0f);
        SetFloat(registerAxisRPosition, 0f);

        // ── 4. 节拍与产量 ───────────────────────────────────────────────
        if (_running)
        {
            var alreadyUseWhen = DateTime.UtcNow - _weekPeriodStartTimestamp;
            if (alreadyUseWhen >= weekPeriodWeekPeriod)
            {
                _weekPeriodStartTimestamp = DateTime.UtcNow;
                _outputCount++;
                _screwedPieceCount++;   // 每件都锁附一次（与现场"每件一颗"的口径一致）

                // 每 5 件出 1 件不良品（确定性伪随机，便于测试断言）
                if (_outputCount % 5 == 0)
                {
                    _defectiveCount++;
                }
                else
                {
                    _goodCount++;
                }

                SetUInt32(registerD1102OutputCount, (uint)_outputCount);
                SetUInt32(registerD1106GoodCount, (uint)_goodCount);
                SetUInt32(registerD1108DefectiveCount, (uint)_defectiveCount);
            }

            SetUInt32(registerD1100WeekPeriodTime, (uint)weekPeriodWeekPeriod.TotalMilliseconds);
        }

        // ── 5. 输入点模拟（全部写线圈区）─────────────────────────────────
        var weekPeriodInner = _running
            ? (DateTime.UtcNow - _weekPeriodStartTimestamp).TotalSeconds % weekPeriodWeekPeriod.TotalSeconds
            : -1.0;

        _memory.UnsafeSetDiscreteInput(xInputBaseAddress + 0, _running && weekPeriodInner < 3.0);      // X0 上料到位（常开）
        _memory.UnsafeSetDiscreteInput(xInputBaseAddress + 1, _running && weekPeriodInner is > 3.0 and < 6.0); // X1 压装到位（常开）

        // X2 安全门（常闭）：触点闭合 = 门关好。模拟中门始终关好 → 原始位恒为 1。
        _memory.UnsafeSetDiscreteInput(xInputBaseAddress + 2, true);

        // X7 急停按钮（常闭）：未按下时触点闭合 → 原始位为 1；按下后断开 → 0。
        _memory.UnsafeSetDiscreteInput(xInputBaseAddress + 7, !_emergencyStop);

        _memory.UnsafeSetDiscreteInput(xInputBaseAddress + 8, _outputCount % 10 < 7);          // X10 料仓满料（常开）

        // ── 5b. 工位完成状态与已锁附颗数（主页面左下三灯 + 颗数）─────────
        //    按节拍阶段给出确定性信号：锁附(3s) → 点胶(5s) → 加工(7s) 逐级置 1，
        //    空闲 / 急停时归 0——现场语义是"本件还没做完"。
        _memory.UnsafeSetCoil(coilM4004ScrewComplete, _running && weekPeriodInner >= 3.0);
        _memory.UnsafeSetCoil(coilM4003DispenseComplete, _running && weekPeriodInner >= 5.0);
        _memory.UnsafeSetCoil(coilM4005ProcessComplete, _running && weekPeriodInner >= 7.0);
        _memory.UnsafeSetHoldingRegister(registerD1250ScrewedPieceCount,
            (ushort)Math.Clamp(_screwedPieceCount, 0, short.MaxValue));

        // ── 6. 报警位模拟（M500 起）──────────────────────────────────────
        //    M500–M600 是留给上位机的报警区；位顺序与 config/alarms.json 的默认配置一致。
        //    上位机的报警扫描服务读这些位，再翻译成具体报警文本显示在报警栏。
        _memory.UnsafeSetCoil(alarmBitBaseAddress + 0, _emergencyStop);                        // M500 急停
        _memory.UnsafeSetCoil(alarmBitBaseAddress + 1, _running && weekPeriodInner is > 6.0 and < 7.5);   // M501 上料超时
        _memory.UnsafeSetCoil(alarmBitBaseAddress + 2, _outputCount % 20 >= 18);                  // M502 余料不足
        _memory.UnsafeSetCoil(alarmBitBaseAddress + 3, false);                                    // M503 安全门（模拟中始终关好）

        // ── 6. 输出点模拟 ───────────────────────────────────────────────
        _memory.UnsafeSetCoil(yCoilBaseAddress + 0, _running && weekPeriodInner < 3.0);      // Y0 上料气缸
        _memory.UnsafeSetCoil(yCoilBaseAddress + 1, _running && weekPeriodInner is > 3.0 and < 6.0); // Y1 压装气缸
        _memory.UnsafeSetCoil(yCoilBaseAddress + 2, _running);                       // Y2 运行指示灯
        _memory.UnsafeSetCoil(yCoilBaseAddress + 8, _emergencyStop);              // Y10 蜂鸣器
    }

    // ── 32 位存取（CDAB：低字在前，与汇川默认一致）──────────────────────

    /// <summary>
    /// 按 32 位无符号写入两个寄存器。
    /// <para>字序规则统一走 <c>寄存器编解码器.编码</c>（R1）：模拟器与真实客户端
    /// 必须共用同一份字序实现，否则自测结论在真机上失效。</para>
    /// </summary>
    private void SetUInt32(int addressValue, uint Value)
    {
        var word = RegisterCodec.Encode(TagDataType.UInt32, Value, WordOrder.LittleEndian);
        _memory.UnsafeSetHoldingRegister(addressValue, word[0]);       // 低字
        _memory.UnsafeSetHoldingRegister(addressValue + 1, word[1]);   // 高字
    }

    /// <summary>
    /// 按 32 位浮点写入两个寄存器（CDAB 字序，低字在前）。
    /// <para>与 <c>RegisterCodec.Encode(Float32, …, LittleEndian)</c> 的规则一致，
    /// 且直接复用其实现而非重写。</para>
    /// </summary>
    private void SetFloat(int addressValue, float Value)
    {
        var word = RegisterCodec.Encode(TagDataType.Float32, Value, WordOrder.LittleEndian);
        _memory.UnsafeSetHoldingRegister(addressValue, word[0]);
        _memory.UnsafeSetHoldingRegister(addressValue + 1, word[1]);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
