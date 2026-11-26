using Microsoft.Extensions.Logging;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// <see cref="页面上下文服务接口"/> 的默认实现：把页面号（D10）与各示教页的点码
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
public sealed class 页面上下文服务 : 页面上下文服务接口
{
    private readonly Modbus客户端接口 _客户端;
    private readonly ILogger<页面上下文服务> _日志;

    /// <summary>写入串行化（页码与各点码可能被并发触发）。</summary>
    private readonly SemaphoreSlim _锁 = new(1, 1);

    /// <summary>最近一次应写入 PLC 的页面号（未连接时留作补写用）。</summary>
    private int? _最近页码;

    /// <summary>
    /// 各点码寄存器最近一次的值（未连接时留作补写用）。
    /// <para>按寄存器分别记忆：锁附（D651）与点胶（D652）是两个独立的上下文，
    /// 合成一个字段会让连接补写把其中一页写成另一页的点号。</para>
    /// </summary>
    private readonly Dictionary<int, int> _最近点号 = new();

    public 页面上下文服务(
        Modbus客户端接口 客户端,
        连接管理器接口 连接,
        ILogger<页面上下文服务> 日志)
    {
        _客户端 = 客户端;
        _日志 = 日志;

        // 连接建立后补写一次：开机阶段的切页 / 点选大多发生在连上之前
        连接.状态已变更 += (_, 状态) =>
        {
            if (状态 is 连接状态.已连接)
            {
                _ = 补写();
            }
        };
    }

    /// <inheritdoc />
    public Task 异步写入页码(int 页码, CancellationToken 取消令牌 = default)
    {
        _最近页码 = 页码;

        return 异步写入(页面上下文寄存器.页码, 页码, "页面号", 取消令牌);
    }

    /// <inheritdoc />
    public Task 异步写入点号(int 点号寄存器, int 点号, CancellationToken 取消令牌 = default)
    {
        _最近点号[点号寄存器] = 点号;

        return 异步写入(点号寄存器, 点号, $"{点码名称(点号寄存器)}点码", 取消令牌);
    }

    /// <summary>点码寄存器对应的现场名称（仅用于日志可读性）。</summary>
    private static string 点码名称(int 点号寄存器) => 点号寄存器 switch
    {
        页面上下文寄存器.锁附示教点码 => "锁附示教",
        页面上下文寄存器.点胶示教点码 => "点胶示教",
        _ => string.Empty,
    };

    private async Task 异步写入(int 寄存器, int 值, string 名称, CancellationToken 取消令牌)
    {
        if (!_客户端.是否已连接)
        {
            _日志.LogDebug(
                "PLC 未连接，{Name} = D{Register}（{Value}）先记在本机，连上后自动补写",
                名称, 寄存器, 值);
            return;
        }

        await _锁.WaitAsync(取消令牌).ConfigureAwait(false);

        try
        {
            await _客户端.异步写入单个寄存器(寄存器, (ushort)值, 取消令牌).ConfigureAwait(false);
            _日志.LogDebug("{Name} 已写入：D{Register} = {Value}", 名称, 寄存器, 值);
        }
        catch (Exception 异常)
        {
            // 只记警告：这几个字是上下文提示，不是关键写入，绝不能反过来卡住界面
            _日志.LogWarning(异常, "写入 {Name}（D{Register} = {Value}）失败", 名称, 寄存器, 值);
        }
        finally
        {
            _锁.Release();
        }
    }

    /// <summary>连接建立后补写最近一次页面号与各页点码。</summary>
    private async Task 补写()
    {
        if (_最近页码 is { } 页码)
        {
            await 异步写入(页面上下文寄存器.页码, 页码, "页面号（连接后补写）", CancellationToken.None)
                .ConfigureAwait(false);
        }

        foreach (var (寄存器, 点号) in _最近点号)
        {
            await 异步写入(寄存器, 点号, $"{点码名称(寄存器)}点码（连接后补写）", CancellationToken.None)
                .ConfigureAwait(false);
        }
    }
}
