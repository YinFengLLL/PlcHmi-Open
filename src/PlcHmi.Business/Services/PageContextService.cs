using Microsoft.Extensions.Logging;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// <see cref="IPageContextService"/> 的默认实现：把页面号（D10）与各示教页的点码
/// （锁附 D651 / 点胶 D652）写入 PLC。
///
/// <para><b>三条设计取舍：</b></para>
/// <list type="number">
/// <item><b>串行化写入</b>：页面号与点码可能来自不同线程（切页在主线程、点选在点表刷新链路），
/// 用信号量把各笔写排队，避免帧交错。</item>
/// <item><b>不因写失败卡住界面</b>：失败只记警告返回——操作员切页/点选绝不能因为 PLC 抖动被拦住。</item>
/// <item><b>连上就补写</b>：开机时往往还没连上 PLC，此时切页/点选只记在内存；
/// 连接建立后自动补写最近值，PLC 侧不会停在空白（见构造函数的连接事件订阅）。</item>
/// </list>
/// </summary>
public sealed class PageContextService : IPageContextService
{
    private readonly IModbusClient _client;
    private readonly ILogger<PageContextService> _log;

    /// <summary>写入串行化（页码与各点码可能被并发触发）。</summary>
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>最近一次应写入 PLC 的页面号（未连接时留作补写用）。</summary>
    private int? _mostRecentPageNumber;

    /// <summary>
    /// 各点码寄存器最近一次的值（未连接时留作补写用）。
    /// <para>按寄存器分别记忆：锁附（D651）与点胶（D652）是两个独立的上下文，
    /// 合成一个字段会让连接补写把其中一页写成另一页的点号。</para>
    /// </summary>
    private readonly Dictionary<int, int> _mostRecentPointNumber = new();

    public PageContextService(
        IModbusClient Client,
        IConnectManager Connect,
        ILogger<PageContextService> Log)
    {
        _client = Client;
        _log = Log;

        // 连接建立后补写一次：开机阶段的切页 / 点选大多发生在连上之前
        Connect.StateChanged += (_, State) =>
        {
            if (State is ConnectionState.Connected)
            {
                _ = Backfill();
            }
        };
    }

    /// <inheritdoc />
    public Task WritePageNumberAsync(int pageNumber, CancellationToken cancellationToken = default)
    {
        _mostRecentPageNumber = pageNumber;

        return WriteAsync(PageContextRegister.pageNumber, pageNumber, "页面号", cancellationToken);
    }

    /// <inheritdoc />
    public Task WritePointNumberAsync(int pointNumberRegister, int pointNumber, CancellationToken cancellationToken = default)
    {
        _mostRecentPointNumber[pointNumberRegister] = pointNumber;

        return WriteAsync(pointNumberRegister, pointNumber, $"{PointCodeName(pointNumberRegister)}点码", cancellationToken);
    }

    /// <summary>点码寄存器对应的现场名称（仅用于日志可读性）。</summary>
    private static string PointCodeName(int pointNumberRegister) => pointNumberRegister switch
    {
        PageContextRegister.screwTeachPointCode => "锁附示教",
        PageContextRegister.dispenseTeachPointCode => "点胶示教",
        _ => string.Empty,
    };

    private async Task WriteAsync(int register, int Value, string Name, CancellationToken cancellationToken)
    {
        if (!_client.IsConnected)
        {
            _log.LogDebug(
                "PLC 未连接，{Name} = D{Register}（{Value}）先记在本机，连上后自动补写",
                Name, register, Value);
            return;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await _client.WriteSingleRegisterAsync(register, (ushort)Value, cancellationToken).ConfigureAwait(false);
            _log.LogDebug("{Name} 已写入：D{Register} = {Value}", Name, register, Value);
        }
        catch (Exception exception)
        {
            // 只记警告：这几个字是上下文提示，不是关键写入，绝不能反过来卡住界面
            _log.LogWarning(exception, "写入 {Name}（D{Register} = {Value}）失败", Name, register, Value);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>连接建立后补写最近一次页面号与各页点码。</summary>
    private async Task Backfill()
    {
        if (_mostRecentPageNumber is { } pageNumber)
        {
            await WriteAsync(PageContextRegister.pageNumber, pageNumber, "页面号（连接后补写）", CancellationToken.None)
                .ConfigureAwait(false);
        }

        foreach (var (register, pointNumber) in _mostRecentPointNumber)
        {
            await WriteAsync(register, pointNumber, $"{PointCodeName(register)}点码（连接后补写）", CancellationToken.None)
                .ConfigureAwait(false);
        }
    }
}
