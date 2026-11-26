using Microsoft.Extensions.Logging;
using 上位机.核心.枚举;
using 上位机.通信.Modbus;

namespace 上位机.通信.仿真;

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
public sealed partial class 虚拟PLC设备 : IAsyncDisposable
{
    // ── 软元件地址常量（小型 PLC）───────────────────────────────────────
    private const int 线圈M200命令启动 = 200;
    private const int 线圈M201命令停止 = 201;
    private const int 线圈M202命令复位 = 202;
    private const int 线圈M203命令报警清除 = 203;
    private const int 线圈M204急停信号 = 204;   // 物理急停按钮锁存位（电平保持，复位命令解除）
    private const int 线圈M1000命令数据清除 = 1000;

    // X 是物理输入：写<b>离散输入区</b>（FC02），与真机口径一致（见 汇川软元件规格表）。
    // 曾写成线圈区，于是模拟器自测通过、真机全错——模拟器必须复刻真机的功能码分工。
    private const int X输入基址 = 63488;     // X0
    private const int Y线圈基址 = 64512;     // Y0

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
    private const int 报警位基址 = 500;

    private const int 寄存器D6运行状态 = 6;      // D6 设备状态字
    private const int 寄存器D1100周期时间 = 1100; // D1100 节拍(ms)
    private const int 寄存器D1102输出数量 = 1102; // D1102 当前产量
    private const int 寄存器D1104目标产量 = 1104; // D1104 目标产量
    private const int 寄存器D1106良品数量 = 1106; // D1106 良品数量
    private const int 寄存器D1108不良品数量 = 1108; // D1108 不良品数量

    // ── 工位完成状态与已锁附颗数（主页面左下三灯 + 颗数）─────────────────
    //    三盏灯是 M 区<b>位</b>（FC01 读线圈）；颗数是 D 区 INT。
    //    地址对齐现场 tags.json（2026-10-21 出厂配置更新）：M4004 / M4003 / M4005、D1250。
    private const int 线圈M4004锁附完成 = 4004;   // 锁附完成（0/1）
    private const int 线圈M4003点胶完成 = 4003;   // 点胶完成（0/1）
    private const int 线圈M4005加工完成 = 4005;   // 加工完成（0/1）
    private const int 寄存器D1250已锁附颗数 = 1250; // 已锁附颗数（INT，1 字）
    private const int 寄存器D200轴目标 = 200;   // D200 轴目标位置

    // ── 轴当前位置（Float32，各占 2 字，CDAB）────────────────────────────
    //    地址对齐现场 tags.json：X/Y/Z/ZZ/GlueZ/GlueX = D100/102/104/106/108/110，
    //    老四轴 R 仍在 D1521（现场未改）。
    private const int 寄存器轴X位置 = 100;
    private const int 寄存器轴Y位置 = 102;
    private const int 寄存器轴Z位置 = 104;
    private const int 寄存器轴ZZ位置 = 106;
    private const int 寄存器轴GlueZ位置 = 108;
    private const int 寄存器轴GlueX位置 = 110;
    private const int 寄存器轴R位置 = 1521;

    // ── 机种参数区 / 面启用 / 控制区 ────────────────────────────────────
    // 定位速度：现场配方把六轴速度集中在 D1400 起（每项 2 字）。
    private const int 寄存器X定位速度 = 1400;
    private const int 寄存器Y定位速度 = 1402;
    private const int 寄存器Z定位速度 = 1404;
    private const int 寄存器ZZ定位速度 = 1406;
    private const int 寄存器GlueZ定位速度 = 1408;
    private const int 寄存器GlueX定位速度 = 1410;
    private const int 寄存器面启用基址 = 3320;   // 每面 1 字：0=停用 1=启用
    private const int 寄存器面数量 = 10;
    private const int 寄存器配方号 = 3330;
    private const int 寄存器配方数量 = 3331;
    private const int 寄存器示教序号 = 3332;
    private const int 寄存器示教面 = 3333;

    // ── 设备状态字（与 config/tags.json 的 RunStatus 注释一致）────────────
    private const ushort 状态空闲 = 0;      // 空闲中
    private const ushort 状态运行中 = 1;   // 运行中
    private const ushort 状态报警中 = 4;   // 报警中（急停触发）

    /// <summary>一个完整节拍的模拟时长。</summary>
    private static readonly TimeSpan 周期周期 = TimeSpan.FromSeconds(8);

    private readonly 虚拟PLC内存 _内存 = new();
    private readonly ILogger<虚拟PLC设备> _日志;

    private CancellationTokenSource? _取消令牌源;
    private Task? _工作线程;

    private DateTime _周期开始时刻 = DateTime.UtcNow;
    private long _输出数量;
    private long _良品数量;
    private long _不良品数量;
    private long _已锁附颗数;
    private bool _运行中;
    private bool _急停;
    private double _轴位置;

    public 虚拟PLC设备(ILogger<虚拟PLC设备> 日志)
    {
        _日志 = 日志;
        初始化内存();
    }

    /// <summary>虚拟内存映像。</summary>
    public 虚拟PLC内存 内存 => _内存;

    /// <summary>启动模拟。</summary>
    public void 启动()
    {
        if (_工作线程 is not null)
        {
            return;
        }

        _取消令牌源 = new CancellationTokenSource();
        _工作线程 = Task.Run(() => 异步运行(_取消令牌源.Token));
        _日志.LogInformation("虚拟 PLC 已启动（节拍周期 {Period} 秒）", 周期周期.TotalSeconds);
    }

    /// <summary>停止模拟。</summary>
    public async Task 异步停止()
    {
        if (_取消令牌源 is null || _工作线程 is null)
        {
            return;
        }

        await _取消令牌源.CancelAsync().ConfigureAwait(false);

        var 等待超时 = TimeSpan.FromSeconds(5);

        try
        {
            // 带超时：模拟循环若卡住，不能拖住程序退出
            await _工作线程.WaitAsync(等待超时).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 5 秒内没退出 = 模拟循环卡住的征兆，不是正常关闭，必须留下痕迹
            _日志.LogWarning("等待虚拟 PLC 模拟循环退出超时（{Seconds}s），已放弃等待", 等待超时.TotalSeconds);
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
        catch (Exception 异常)
        {
            // 循环因自身 bug 挂掉：记录但不影响退出流程
            _日志.LogWarning(异常, "虚拟 PLC 模拟循环异常退出");
        }
        finally
        {
            _取消令牌源.Dispose();
            _取消令牌源 = null;
            _工作线程 = null;
        }
    }

    // 内存初始化（初始软元件值）见 VirtualPlcDevice.Memory.cs（partial）。

    private async Task 异步运行(CancellationToken 取消令牌)
    {
        using var 计时器 = new PeriodicTimer(TimeSpan.FromMilliseconds(100));

        while (await 计时器.WaitForNextTickAsync(取消令牌).ConfigureAwait(false))
        {
            try
            {
                _内存.运行独占(滴答);
            }
            catch (Exception 异常)
            {
                _日志.LogError(异常, "虚拟 PLC 模拟循环出现异常");
            }
        }
    }

    private void 滴答()
    {
        // ── 1. 处理命令 ─────────────────────────────────────────────────
        if (_内存.不安全获取线圈(线圈M200命令启动))
        {
            _内存.不安全设置线圈(线圈M200命令启动, false);
            if (!_急停)
            {
                _运行中 = true;
                _周期开始时刻 = DateTime.UtcNow;
            }
        }

        if (_内存.不安全获取线圈(线圈M201命令停止))
        {
            _内存.不安全设置线圈(线圈M201命令停止, false);
            _运行中 = false;
        }

        if (_内存.不安全获取线圈(线圈M202命令复位))
        {
            _内存.不安全设置线圈(线圈M202命令复位, false);
            _运行中 = false;
            _急停 = false;
            _输出数量 = 0;
            _良品数量 = 0;
            _不良品数量 = 0;
            _已锁附颗数 = 0;
            _轴位置 = 0;
            _周期开始时刻 = DateTime.UtcNow;
            设置UInt32(寄存器D1102输出数量, 0);
            设置UInt32(寄存器D1106良品数量, 0);
            设置UInt32(寄存器D1108不良品数量, 0);
            设置UInt32(寄存器D1100周期时间, 0);

            // 复位解除急停锁存（物理急停按钮松开后由复位清掉信号位）
            _内存.不安全设置线圈(线圈M204急停信号, false);
        }

        if (_内存.不安全获取线圈(线圈M203命令报警清除))
        {
            // 脉冲命令：报警锁存由 PLC 程序处理，模拟器直接消费即可
            _内存.不安全设置线圈(线圈M203命令报警清除, false);
        }

        if (_内存.不安全获取线圈(线圈M1000命令数据清除))
        {
            _内存.不安全设置线圈(线圈M1000命令数据清除, false);
            _输出数量 = 0;
            _良品数量 = 0;
            _不良品数量 = 0;
            _已锁附颗数 = 0;
            设置UInt32(寄存器D1102输出数量, 0);
            设置UInt32(寄存器D1106良品数量, 0);
            设置UInt32(寄存器D1108不良品数量, 0);
        }

        // 急停信号为电平保持：物理急停按钮按下即置位，直到复位命令解除
        _急停 = _内存.不安全获取线圈(线圈M204急停信号);
        if (_急停)
        {
            _运行中 = false;
        }

        // ── 2. 运行状态寄存器（状态字约定：0=空闲中 1=运行中 2=待机中 3=复位中 4=报警中）──
        var 状态项 = _急停 ? 状态报警中 : _运行中 ? 状态运行中 : 状态空闲;
        _内存.不安全设置保持寄存器(寄存器D6运行状态, 状态项);

        // ── 3. 轴运动模拟 ───────────────────────────────────────────────
        var 目标 = _内存.不安全获取保持寄存器(寄存器D200轴目标);
        if (_运行中)
        {
            _轴位置 += (目标 - _轴位置) * 0.15;
            if (Math.Abs(目标 - _轴位置) < 0.05)
            {
                _轴位置 = 目标;
            }
        }

        // 六轴位置按 Float32 写入（CDAB：低字在前，占 2 字）
        // X 轴跟随目标位置；Z 轴模拟下压，取 X 行程的三成；其余保持 0
        设置浮点(寄存器轴X位置, (float)Math.Round(_轴位置, 2));
        设置浮点(寄存器轴Y位置, 0f);
        设置浮点(寄存器轴Z位置, (float)Math.Round(-_轴位置 * 0.3, 2));
        设置浮点(寄存器轴ZZ位置, 0f);
        设置浮点(寄存器轴GlueZ位置, 0f);
        设置浮点(寄存器轴GlueX位置, 0f);
        设置浮点(寄存器轴R位置, 0f);

        // ── 4. 节拍与产量 ───────────────────────────────────────────────
        if (_运行中)
        {
            var 已用时 = DateTime.UtcNow - _周期开始时刻;
            if (已用时 >= 周期周期)
            {
                _周期开始时刻 = DateTime.UtcNow;
                _输出数量++;
                _已锁附颗数++;   // 每件都锁附一次（与现场"每件一颗"的口径一致）

                // 每 5 件出 1 件不良品（确定性伪随机，便于测试断言）
                if (_输出数量 % 5 == 0)
                {
                    _不良品数量++;
                }
                else
                {
                    _良品数量++;
                }

                设置UInt32(寄存器D1102输出数量, (uint)_输出数量);
                设置UInt32(寄存器D1106良品数量, (uint)_良品数量);
                设置UInt32(寄存器D1108不良品数量, (uint)_不良品数量);
            }

            设置UInt32(寄存器D1100周期时间, (uint)周期周期.TotalMilliseconds);
        }

        // ── 5. 输入点模拟（全部写线圈区）─────────────────────────────────
        var 周期内 = _运行中
            ? (DateTime.UtcNow - _周期开始时刻).TotalSeconds % 周期周期.TotalSeconds
            : -1.0;

        _内存.不安全设置离散输入(X输入基址 + 0, _运行中 && 周期内 < 3.0);      // X0 上料到位（常开）
        _内存.不安全设置离散输入(X输入基址 + 1, _运行中 && 周期内 is > 3.0 and < 6.0); // X1 压装到位（常开）

        // X2 安全门（常闭）：触点闭合 = 门关好。模拟中门始终关好 → 原始位恒为 1。
        _内存.不安全设置离散输入(X输入基址 + 2, true);

        // X7 急停按钮（常闭）：未按下时触点闭合 → 原始位为 1；按下后断开 → 0。
        _内存.不安全设置离散输入(X输入基址 + 7, !_急停);

        _内存.不安全设置离散输入(X输入基址 + 8, _输出数量 % 10 < 7);          // X10 料仓满料（常开）

        // ── 5b. 工位完成状态与已锁附颗数（主页面左下三灯 + 颗数）─────────
        //    按节拍阶段给出确定性信号：锁附(3s) → 点胶(5s) → 加工(7s) 逐级置 1，
        //    空闲 / 急停时归 0——现场语义是"本件还没做完"。
        _内存.不安全设置线圈(线圈M4004锁附完成, _运行中 && 周期内 >= 3.0);
        _内存.不安全设置线圈(线圈M4003点胶完成, _运行中 && 周期内 >= 5.0);
        _内存.不安全设置线圈(线圈M4005加工完成, _运行中 && 周期内 >= 7.0);
        _内存.不安全设置保持寄存器(寄存器D1250已锁附颗数,
            (ushort)Math.Clamp(_已锁附颗数, 0, short.MaxValue));

        // ── 6. 报警位模拟（M500 起）──────────────────────────────────────
        //    M500–M600 是留给上位机的报警区；位顺序与 config/alarms.json 的默认配置一致。
        //    上位机的报警扫描服务读这些位，再翻译成具体报警文本显示在报警栏。
        _内存.不安全设置线圈(报警位基址 + 0, _急停);                        // M500 急停
        _内存.不安全设置线圈(报警位基址 + 1, _运行中 && 周期内 is > 6.0 and < 7.5);   // M501 上料超时
        _内存.不安全设置线圈(报警位基址 + 2, _输出数量 % 20 >= 18);                  // M502 余料不足
        _内存.不安全设置线圈(报警位基址 + 3, false);                                    // M503 安全门（模拟中始终关好）

        // ── 6. 输出点模拟 ───────────────────────────────────────────────
        _内存.不安全设置线圈(Y线圈基址 + 0, _运行中 && 周期内 < 3.0);      // Y0 上料气缸
        _内存.不安全设置线圈(Y线圈基址 + 1, _运行中 && 周期内 is > 3.0 and < 6.0); // Y1 压装气缸
        _内存.不安全设置线圈(Y线圈基址 + 2, _运行中);                       // Y2 运行指示灯
        _内存.不安全设置线圈(Y线圈基址 + 8, _急停);              // Y10 蜂鸣器
    }

    // ── 32 位存取（CDAB：低字在前，与汇川默认一致）──────────────────────

    /// <summary>
    /// 按 32 位无符号写入两个寄存器。
    /// <para>字序规则统一走 <c>寄存器编解码器.编码</c>（R1）：模拟器与真实客户端
    /// 必须共用同一份字序实现，否则自测结论在真机上失效。</para>
    /// </summary>
    private void 设置UInt32(int 地址值, uint 值)
    {
        var 字 = 寄存器编解码器.编码(标签数据类型.UInt32, 值, 字顺序.小字节序);
        _内存.不安全设置保持寄存器(地址值, 字[0]);       // 低字
        _内存.不安全设置保持寄存器(地址值 + 1, 字[1]);   // 高字
    }

    /// <summary>
    /// 按 32 位浮点写入两个寄存器（CDAB 字序，低字在前）。
    /// <para>与 <c>RegisterCodec.Encode(Float32, …, LittleEndian)</c> 的规则一致，
    /// 且直接复用其实现而非重写。</para>
    /// </summary>
    private void 设置浮点(int 地址值, float 值)
    {
        var 字 = 寄存器编解码器.编码(标签数据类型.Float32, 值, 字顺序.小字节序);
        _内存.不安全设置保持寄存器(地址值, 字[0]);
        _内存.不安全设置保持寄存器(地址值 + 1, 字[1]);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await 异步停止().ConfigureAwait(false);
    }
}
