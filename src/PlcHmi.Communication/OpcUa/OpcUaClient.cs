using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Models;

namespace PlcHmi.Communication.OpcUa;

/// <summary>
/// 基于 OPCFoundation .NET Standard 栈的 OPC UA 客户端实现，
/// 与 FluentModbusTCP客户端 共用 <see cref="IModbusClient"/> 契约。
///
/// <para><b>职责边界（与 Modbus 客户端一致）：</b>只负责“把值搬运到位”——
/// 把区域+地址推导为 NodeId 后读写节点值。<b>不做任何字序处理</b>，
/// 32 位数据的 CDAB 拆装仍由 <c>寄存器编解码器</c> 在业务侧完成（红线 R1）。</para>
///
/// <para><b>并发约束：</b>与 Modbus 相同，所有收发经 <see cref="_writeGate"/> 串行化。
/// 断线行为与 FluentModbus 客户端一致——连接失败 / 读写失败抛异常，
/// 由连接管理器统一走指数退避重连（心跳“读 D0”自动翻译为读 D0 节点）。
/// 读写语义的翻译见 <c>OpcUaClient.IO.cs</c>。</para>
/// </summary>
public sealed partial class OpcUaClient : IModbusClient
{
    /// <summary>会话超时下限：至少覆盖两个心跳周期，避免心跳间隔偏大时被服务端踢下线。</summary>
    private const int minSessionTimeoutMilliseconds = 60000;

    /// <summary>OPC UA 单次批量请求的节点数上限。轮询引擎的单块合并不超过 100，此处留足余量。</summary>
    private const int maxBatchSectionPointCount = 2000;

    /// <summary>栈的遥测上下文（1.5.x 新 API 需要），默认无附加日志输出。</summary>
    private static readonly ITelemetryContext defaultRemoteMeasureContext = DefaultTelemetry.Create(_ => { });

    private readonly PlcConnectionOptions _options;
    private readonly NodeIdResolver _resolver;
    private readonly ILogger<OpcUaClient> _log;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    // volatile：确保已连接()/确保已连接() 在无锁读时能看到 异步关闭会话()（重连线程）的置空
    private volatile ISession? _session;
    private bool _disposed;

    public OpcUaClient(PlcConnectionOptions Options, NodeIdResolver Resolver, ILogger<OpcUaClient> Log)
    {
        _options = Options ?? throw new ArgumentNullException(nameof(Options));
        _resolver = Resolver ?? throw new ArgumentNullException(nameof(Resolver));
        _log = Log;
    }

    /// <inheritdoc />
    public bool IsConnected => _session is not null && !_session.KeepAliveStopped;

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="UserIdentity"/> 为 IDisposable，由会话接管所有权并随会话释放——
    /// 分析器无法识别这种所有权转移，故显式声明抑制。
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "可靠性", "CA2000:缺少范围释放对象",
        Justification = "UserIdentity 由会话接管所有权，随会话统一释放")]
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected)
            {
                return;
            }

            await CloseSessionAsync().ConfigureAwait(false);

            // 会话建立是网络 IO，直接用栈的异步 API（SessionClientBatched 内部全异步）。
            // 非 ServiceResult 异常不做翻译，原样上抛——连接失败由连接管理器统一记录与重试。
            var workFactory = new DefaultSessionFactory(defaultRemoteMeasureContext);
            var Session = await workFactory.CreateAsync(
                CreateApplyConfig(), CreateEndpoint(), false, "PlcHmi",
                (uint)Math.Max(minSessionTimeoutMilliseconds, _options.HeartbeatIntervalMilliseconds * 2),
                CreateBodyPart(), null, cancellationToken).ConfigureAwait(false);

            // 创建成功立刻记账：本方法后续任何异常都能经 异步关闭会话() 统一收口，
            // 不会留下"会话已建立但无人持有"的泄漏窗口（与 Modbus 侧"先记账再连接"同款）
            _session = Session;

            _log.LogInformation("已连接 OPC UA 服务端 opc.tcp://{Host}:{Port}",
                _options.Host, _options.OpcUaPort);
        }
        catch (ServiceResultException exception)
        {
            await CloseSessionAsync().ConfigureAwait(false);
            _log.LogWarning("连接 OPC UA 服务端 {Host}:{Port} 失败：{Message}",
                _options.Host, _options.OpcUaPort, exception.Message);
            throw new InvalidOperationException($"连接 OPC UA 服务端失败：{exception.Message}", exception);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task DisconnectAsync()
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseSessionAsync().ConfigureAwait(false);
            _log.LogInformation("已断开 OPC UA 连接");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // ── 会话管理 ────────────────────────────────────────────────────────

    /// <summary>
    /// 客户端应用配置。默认安全策略 None + 自动信任服务端证书（工控现场 PLC 服务端常用自签证书）；
    /// 需要鉴权时在配置中填写 用户名/密码，身份即切换为用户名口令。
    /// </summary>
    private ApplicationConfiguration CreateApplyConfig()
    {
        var Config = new ApplicationConfiguration
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
                DefaultSessionTimeout = minSessionTimeoutMilliseconds,
            },
            TransportConfigurations = new TransportConfigurationCollection(),
            TransportQuotas = new TransportQuotas
            {
                OperationTimeout = _options.ReadTimeoutMilliseconds,
            },
            CertificateValidator = new CertificateValidator(defaultRemoteMeasureContext),
        };

        // 现场默认自动信任：PLC 内置服务端用自签证书，上位机无人值守，弹不出确认框
        Config.CertificateValidator.CertificateValidation += (_, eventArgs) => eventArgs.Accept = true;

        return Config;
    }

    private ConfiguredEndpoint CreateEndpoint()
    {
        var ApplyConfig = CreateApplyConfig();

        var endpointDescription = new EndpointDescription
        {
            EndpointUrl = $"opc.tcp://{_options.Host}:{_options.OpcUaPort}",
            SecurityPolicyUri = SecurityPolicies.None,
            Server = new ApplicationDescription(),
        };

        return new ConfiguredEndpoint(null, endpointDescription, EndpointConfiguration.Create(ApplyConfig));
    }

    /// <summary>按配置创建身份：用户名为空用匿名，否则用用户名口令。</summary>
    /// <remarks>身份令牌为 IDisposable，<see cref="UserIdentity"/> 构造即接管其所有权并随会话释放。</remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "可靠性", "CA2000:缺少范围释放对象",
        Justification = "UserIdentity 构造接管令牌所有权，随会话统一释放")]
    private UserIdentity CreateBodyPart()
    {
        if (string.IsNullOrWhiteSpace(_options.OpcUaUserName))
        {
            return new UserIdentity(new AnonymousIdentityToken());
        }

        // UserIdentity(string, byte[]) 内部自建令牌，避免中间 Disposable 对象触发 CA2000
        return new UserIdentity(
            _options.OpcUaUserName,
            System.Text.Encoding.UTF8.GetBytes(_options.OpcUaPassword ?? string.Empty));
    }

    /// <summary>
    /// 关闭并释放当前会话。CloseAsync 的异常只记录不吞——调用方
    /// （异步安全断开 / 宿主释放）各自有兜底处理；finally 中必然 Dispose，保证会话资源不泄漏。
    /// </summary>
    private async Task CloseSessionAsync()
    {
        if (_session is null)
        {
            return;
        }

        var Session = _session;
        _session = null;

        try
        {
            await Session.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (ServiceResultException exception)
        {
            _log.LogDebug(exception, "关闭 OPC UA 会话时服务端返回错误，已忽略");
        }
        finally
        {
            Session.Dispose();
        }
    }

    private ISession EnsureConnected()
    {
        if (_session is null || _session.KeepAliveStopped)
        {
            throw new InvalidOperationException("PLC 未连接，无法执行读写操作。");
        }

        return _session;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DisconnectAsync().ConfigureAwait(false);
        _writeGate.Dispose();
    }
}
