using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using 上位机.业务.配置;
using 上位机.业务.服务;
using 上位机.通信.汇川;
using 上位机.通信.Modbus;
using 上位机.通信.OPCUA;
using 上位机.通信.配方;
using 上位机.通信.仿真;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.业务;

/// <summary>业务层与通信层的依赖注入装配。</summary>
public static class 服务集合扩展
{
    /// <summary>
    /// 注册通信层与业务层的全部服务。
    /// <para>所有服务均为单例——上位机只维持<b>一个</b> Modbus 连接
    /// （汇川 H5U 默认最多 8 个客户端连接，没有理由开多个）。</para>
    /// </summary>
    public static IServiceCollection 添加上位机服务(
        this IServiceCollection 服务集合,
        PLC连接选项 PLC选项,
        轮询选项 轮询选项,
        配方配置 配方配置,
        变量表文档? 变量表 = null,
        扫码枪配置? 扫码枪 = null)
    {
        ArgumentNullException.ThrowIfNull(服务集合);
        ArgumentNullException.ThrowIfNull(PLC选项);
        ArgumentNullException.ThrowIfNull(轮询选项);
        ArgumentNullException.ThrowIfNull(配方配置);

        服务集合.AddSingleton(PLC选项);
        服务集合.AddSingleton(轮询选项);
        服务集合.AddSingleton(配方配置);

        // 扫码枪配置：调用方没传就按 appsettings.json 现读（老调用点不受影响）
        服务集合.AddSingleton(扫码枪 ?? 应用设置加载器.加载(out _).扫码枪);

        // ── 配置健康登记处 ──────────────────────────────────────────────
        // 各加载器把"读不出来 / 已从备份恢复"登记到这里，界面据此显示常驻横幅。
        // 降级可以是允许的（保证界面能打开），但**不允许是无声的** ——
        // 报警表损坏却安静地显示"无报警"，比直接崩溃更危险。
        服务集合.AddSingleton<配置健康接口, 配置健康服务>();

        // ── 通信层 ──────────────────────────────────────────────────────
        // 地址解析器按机型创建：小型 PLC 与中型 PLC 使用完全不同的地址体系
        服务集合.AddSingleton<地址解析器接口>(_ => new 汇川地址解析器(PLC选项.系列));

        // 变量表服务：轴参数/延时/气缸区的变量绑定数据源（未导入时为空表，行为与旧版一致）
        服务集合.AddSingleton<变量表服务接口>(sp =>
            new 变量表服务(变量表 ?? new 变量表文档(),
                new 汇川地址解析器(PLC选项.系列), sp.GetRequiredService<ILogger<变量表服务>>()));

        // OPC UA 模式的节点推导器：与 Modbus 地址体系共用同一份基址表（反解），
        // 变量绑定的点位优先按变量名符号寻址
        if (PLC选项.通讯协议 == 通讯协议.OPCUA)
        {
            服务集合.AddSingleton(sp => new NodeId推导器(PLC选项, new 汇川地址解析器(PLC选项.系列),
                sp.GetRequiredService<变量表服务接口>()));
        }

        // 客户端实现按“协议 × 模拟器”四种组合装配；
        // 协议与模拟器均为 DI 定型参数，运行期修改无效，需重启程序
        if (PLC选项.通讯协议 == 通讯协议.OPCUA)
        {
            // OPC UA：客户端实现由 DI 定型，沿用"保存 + 重启程序生效"的既有切换机制
            if (PLC选项.使用模拟器)
            {
                服务集合.AddSingleton<虚拟PLC设备>();
                服务集合.AddSingleton<Modbus客户端接口, 仿真OPCUA客户端>();
            }
            else
            {
                服务集合.AddSingleton<Modbus客户端接口, OPCUA客户端>();
            }
        }
        else
        {
            // Modbus：运行模式可在运行期切换，故装配"可切换代理 + 两套实现 + 离线调试从站"。
            // 连接管理器注入的是代理，切换只发生在运行模式管理器内部，其它服务无感知。
            服务集合.AddSingleton<虚拟PLC设备>();
            服务集合.AddSingleton<FluentModbusTCP客户端>();
            服务集合.AddSingleton<仿真Modbus客户端>();
            服务集合.AddSingleton<虚拟PLC从站>();

            服务集合.AddSingleton(sp => new 可切换Modbus客户端(
                PLC选项.使用模拟器
                    ? sp.GetRequiredService<仿真Modbus客户端>()
                    : sp.GetRequiredService<FluentModbusTCP客户端>()));

            // 接口解析到同一份代理实例（不能各自再 new 一个，否则连接管理器与实际被切换的对象不是同一个）
            服务集合.AddSingleton<Modbus客户端接口>(sp => sp.GetRequiredService<可切换Modbus客户端>());

            // 运行模式管理器仅在 Modbus 模式注册；OPC UA 模式注入可空依赖 → 界面自动隐藏入口
            服务集合.AddSingleton<运行模式管理器接口, Modbus运行模式管理器>();
        }

        // ── 业务层 ──────────────────────────────────────────────────────
        服务集合.AddSingleton<标签仓储接口, 标签仓储>();
        服务集合.AddSingleton<标签值服务>();
        服务集合.AddSingleton<标签值服务接口>(串口 => 串口.GetRequiredService<标签值服务>());
        服务集合.AddSingleton<IO点位服务接口, IO点位服务>();

        // IO 通信诊断：X/Y 段 × FC01/FC02 的原始读数并排，用于定位"显示与物理对不上"
        服务集合.AddSingleton<IO诊断服务接口, IO诊断服务>();
        服务集合.AddSingleton<标签写入器>();
        服务集合.AddSingleton<连接管理器接口, 连接管理器>();
        服务集合.AddSingleton<轮询引擎接口, 轮询引擎>();
        服务集合.AddSingleton<设备服务接口, 设备服务>();

        // 报警历史落盘：跨重启保留，用于交接班追溯（硬件 HMI 面板本来就会存）。
        // 注册为单例 → 宿主释放时会 Dispose，退出前把待写内容刷盘。
        服务集合.AddSingleton<报警历史存储>();
        服务集合.AddSingleton<报警服务接口, 报警服务>();
        服务集合.AddSingleton<手动控件服务接口, 手动控件服务>();

        // 手动使能保持：周期把使能状态写进 M666（打开持续置位 / 关闭持续复位），
        // 只在开关切换时写一次会在 PLC 自行复位该位后与界面状态分叉
        服务集合.AddHostedService<手动使能保持服务>();

        // ── 产量统计（运行页图表：今日 / 昨日每小时、近 7 天每日） ──────
        // 产量来源委托到设备服务的累计计数器；随宿主启动/停止采样循环
        服务集合.AddSingleton(sp => new 产量统计服务(
            sp.GetRequiredService<ILogger<产量统计服务>>(),
            sp.GetRequiredService<配置健康接口>(),
            () => sp.GetRequiredService<设备服务接口>().输出数量));
        服务集合.AddSingleton<产量统计接口>(串口 => 串口.GetRequiredService<产量统计服务>());
        服务集合.AddHostedService(串口 => 串口.GetRequiredService<产量统计服务>());

        // ── 手动页元件（配置驱动）与权限 ────────────────────────────────
        服务集合.AddSingleton<手动元件服务接口, 手动元件服务>();
        服务集合.AddSingleton<用户服务接口, 用户服务>();

        // 服务层权限守卫：界面隐藏按钮只是防误触，真正的边界在这里（审查标准 R5）。
        // 单元测试若不需要验证权限分支，请在 AddPlcHmiServices 之后显式覆盖为
        // AllowAllPermissionGuard —— 让"本用例不覆盖权限"成为一行看得见的声明。
        服务集合.AddSingleton<权限守卫接口, 用户权限守卫>();

        // ── 报警定义（PLC 地址 → 报警文本）与扫描 ───────────────────────
        服务集合.AddSingleton<报警定义服务接口, 报警定义服务>();
        服务集合.AddHostedService<报警扫描服务>();

        // ── 历史记录（触发位上升沿 → 读取数据体 → 按日落盘，可导 CSV） ──
        // 存储与采集服务都必须是单例：界面读的与采集写的必须是同一份内存真相
        服务集合.AddSingleton<历史记录存储>();
        服务集合.AddSingleton<历史记录服务>();
        服务集合.AddSingleton<历史记录服务接口>(串口 => 串口.GetRequiredService<历史记录服务>());
        服务集合.AddHostedService(串口 => 串口.GetRequiredService<历史记录服务>());

        // ── 扫码枪（串口 / TCP；手动型写成功信号，触发型应答触发寄存器） ──
        // 工单号只存内存（不跨重启）；未启用时 StartAsync 什么都不做
        服务集合.AddSingleton<扫码枪服务>();
        服务集合.AddSingleton<扫码枪服务接口>(串口 => 串口.GetRequiredService<扫码枪服务>());
        服务集合.AddHostedService(串口 => 串口.GetRequiredService<扫码枪服务>());

        // ── 参数页自定义表格（延时 / 按钮） ──────────────────────────────
        服务集合.AddSingleton<参数表格服务接口, 参数表格服务>();

        // ── 轴地址集中管理（定位速度 / 位置 / 点动 / 档位） ──────────────
        服务集合.AddSingleton<轴地址服务接口, 轴地址服务>();

        // ── 运行监控页元件地址（状态 / 节拍 / 产量 / 命令按钮，仅管理员） ──
        服务集合.AddSingleton<运行监控地址服务接口, 运行监控地址服务>();

        // ── PLC 连接参数管理（系统设置页，仅管理员） ─────────────────────
        服务集合.AddSingleton<PLC连接设置服务接口, PLC连接设置服务>();

        // ── 机种（配方）+ 全局参数 ──────────────────────────────────────
        // 两个 Store 都必须是单例：各自共用一个文件的同一份内存真相。
        // 机种 = D2500–D3999 区段快照；全局参数 = 区段之外的 D 区数据（不随机种变动）。
        服务集合.AddSingleton<配方存储>();
        服务集合.AddSingleton<参数存储>();
        服务集合.AddSingleton<配方服务接口, 配方服务>();
        服务集合.AddSingleton<弹窗监控服务>();
        服务集合.AddSingleton<页面上下文服务接口, 页面上下文服务>();

        // ── 点胶锁付机模板：通用点表 / 辅助点 / 标定 ────────────────────
        // 锁附示教与点胶示教共用同一套点表服务，仅配置不同；
        // 标定服务复用手动控件服务的脉冲线圈（M 区 + 使能联锁）。
        服务集合.AddSingleton<点表服务接口, 点表服务>();
        服务集合.AddSingleton<辅助点服务接口, 辅助点服务>();
        服务集合.AddSingleton<标定服务接口, 标定服务>();
        服务集合.AddSingleton<页面地址服务接口, 页面地址服务>();

        // 随宿主启动/停止通信与采集
        服务集合.AddHostedService<PLC运行期托管服务>();

        return 服务集合;
    }
}

/// <summary>
/// 运行时宿主服务。宿主启动时拉起连接管理与轮询引擎，退出时优雅停止。
/// <para>连接建立后会做一次<b>机种/示教初始化 + 一致性校验</b>——校验结果只提示、不自动覆盖。</para>
/// </summary>
public sealed class PLC运行期托管服务 : IHostedService
{
    /// <summary>等待首轮采集把点位值填上的时间。</summary>
    private static readonly TimeSpan 预热延迟 = TimeSpan.FromMilliseconds(600);

    private readonly 连接管理器接口 _连接;
    private readonly 轮询引擎接口 _轮询;
    private readonly 配方服务接口 _配方;
    private readonly 点表服务接口 _点表;
    private readonly 辅助点服务接口 _辅助点;
    private readonly 用户服务接口 _用户项;
    private readonly 手动元件服务接口 _手动元件;
    private readonly 报警定义服务接口 _报警定义;
    private readonly 参数表格服务接口 _参数表格;
    private readonly ILogger<PLC运行期托管服务> _日志;

    public PLC运行期托管服务(
        连接管理器接口 连接,
        轮询引擎接口 轮询,
        配方服务接口 配方项,
        点表服务接口 点表,
        辅助点服务接口 辅助点,
        用户服务接口 用户,
        手动元件服务接口 手动元件,
        报警定义服务接口 报警定义,
        参数表格服务接口 参数表格,
        ILogger<PLC运行期托管服务> 日志)
    {
        _连接 = 连接;
        _轮询 = 轮询;
        _配方 = 配方项;
        _点表 = 点表;
        _辅助点 = 辅助点;
        _用户项 = 用户;
        _手动元件 = 手动元件;
        _报警定义 = 报警定义;
        _参数表格 = 参数表格;
        _日志 = 日志;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken 取消令牌)
    {
        _日志.LogInformation("启动 PLC 运行时……");

        // 账号与手动页元件不依赖 PLC，先加载（登录对话框要用）
        try
        {
            await _用户项.异步初始化(取消令牌).ConfigureAwait(false);
            await _手动元件.异步初始化(取消令牌).ConfigureAwait(false);
            await _报警定义.异步初始化(取消令牌).ConfigureAwait(false);
            await _参数表格.异步初始化(取消令牌).ConfigureAwait(false);
            await _点表.异步初始化(取消令牌).ConfigureAwait(false);
            await _辅助点.异步初始化(取消令牌).ConfigureAwait(false);
        }
        catch (Exception 异常)
        {
            _日志.LogError(异常, "账号 / 手动页元件初始化失败");
        }

        // 先起轮询（未连接时它自身会空转等待），再起连接
        await _轮询.异步启动(取消令牌).ConfigureAwait(false);

        // 连接真正建立后再补一次全局数据同步（首次上电 / 网线刚插上时启动同步会被跳过）
        _连接.状态已变更 += 连接状态已变更;

        await _连接.异步启动(取消令牌).ConfigureAwait(false);

        try
        {
            // 等首轮采集出数，否则读到的机种号全是空的
            await Task.Delay(预热延迟, 取消令牌).ConfigureAwait(false);

            await _配方.异步初始化(取消令牌).ConfigureAwait(false);

            // 配方初始化读的是 PLC 实际机种号；点表在它之前已按默认号（1）装载过一遍，
            // 若 PLC 实际机种是 5，开机首屏点表显示的就是错机种的数据。
            // 这里按正确机种整表重载（纯内存填充，成本可忽略）。
            foreach (var 表项 in _点表.全部点表)
            {
                await _点表.异步加载点表(表项.键, 取消令牌).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 启动过程中被取消，属于正常退出路径
        }
        catch (Exception 异常)
        {
            // 初始化失败不能挡住界面 —— 操作员至少要能进去看日志
            _日志.LogWarning(异常, "机种初始化失败，界面仍可正常打开");
        }

        _日志.LogInformation("PLC 运行时已启动");
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken 取消令牌)
    {
        _日志.LogInformation("停止 PLC 运行时……");

        _连接.状态已变更 -= 连接状态已变更;

        await _连接.异步停止(取消令牌).ConfigureAwait(false);
        await _轮询.异步停止(取消令牌).ConfigureAwait(false);

        _日志.LogInformation("PLC 运行时已停止");
    }

    /// <summary>
    /// 连接建立后补做一次全局数据同步（<b>只做开机后的首次</b>，后续重连由轮询与页面进入刷新）。
    ///
    /// <para><b>为什么需要：</b>现场口径是"机种区（D2500–D3999）之外的数据每次开启软件后
    /// 从 PLC 读取刷新即可"。而启动那一瞬间 PLC 未必已连上（首次上电、网线刚插上），
    /// 此时 <see cref="配方服务接口.异步初始化"/> 的同步会被跳过；等连接真正建立时再补一次，
    /// 免得界面一直显示 <c>parameters.json</c> 里的旧缓存值。</para>
    ///
    /// <para><b>为什么不用 try/catch：</b>连接事件在通信线程上触发，异常绝不能抛回去；
    /// 这里用 <see cref="Task.ContinueWith(Action{Task}, TaskContinuationOptions)"/>
    /// 的失败续延记账，既不阻塞也不吞掉调用方。</para>
    /// </summary>
    private void 连接状态已变更(object? 发送者, 连接状态 状态)
    {
        if (状态 is not 连接状态.已连接)
        {
            return;
        }

        // 0/1：重连抖动时只补一次
        if (Interlocked.Exchange(ref _已补同步, 1) == 1)
        {
            return;
        }

        _ = _配方
            .异步初始化(CancellationToken.None)
            .ContinueWith(
                任务 => _日志.LogWarning(
                    任务.Exception, "连接建立后的全局数据同步失败（界面仍可正常使用）"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
    }

    private int _已补同步;
}
