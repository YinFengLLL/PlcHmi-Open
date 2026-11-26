using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using 上位机.核心.抽象;
using 上位机.核心.模型;

namespace 上位机.通信.OPCUA;

/// <summary>
/// 基于 OPCFoundation .NET Standard 栈的 OPC UA 客户端实现，
/// 与 FluentModbusTCP客户端 共用 <see cref="Modbus客户端接口"/> 契约。
///
/// <para><b>职责边界（与 Modbus 客户端一致）：</b>只负责“把值搬运到位”——
/// 把区域+地址推导为 NodeId 后读写节点值。<b>不做任何字序处理</b>，
/// 32 位数据的 CDAB 拆装仍由 <c>寄存器编解码器</c> 在业务侧完成（红线 R1）。</para>
///
/// <para><b>并发约束：</b>与 Modbus 相同，所有收发经 <see cref="_写入闸锁"/> 串行化。
/// 断线行为与 FluentModbus 客户端一致——连接失败 / 读写失败抛异常，
/// 由连接管理器统一走指数退避重连（心跳“读 D0”自动翻译为读 D0 节点）。
/// 读写语义的翻译见 <c>OpcUaClient.IO.cs</c>。</para>
/// </summary>
public sealed partial class OPCUA客户端 : Modbus客户端接口
{
    /// <summary>会话超时下限：至少覆盖两个心跳周期，避免心跳间隔偏大时被服务端踢下线。</summary>
    private const int 最小会话超时毫秒 = 60000;

    /// <summary>OPC UA 单次批量请求的节点数上限。轮询引擎的单块合并不超过 100，此处留足余量。</summary>
    private const int 最大批量节点数 = 2000;

    /// <summary>栈的遥测上下文（1.5.x 新 API 需要），默认无附加日志输出。</summary>
    private static readonly ITelemetryContext 默认遥测上下文 = DefaultTelemetry.Create(_ => { });

    private readonly PLC连接选项 _选项;
    private readonly NodeId推导器 _推导器;
    private readonly ILogger<OPCUA客户端> _日志;
    private readonly SemaphoreSlim _写入闸锁 = new(1, 1);

    // volatile：确保已连接()/确保已连接() 在无锁读时能看到 异步关闭会话()（重连线程）的置空
    private volatile ISession? _会话;
    private bool _已释放;

    public OPCUA客户端(PLC连接选项 选项, NodeId推导器 推导器, ILogger<OPCUA客户端> 日志)
    {
        _选项 = 选项 ?? throw new ArgumentNullException(nameof(选项));
        _推导器 = 推导器 ?? throw new ArgumentNullException(nameof(推导器));
        _日志 = 日志;
    }

    /// <inheritdoc />
    public bool 是否已连接 => _会话 is not null && !_会话.KeepAliveStopped;

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="UserIdentity"/> 为 IDisposable，由会话接管所有权并随会话释放——
    /// 分析器无法识别这种所有权转移，故显式声明抑制。
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "可靠性", "CA2000:缺少范围释放对象",
        Justification = "UserIdentity 由会话接管所有权，随会话统一释放")]
    public async Task 异步连接(CancellationToken 取消令牌 = default)
    {
        await _写入闸锁.WaitAsync(取消令牌).ConfigureAwait(false);
        try
        {
            if (是否已连接)
            {
                return;
            }

            await 异步关闭会话().ConfigureAwait(false);

            // 会话建立是网络 IO，直接用栈的异步 API（SessionClientBatched 内部全异步）。
            // 非 ServiceResult 异常不做翻译，原样上抛——连接失败由连接管理器统一记录与重试。
            var 工厂 = new DefaultSessionFactory(默认遥测上下文);
            var 会话 = await 工厂.CreateAsync(
                创建应用配置(), 创建端点(), false, "PlcHmi",
                (uint)Math.Max(最小会话超时毫秒, _选项.心跳间隔毫秒 * 2),
                创建身份(), null, 取消令牌).ConfigureAwait(false);

            // 创建成功立刻记账：本方法后续任何异常都能经 异步关闭会话() 统一收口，
            // 不会留下"会话已建立但无人持有"的泄漏窗口（与 Modbus 侧"先记账再连接"同款）
            _会话 = 会话;

            _日志.LogInformation("已连接 OPC UA 服务端 opc.tcp://{Host}:{Port}",
                _选项.主机, _选项.OPCUA端口);
        }
        catch (ServiceResultException 异常)
        {
            await 异步关闭会话().ConfigureAwait(false);
            _日志.LogWarning("连接 OPC UA 服务端 {Host}:{Port} 失败：{Message}",
                _选项.主机, _选项.OPCUA端口, 异常.Message);
            throw new InvalidOperationException($"连接 OPC UA 服务端失败：{异常.Message}", 异常);
        }
        finally
        {
            _写入闸锁.Release();
        }
    }

    /// <inheritdoc />
    public async Task 异步断开()
    {
        await _写入闸锁.WaitAsync().ConfigureAwait(false);
        try
        {
            await 异步关闭会话().ConfigureAwait(false);
            _日志.LogInformation("已断开 OPC UA 连接");
        }
        finally
        {
            _写入闸锁.Release();
        }
    }

    // ── 会话管理 ────────────────────────────────────────────────────────

    /// <summary>
    /// 客户端应用配置。默认安全策略 None + 自动信任服务端证书（工控现场 PLC 服务端常用自签证书）；
    /// 需要鉴权时在配置中填写 用户名/密码，身份即切换为用户名口令。
    /// </summary>
    private ApplicationConfiguration 创建应用配置()
    {
        var 配置 = new ApplicationConfiguration
        {
            ApplicationName = "PlcHmi 上位机",
            ApplicationUri = $"urn:{System.Net.Dns.GetHostName()}:PlcHmi:client",
            ProductUri = "urn:PlcHmi:client",
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration
            {
                AutoAcceptUntrustedCertificates = true,
                RejectSHA1SignedCertificates = false,
                MinimumCertificateKeySize = 1024,
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = "pki/own",
                    SubjectName = "PlcHmi",
                },
                TrustedIssuerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = "pki/issuers",
                },
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = "pki/trusted",
                },
                RejectedCertificateStore = new CertificateStoreIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = "pki/rejected",
                },
            },
            ClientConfiguration = new ClientConfiguration
            {
                DefaultSessionTimeout = 最小会话超时毫秒,
            },
            TransportConfigurations = new TransportConfigurationCollection(),
            TransportQuotas = new TransportQuotas
            {
                OperationTimeout = _选项.读取超时毫秒,
            },
            CertificateValidator = new CertificateValidator(默认遥测上下文),
        };

        // 现场默认自动信任：PLC 内置服务端用自签证书，上位机无人值守，弹不出确认框
        配置.CertificateValidator.CertificateValidation += (_, 事件参数) => 事件参数.Accept = true;

        return 配置;
    }

    private ConfiguredEndpoint 创建端点()
    {
        var 应用配置 = 创建应用配置();

        var 端点描述 = new EndpointDescription
        {
            EndpointUrl = $"opc.tcp://{_选项.主机}:{_选项.OPCUA端口}",
            SecurityPolicyUri = SecurityPolicies.None,
            Server = new ApplicationDescription(),
        };

        return new ConfiguredEndpoint(null, 端点描述, EndpointConfiguration.Create(应用配置));
    }

    /// <summary>按配置创建身份：用户名为空用匿名，否则用用户名口令。</summary>
    /// <remarks>身份令牌为 IDisposable，<see cref="UserIdentity"/> 构造即接管其所有权并随会话释放。</remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "可靠性", "CA2000:缺少范围释放对象",
        Justification = "UserIdentity 构造接管令牌所有权，随会话统一释放")]
    private UserIdentity 创建身份()
    {
        if (string.IsNullOrWhiteSpace(_选项.OPCUA用户名))
        {
            return new UserIdentity(new AnonymousIdentityToken());
        }

        // UserIdentity(string, byte[]) 内部自建令牌，避免中间 Disposable 对象触发 CA2000
        return new UserIdentity(
            _选项.OPCUA用户名,
            System.Text.Encoding.UTF8.GetBytes(_选项.OPCUA密码 ?? string.Empty));
    }

    /// <summary>
    /// 关闭并释放当前会话。CloseAsync 的异常只记录不吞——调用方
    /// （异步安全断开 / 宿主释放）各自有兜底处理；finally 中必然 Dispose，保证会话资源不泄漏。
    /// </summary>
    private async Task 异步关闭会话()
    {
        if (_会话 is null)
        {
            return;
        }

        var 会话 = _会话;
        _会话 = null;

        try
        {
            await 会话.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (ServiceResultException 异常)
        {
            _日志.LogDebug(异常, "关闭 OPC UA 会话时服务端返回错误，已忽略");
        }
        finally
        {
            会话.Dispose();
        }
    }

    private ISession 确保已连接()
    {
        if (_会话 is null || _会话.KeepAliveStopped)
        {
            throw new InvalidOperationException("PLC 未连接，无法执行读写操作。");
        }

        return _会话;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_已释放)
        {
            return;
        }

        _已释放 = true;
        await 异步断开().ConfigureAwait(false);
        _写入闸锁.Dispose();
    }
}
