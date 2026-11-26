using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PlcHmi.Business.Configuration;
using PlcHmi.Business.Services;
using PlcHmi.Communication.Inovance;
using PlcHmi.Communication.Modbus;
using PlcHmi.Communication.OpcUa;
using PlcHmi.Communication.Recipe;
using PlcHmi.Communication.Simulation;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Business;

/// <summary>业务层与通信层的依赖注入装配。</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册通信层与业务层的全部服务。
    /// <para>所有服务均为单例——上位机只维持<b>一个</b> Modbus 连接
    /// （汇川 H5U 默认最多 8 个客户端连接，没有理由开多个）。</para>
    /// </summary>
    public static IServiceCollection AddPlcHmiService(
        this IServiceCollection serviceCollection,
        PlcConnectionOptions PlcOptions,
        PollingOptions PollingOptions,
        RecipeConfig RecipeConfig,
        VariableTableDocument? VariableTable = null,
        ScannerConfig? Scanner = null)
    {
        ArgumentNullException.ThrowIfNull(serviceCollection);
        ArgumentNullException.ThrowIfNull(PlcOptions);
        ArgumentNullException.ThrowIfNull(PollingOptions);
        ArgumentNullException.ThrowIfNull(RecipeConfig);

        serviceCollection.AddSingleton(PlcOptions);
        serviceCollection.AddSingleton(PollingOptions);
        serviceCollection.AddSingleton(RecipeConfig);

        // 扫码枪配置：调用方没传就按 appsettings.json 现读（老调用点不受影响）
        serviceCollection.AddSingleton(Scanner ?? AppSettingsLoader.Load(out _).Scanner);

        // ── 配置健康登记处 ──────────────────────────────────────────────
        // 各加载器把"读不出来 / 已从备份恢复"登记到这里，界面据此显示常驻横幅。
        // 降级可以是允许的（保证界面能打开），但**不允许是无声的** ——
        // 报警表损坏却安静地显示"无报警"，比直接崩溃更危险。
        serviceCollection.AddSingleton<IConfigHealth, ConfigHealthService>();

        // ── 通信层 ──────────────────────────────────────────────────────
        // 地址解析器按机型创建：小型 PLC 与中型 PLC 使用完全不同的地址体系
        serviceCollection.AddSingleton<IAddressParser>(_ => new InovanceAddressParser(PlcOptions.Series));

        // 变量表服务：轴参数/延时/气缸区的变量绑定数据源（未导入时为空表，行为与旧版一致）
        serviceCollection.AddSingleton<IVariableTableService>(sp =>
            new VariableTableService(VariableTable ?? new VariableTableDocument(),
                new InovanceAddressParser(PlcOptions.Series), sp.GetRequiredService<ILogger<VariableTableService>>()));

        // OPC UA 模式的节点推导器：与 Modbus 地址体系共用同一份基址表（反解），
        // 变量绑定的点位优先按变量名符号寻址
        if (PlcOptions.CommunicationProtocol == CommunicationProtocol.OPCUA)
        {
            serviceCollection.AddSingleton(sp => new NodeIdResolver(PlcOptions, new InovanceAddressParser(PlcOptions.Series),
                sp.GetRequiredService<IVariableTableService>()));
        }

        // 客户端实现按“协议 × 模拟器”四种组合装配；
        // 协议与模拟器均为 DI 定型参数，运行期修改无效，需重启程序
        if (PlcOptions.CommunicationProtocol == CommunicationProtocol.OPCUA)
        {
            // OPC UA：客户端实现由 DI 定型，沿用"保存 + 重启程序生效"的既有切换机制
            if (PlcOptions.UseSimulator)
            {
                serviceCollection.AddSingleton<VirtualPlcDevice>();
                serviceCollection.AddSingleton<IModbusClient, SimulationOpcUaClient>();
            }
            else
            {
                serviceCollection.AddSingleton<IModbusClient, OpcUaClient>();
            }
        }
        else
        {
            // Modbus：运行模式可在运行期切换，故装配"可切换代理 + 两套实现 + 离线调试从站"。
            // 连接管理器注入的是代理，切换只发生在运行模式管理器内部，其它服务无感知。
            serviceCollection.AddSingleton<VirtualPlcDevice>();
            serviceCollection.AddSingleton<FluentModbusTCPClient>();
            serviceCollection.AddSingleton<SimulationModbusClient>();
            serviceCollection.AddSingleton<VirtualPlcSlave>();

            serviceCollection.AddSingleton(sp => new SwitchableModbusClient(
                PlcOptions.UseSimulator
                    ? sp.GetRequiredService<SimulationModbusClient>()
                    : sp.GetRequiredService<FluentModbusTCPClient>()));

            // 接口解析到同一份代理实例（不能各自再 new 一个，否则连接管理器与实际被切换的对象不是同一个）
            serviceCollection.AddSingleton<IModbusClient>(sp => sp.GetRequiredService<SwitchableModbusClient>());

            // 运行模式管理器仅在 Modbus 模式注册；OPC UA 模式注入可空依赖 → 界面自动隐藏入口
            serviceCollection.AddSingleton<IRunModeManager, ModbusRunModeManager>();
        }

        // ── 业务层 ──────────────────────────────────────────────────────
        serviceCollection.AddSingleton<ITagRepository, TagRepository>();
        serviceCollection.AddSingleton<TagValueService>();
        serviceCollection.AddSingleton<ITagValueService>(SerialPort => SerialPort.GetRequiredService<TagValueService>());
        serviceCollection.AddSingleton<IIoPointService, IoPointService>();

        // IO 通信诊断：X/Y 段 × FC01/FC02 的原始读数并排，用于定位"显示与物理对不上"
        serviceCollection.AddSingleton<IIoDiagnosticService, IoDiagnosticService>();
        serviceCollection.AddSingleton<TagWriter>();
        serviceCollection.AddSingleton<IConnectManager, ConnectionManager>();
        serviceCollection.AddSingleton<IPollingEngine, PollingEngine>();
        serviceCollection.AddSingleton<IDeviceService, DeviceService>();

        // 报警历史落盘：跨重启保留，用于交接班追溯（硬件 HMI 面板本来就会存）。
        // 注册为单例 → 宿主释放时会 Dispose，退出前把待写内容刷盘。
        serviceCollection.AddSingleton<AlarmHistoryStore>();
        serviceCollection.AddSingleton<IAlarmService, AlarmService>();
        serviceCollection.AddSingleton<IManualWidgetService, ManualWidgetService>();

        // 手动使能保持：周期把使能状态写进 M666（打开持续置位 / 关闭持续复位），
        // 只在开关切换时写一次会在 PLC 自行复位该位后与界面状态分叉
        serviceCollection.AddHostedService<ManualEnableHoldService>();

        // ── 产量统计（运行页图表：今日 / 昨日每小时、近 7 天每日） ──────
        // 产量来源委托到设备服务的累计计数器；随宿主启动/停止采样循环
        serviceCollection.AddSingleton(sp => new OutputStatisticsService(
            sp.GetRequiredService<ILogger<OutputStatisticsService>>(),
            sp.GetRequiredService<IConfigHealth>(),
            () => sp.GetRequiredService<IDeviceService>().OutputCount));
        serviceCollection.AddSingleton<IOutputStatistics>(SerialPort => SerialPort.GetRequiredService<OutputStatisticsService>());
        serviceCollection.AddHostedService(SerialPort => SerialPort.GetRequiredService<OutputStatisticsService>());

        // ── 手动页元件（配置驱动）与权限 ────────────────────────────────
        serviceCollection.AddSingleton<IManualComponentService, ManualComponentService>();
        serviceCollection.AddSingleton<IUserService, UserService>();

        // 服务层权限守卫：界面隐藏按钮只是防误触，真正的边界在这里（审查标准 R5）。
        // 单元测试若不需要验证权限分支，请在 AddPlcHmiServices 之后显式覆盖为
        // AllowAllPermissionGuard —— 让"本用例不覆盖权限"成为一行看得见的声明。
        serviceCollection.AddSingleton<IPermissionGuard, UserPermissionGuard>();

        // ── 报警定义（PLC 地址 → 报警文本）与扫描 ───────────────────────
        serviceCollection.AddSingleton<IAlarmDefinitionService, AlarmDefinitionService>();
        serviceCollection.AddHostedService<AlarmScanService>();

        // ── 历史记录（触发位上升沿 → 读取数据体 → 按日落盘，可导 CSV） ──
        // 存储与采集服务都必须是单例：界面读的与采集写的必须是同一份内存真相
        serviceCollection.AddSingleton<HistoryRecordStore>();
        serviceCollection.AddSingleton<HistoryRecordService>();
        serviceCollection.AddSingleton<IHistoryRecordService>(SerialPort => SerialPort.GetRequiredService<HistoryRecordService>());
        serviceCollection.AddHostedService(SerialPort => SerialPort.GetRequiredService<HistoryRecordService>());

        // ── 扫码枪（串口 / TCP；手动型写成功信号，触发型应答触发寄存器） ──
        // 工单号只存内存（不跨重启）；未启用时 StartAsync 什么都不做
        serviceCollection.AddSingleton<ScannerService>();
        serviceCollection.AddSingleton<IScannerService>(SerialPort => SerialPort.GetRequiredService<ScannerService>());
        serviceCollection.AddHostedService(SerialPort => SerialPort.GetRequiredService<ScannerService>());

        // ── 参数页自定义表格（延时 / 按钮） ──────────────────────────────
        serviceCollection.AddSingleton<IParameterTableService, ParameterTableService>();

        // ── 轴地址集中管理（定位速度 / 位置 / 点动 / 档位） ──────────────
        serviceCollection.AddSingleton<IAxisAddressService, AxisAddressService>();

        // ── 运行监控页元件地址（状态 / 节拍 / 产量 / 命令按钮，仅管理员） ──
        serviceCollection.AddSingleton<IRunMonitorAddressService, RunMonitorAddressService>();

        // ── PLC 连接参数管理（系统设置页，仅管理员） ─────────────────────
        serviceCollection.AddSingleton<IPlcConnectionSettingsService, PlcConnectionSettingsService>();

        // ── 机种（配方）+ 全局参数 ──────────────────────────────────────
        // 两个 Store 都必须是单例：各自共用一个文件的同一份内存真相。
        // 机种 = D2500–D3999 区段快照；全局参数 = 区段之外的 D 区数据（不随机种变动）。
        serviceCollection.AddSingleton<RecipeStore>();
        serviceCollection.AddSingleton<ParameterStore>();
        serviceCollection.AddSingleton<IRecipeService, RecipeService>();
        serviceCollection.AddSingleton<PopupMonitorService>();
        serviceCollection.AddSingleton<IPageContextService, PageContextService>();

        // ── 点胶锁付机模板：通用点表 / 辅助点 / 标定 ────────────────────
        // 锁附示教与点胶示教共用同一套点表服务，仅配置不同；
        // 标定服务复用手动控件服务的脉冲线圈（M 区 + 使能联锁）。
        serviceCollection.AddSingleton<IPointTableService, PointTableService>();
        serviceCollection.AddSingleton<IAuxPointService, AuxPointService>();
        serviceCollection.AddSingleton<ICalibrationService, CalibrationService>();
        serviceCollection.AddSingleton<IPageAddressService, PageAddressService>();

        // 随宿主启动/停止通信与采集
        serviceCollection.AddHostedService<PlcRuntimeHostingService>();

        return serviceCollection;
    }
}

/// <summary>
/// 运行时宿主服务。宿主启动时拉起连接管理与轮询引擎，退出时优雅停止。
/// <para>连接建立后会做一次<b>机种/示教初始化 + 一致性校验</b>——校验结果只提示、不自动覆盖。</para>
/// </summary>
public sealed class PlcRuntimeHostingService : IHostedService
{
    /// <summary>等待首轮采集把点位值填上的时间。</summary>
    private static readonly TimeSpan preHotDelay = TimeSpan.FromMilliseconds(600);

    private readonly IConnectManager _connect;
    private readonly IPollingEngine _polling;
    private readonly IRecipeService _recipe;
    private readonly IPointTableService _pointTable;
    private readonly IAuxPointService _auxPoint;
    private readonly IUserService _userItem;
    private readonly IManualComponentService _manualComponent;
    private readonly IAlarmDefinitionService _alarmDefinition;
    private readonly IParameterTableService _parameterTable;
    private readonly ILogger<PlcRuntimeHostingService> _log;

    public PlcRuntimeHostingService(
        IConnectManager Connect,
        IPollingEngine Polling,
        IRecipeService recipeItem,
        IPointTableService PointTable,
        IAuxPointService AuxPoint,
        IUserService User,
        IManualComponentService ManualComponent,
        IAlarmDefinitionService AlarmDefinition,
        IParameterTableService ParameterTable,
        ILogger<PlcRuntimeHostingService> Log)
    {
        _connect = Connect;
        _polling = Polling;
        _recipe = recipeItem;
        _pointTable = PointTable;
        _auxPoint = AuxPoint;
        _userItem = User;
        _manualComponent = ManualComponent;
        _alarmDefinition = AlarmDefinition;
        _parameterTable = ParameterTable;
        _log = Log;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _log.LogInformation("启动 PLC 运行时……");

        // 账号与手动页元件不依赖 PLC，先加载（登录对话框要用）
        try
        {
            await _userItem.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await _manualComponent.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await _alarmDefinition.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await _parameterTable.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await _pointTable.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await _auxPoint.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "账号 / 手动页元件初始化失败");
        }

        // 先起轮询（未连接时它自身会空转等待），再起连接
        await _polling.StartAsync(cancellationToken).ConfigureAwait(false);

        // 连接真正建立后再补一次全局数据同步（首次上电 / 网线刚插上时启动同步会被跳过）
        _connect.StateChanged += ConnectionStateChanged;

        await _connect.StartAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // 等首轮采集出数，否则读到的机种号全是空的
            await Task.Delay(preHotDelay, cancellationToken).ConfigureAwait(false);

            await _recipe.InitializeAsync(cancellationToken).ConfigureAwait(false);

            // 配方初始化读的是 PLC 实际机种号；点表在它之前已按默认号（1）装载过一遍，
            // 若 PLC 实际机种是 5，开机首屏点表显示的就是错机种的数据。
            // 这里按正确机种整表重载（纯内存填充，成本可忽略）。
            foreach (var TableEntry in _pointTable.AllPointTable)
            {
                await _pointTable.LoadPointTableAsync(TableEntry.Key, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 启动过程中被取消，属于正常退出路径
        }
        catch (Exception exception)
        {
            // 初始化失败不能挡住界面 —— 操作员至少要能进去看日志
            _log.LogWarning(exception, "机种初始化失败，界面仍可正常打开");
        }

        _log.LogInformation("PLC 运行时已启动");
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _log.LogInformation("停止 PLC 运行时……");

        _connect.StateChanged -= ConnectionStateChanged;

        await _connect.StopAsync(cancellationToken).ConfigureAwait(false);
        await _polling.StopAsync(cancellationToken).ConfigureAwait(false);

        _log.LogInformation("PLC 运行时已停止");
    }

    /// <summary>
    /// 连接建立后补做一次全局数据同步（<b>只做开机后的首次</b>，后续重连由轮询与页面进入刷新）。
    ///
    /// <para><b>为什么需要：</b>现场口径是"机种区（D2500–D3999）之外的数据每次开启软件后
    /// 从 PLC 读取刷新即可"。而启动那一瞬间 PLC 未必已连上（首次上电、网线刚插上），
    /// 此时 <see cref="IRecipeService.InitializeAsync"/> 的同步会被跳过；等连接真正建立时再补一次，
    /// 免得界面一直显示 <c>parameters.json</c> 里的旧缓存值。</para>
    ///
    /// <para><b>为什么不用 try/catch：</b>连接事件在通信线程上触发，异常绝不能抛回去；
    /// 这里用 <see cref="Task.ContinueWith(Action{Task}, TaskContinuationOptions)"/>
    /// 的失败续延记账，既不阻塞也不吞掉调用方。</para>
    /// </summary>
    private void ConnectionStateChanged(object? sender, ConnectionState State)
    {
        if (State is not ConnectionState.Connected)
        {
            return;
        }

        // 0/1：重连抖动时只补一次
        if (Interlocked.Exchange(ref _filledSync, 1) == 1)
        {
            return;
        }

        _ = _recipe
            .InitializeAsync(CancellationToken.None)
            .ContinueWith(
                task => _log.LogWarning(
                    task.Exception, "连接建立后的全局数据同步失败（界面仍可正常使用）"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
    }

    private int _filledSync;
}
