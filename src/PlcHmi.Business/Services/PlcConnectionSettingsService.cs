using System.Text.Json;
using Microsoft.Extensions.Logging;
using 上位机.业务.配置;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// PLC 连接参数管理（系统设置页，仅管理员）。
///
/// <para><b>生效机制</b>：通信层客户端（<c>FluentModbusTcpClient</c>）每次连接时都从共享的
/// <see cref="PLC连接选项"/> 单例现读 IP / 端口，连接管理器 <c>StopAsync</c> 会断开
/// 底层 TCP。因此"保存"= 更新共享参数 → 原子落盘 → 重启连接管理器，新地址立即生效，无需重启程序。</para>
///
/// <para><b>为什么 Series / UseSimulator 不开放编辑</b>：前者决定地址解析体系（换机型属 L3 高风险），
/// 后者由 DI 装配定型（运行期改了无效）——把"改了不起作用"的入口挡在模型之外。</para>
/// </summary>
public sealed class PLC连接设置服务 : PLC连接设置服务接口
{
    /// <summary>Modbus TCP 标准端口。</summary>
    private const int 端口最小 = 1;

    private const int 端口最大 = 65535;

    /// <summary>Modbus 从站号合法范围（0 是广播，TCP 主站常规寻址不用）。</summary>
    private const int 单元标识最小 = 1;

    private const int 单元标识最大 = 247;

    /// <summary>低于 100ms 的超时在任何真实网络上都不现实，只会在 PLC 忙时误判断线。</summary>
    private const int 超时最小毫秒 = 100;

    /// <summary>心跳过密会平白占用 PLC 连接处理能力（H5U 默认最多 8 客户端）。</summary>
    private const int 心跳最小毫秒 = 500;

    /// <summary>退避小于 100ms 断线风暴时会打挂 PLC。</summary>
    private const int 重连最小毫秒 = 100;

    /// <summary>汇川 PLC 单次读寄存器上限 123（本程序文档建议保守取 100）。</summary>
    private const int 最大寄存器下限 = 1;

    private const int 最大寄存器上限 = 123;

    /// <summary>OPC UA 命名空间索引合法范围。ns=0 是标准命名空间保留段，现场从 1 起用。</summary>
    private const int 命名空间最小 = 1;

    private const int 命名空间最大 = 65535;

    private readonly PLC连接选项 _选项;
    private readonly 连接管理器接口 _连接;
    private readonly ILogger<PLC连接设置服务> _日志;

    /// <summary>改连接参数会断开重连，属管理员专有。</summary>
    private readonly 权限守卫接口 _权限;

    /// <inheritdoc />
    public event EventHandler? 已保存;

    public PLC连接设置服务(
        PLC连接选项 选项,
        连接管理器接口 连接,
        权限守卫接口 权限,
        ILogger<PLC连接设置服务> 日志)
    {
        _选项 = 选项;
        _连接 = 连接;
        _权限 = 权限;
        _日志 = 日志;
    }

    /// <inheritdoc />
    public PLC连接编辑模型 创建编辑模型() => new()
    {
        主机 = _选项.主机,
        端口 = _选项.端口,
        单元标识 = _选项.单元标识,
        连接超时毫秒 = _选项.连接超时毫秒,
        读取超时毫秒 = _选项.读取超时毫秒,
        心跳间隔毫秒 = _选项.心跳间隔毫秒,
        重连初始延迟毫秒 = _选项.重连初始延迟毫秒,
        重连最大延迟毫秒 = _选项.重连最大延迟毫秒,
        每次请求最大寄存器数 = _选项.每次请求最大寄存器数,
        自动重连 = _选项.自动重连,
        通讯协议 = _选项.通讯协议,
        OPCUA端口 = _选项.OPCUA端口,
        OPCUA命名空间索引 = _选项.OPCUA命名空间索引,
        模拟从站端口 = _选项.模拟从站端口,
        模拟从站监听地址 = _选项.模拟从站监听地址,
    };

    /// <inheritdoc />
    public IReadOnlyList<string> 校验(PLC连接编辑模型 候选)
    {
        ArgumentNullException.ThrowIfNull(候选);

        var 错误列表 = new List<string>();
        var 主机 = 候选.主机.Trim();

        if (主机.Length == 0)
        {
            错误列表.Add("PLC 地址不能为空。");
        }
        else if (Uri.CheckHostName(主机) == UriHostNameType.Unknown)
        {
            错误列表.Add($"PLC 地址「{主机}」不是合法的 IP 地址或主机名。");
        }

        if (候选.端口 is < 端口最小 or > 端口最大)
        {
            错误列表.Add($"端口必须在 {端口最小}–{端口最大} 之间（标准 Modbus TCP 为 502），当前 {候选.端口}。");
        }

        if (候选.单元标识 is < 单元标识最小 or > 单元标识最大)
        {
            错误列表.Add($"从站号必须在 {单元标识最小}–{单元标识最大} 之间，当前 {候选.单元标识}。");
        }

        if (候选.连接超时毫秒 < 超时最小毫秒)
        {
            错误列表.Add($"连接超时不能小于 {超时最小毫秒}ms，当前 {候选.连接超时毫秒}ms。");
        }

        if (候选.读取超时毫秒 < 超时最小毫秒)
        {
            错误列表.Add($"读写超时不能小于 {超时最小毫秒}ms，当前 {候选.读取超时毫秒}ms。");
        }

        if (候选.心跳间隔毫秒 < 心跳最小毫秒)
        {
            错误列表.Add($"心跳间隔不能小于 {心跳最小毫秒}ms（过密会占用 PLC 连接处理能力），当前 {候选.心跳间隔毫秒}ms。");
        }

        if (候选.重连初始延迟毫秒 < 重连最小毫秒)
        {
            错误列表.Add($"重连初始退避不能小于 {重连最小毫秒}ms，当前 {候选.重连初始延迟毫秒}ms。");
        }

        if (候选.重连最大延迟毫秒 < 候选.重连初始延迟毫秒)
        {
            错误列表.Add($"重连退避上限（{候选.重连最大延迟毫秒}ms）不能小于初始退避（{候选.重连初始延迟毫秒}ms）。");
        }

        if (候选.每次请求最大寄存器数 is < 最大寄存器下限 or > 最大寄存器上限)
        {
            错误列表.Add($"单次最大寄存器数必须在 {最大寄存器下限}–{最大寄存器上限} 之间（汇川读上限 123），当前 {候选.每次请求最大寄存器数}。");
        }

        if (候选.OPCUA端口 is < 端口最小 or > 端口最大)
        {
            错误列表.Add($"OPC UA 端口必须在 {端口最小}–{端口最大} 之间（汇川内置服务端默认 4840），当前 {候选.OPCUA端口}。");
        }

        if (候选.OPCUA命名空间索引 is < 命名空间最小 or > 命名空间最大)
        {
            错误列表.Add($"OPC UA 命名空间索引必须在 {命名空间最小}–{命名空间最大} 之间（默认 2），当前 {候选.OPCUA命名空间索引}。");
        }

        // 模拟从站参数只在 Modbus 模式的模拟运行期生效；仍然校验格式，
        // 避免管理员写下一个切到模拟模式时才暴露的坏端口 / 坏地址。
        if (候选.模拟从站端口 is < 端口最小 or > 端口最大)
        {
            错误列表.Add($"模拟从站端口必须在 {端口最小}–{端口最大} 之间（默认 5020，避开真实 PLC 的 502），当前 {候选.模拟从站端口}。");
        }

        var 模拟监听地址 = 候选.模拟从站监听地址.Trim();

        if (模拟监听地址.Length > 0 && !System.Net.IPAddress.TryParse(模拟监听地址, out _))
        {
            错误列表.Add($"模拟从站监听地址「{模拟监听地址}」不是合法的 IP 地址（本机调试填 127.0.0.1，跨机调试填 0.0.0.0 或本机网卡地址）。");
        }

        return 错误列表;
    }

    /// <inheritdoc />
    public async Task 异步保存(PLC连接编辑模型 候选, CancellationToken 取消令牌 = default)
    {
        ArgumentNullException.ThrowIfNull(候选);

        _权限.要求(权限.编辑PLC连接);

        var 错误列表 = 校验(候选);
        if (错误列表.Count > 0)
        {
            throw new InvalidOperationException(string.Join("\n", 错误列表));
        }

        var 主机 = 候选.主机.Trim();

        // 先落盘、后改内存：写入是原子的（AtomicFile），任一步失败内存与文件都保持旧值一致。
        // Series / 通讯协议 / 使用模拟器 以内存运行态为准写回，避免"文件缺失回退默认值"覆盖掉真实运行模式。
        // OPC UA 专属参数（端口/命名空间/节点格式/账号）同为 DI 定型参数，原样带过以防落盘丢失。
        //
        // 使用模拟器的落盘值走 运行模式策略：Modbus 模式下恒写 false —— 运行模式是运行期状态，
        // 不该被"保存连接参数"这个动作顺手记进配置文件（否则下次启动就记住了上次的模拟选择）。
        var 快照 = new PLC连接选项
        {
            主机 = 主机,
            端口 = 候选.端口,
            // 运行参数里 UnitId 是 byte（Modbus 从站号 1–247 恰在其范围内），校验已保证可安全收窄
            单元标识 = (byte)候选.单元标识,
            系列 = _选项.系列,
            使用模拟器 = 运行模式策略.持久化模拟开关(候选.通讯协议, _选项.使用模拟器),
            通讯协议 = 候选.通讯协议,
            OPCUA端口 = 候选.OPCUA端口,
            OPCUA命名空间索引 = 候选.OPCUA命名空间索引,
            OPCUA节点格式 = _选项.OPCUA节点格式,
            OPCUA用户名 = _选项.OPCUA用户名,
            OPCUA密码 = _选项.OPCUA密码,
            模拟从站端口 = 候选.模拟从站端口,
            模拟从站监听地址 = 候选.模拟从站监听地址.Trim(),
            连接超时毫秒 = 候选.连接超时毫秒,
            读取超时毫秒 = 候选.读取超时毫秒,
            心跳间隔毫秒 = 候选.心跳间隔毫秒,
            重连初始延迟毫秒 = 候选.重连初始延迟毫秒,
            重连最大延迟毫秒 = 候选.重连最大延迟毫秒,
            每次请求最大寄存器数 = 候选.每次请求最大寄存器数,
            自动重连 = 候选.自动重连,
        };

        await 异步落盘(快照, 取消令牌).ConfigureAwait(false);

        应用运行期(快照);

        // 重启连接循环：StopAsync 末尾会断开底层 TCP，StartAsync 重新起循环，
        // 循环内 ConnectAsync 会用刚更新的 Host/Port 建立新连接。轮询引擎无需重启——
        // 它未连接时本就空转等待，重连成功后自动恢复采集。
        await _连接.异步停止(取消令牌).ConfigureAwait(false);
        await _连接.异步启动(取消令牌).ConfigureAwait(false);

        _日志.LogWarning(
            "PLC 连接参数已变更并生效：{Host}:{Port}（从站号 {UnitId}，自动重连 {Auto}）",
            主机, 候选.端口, 候选.单元标识, 候选.自动重连 ? "开" : "关");

        已保存?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>把参数整体写入 config/appsettings.json（原子写；其余节原样保留）。</summary>
    private async Task 异步落盘(PLC连接选项 快照, CancellationToken 取消令牌)
    {
        // 2026-11-19 安全加固：这里是"加载整份 → 改 Plc 节 → 整体写回"。
        // 若主文件与 .bak 都坏了，加载器只能给出出厂默认 —— 直接写回就等于
        // 用默认值抹掉现场的扫码枪 / 轮询 / 轴定义配置。因此这种情况必须拒绝落盘。
        var 结果 = 应用设置加载器.加载带结果();

        var 文档 = 结果.加载结果 switch
        {
            加载结果分类.已加载 => 结果.值!,

            加载结果分类.缺失 => new 应用设置文档(),   // 首次部署：按默认文档创建

            加载结果分类.已从备份恢复 => 结果.值!,

            _ => throw new InvalidOperationException(
                $"应用配置读取失败（主文件与备份均不可用），为避免覆盖现场配置已中止保存：{结果.错误信息}"),
        };

        if (结果.加载结果 is 加载结果分类.已从备份恢复)
        {
            // 允许继续，但必须留痕：现场需要知道"最近一次改动可能已丢失"
            _日志.LogWarning(
                "保存 PLC 连接参数前发现 appsettings.json 损坏，已从 .bak 恢复后继续：{Path}", 结果.路径);
        }

        文档.PLC连接 = 快照;

        // appsettings.json 现为 PascalCase 风格（与 tags.json 等人工手写的 camelCase 文件不同口径），
        // 用 Write 保持风格不漂移
        var JSON文本 = JsonSerializer.Serialize(文档, JSON配置.写入);

        try
        {
            await 原子文件.异步写入全部文本(配置路径.应用设置, JSON文本, 取消令牌)
                .ConfigureAwait(false);
        }
        catch (Exception 异常)
        {
            _日志.LogError(异常, "保存 PLC 连接参数失败：{Path}", 配置路径.应用设置);
            throw;
        }
    }

    /// <summary>把已落盘的参数同步进共享单例。通信层每次连接现读这些值，改完即对下次连接生效。</summary>
    private void 应用运行期(PLC连接选项 快照)
    {
        _选项.主机 = 快照.主机;
        _选项.端口 = 快照.端口;
        _选项.单元标识 = 快照.单元标识;
        _选项.连接超时毫秒 = 快照.连接超时毫秒;
        _选项.读取超时毫秒 = 快照.读取超时毫秒;
        _选项.心跳间隔毫秒 = 快照.心跳间隔毫秒;
        _选项.重连初始延迟毫秒 = 快照.重连初始延迟毫秒;
        _选项.重连最大延迟毫秒 = 快照.重连最大延迟毫秒;
        _选项.每次请求最大寄存器数 = 快照.每次请求最大寄存器数;
        _选项.自动重连 = 快照.自动重连;
        _选项.通讯协议 = 快照.通讯协议;
        _选项.OPCUA端口 = 快照.OPCUA端口;
        _选项.OPCUA命名空间索引 = 快照.OPCUA命名空间索引;
        _选项.模拟从站端口 = 快照.模拟从站端口;
        _选项.模拟从站监听地址 = 快照.模拟从站监听地址;
    }
}
