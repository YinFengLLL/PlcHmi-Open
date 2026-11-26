using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using 上位机.业务.配置;
using 上位机.通信.Modbus;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// 扫码枪服务（2026-11-12 新增，实现思路参考 SmartScanner）。
///
/// <para><b>手动型：</b>不管触发寄存器——扫码枪自己扫到码就往串口/网口发，
/// 上位机收到即视作"扫码成功"：把条码记为工单号，并给
/// <see cref="扫码枪配置.扫码成功寄存器"/> 写 1。</para>
///
/// <para><b>触发型：</b>轮询 <see cref="扫码枪配置.扫码触发寄存器"/>，读到 1 就写 0 应答，
/// 下发触发指令并开一个等码窗口（<see cref="扫码枪配置.超时毫秒"/>）：
/// 窗口内收到条码 → 写 1；超时未收到 → 写 2 并下发释放指令（见 <c>ScannerService.Timeout.cs</c>）。</para>
///
/// <para><b>工单号只存内存：</b>关软件即清空（现场要求：不跨重启保留扫码信息）。</para>
/// </summary>
public sealed partial class 扫码枪服务 : IHostedService, 扫码枪服务接口, IDisposable
{
    private readonly 地址解析器接口 _解析器;
    private readonly Modbus客户端接口 _客户端;
    private readonly 权限守卫接口 _权限;
    private readonly 扫码枪配置 _配置;
    private readonly ILogger<扫码枪服务> _日志;

    private 扫码枪传输接口? _传输;
    private CancellationTokenSource? _触发取消;
    private Task? _触发任务;
    private volatile string _工单号 = string.Empty;

    private int _成功寄存器;
    private int _触发寄存器;

    public 扫码枪服务(
        扫码枪配置 配置,
        地址解析器接口 解析器,
        Modbus客户端接口 客户端,
        权限守卫接口 权限,
        ILogger<扫码枪服务> 日志)
    {
        _配置 = 配置;
        _解析器 = 解析器;
        _客户端 = 客户端;
        _权限 = 权限;
        _日志 = 日志;

        刷新寄存器地址();
    }

    /// <inheritdoc />
    public 扫码枪配置 配置 => _配置;

    /// <inheritdoc />
    public bool 已连接 => _传输?.已打开 == true;

    /// <inheritdoc />
    public string 工单号 => _工单号;

    /// <inheritdoc />
    public event EventHandler<string>? 工单号已变更;

    /// <inheritdoc />
    public event EventHandler<bool>? 连接已变更;

    // ── 生命周期 ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken 取消令牌)
    {
        await 异步重启(取消令牌).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken 取消令牌)
    {
        停止内部();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task 异步重启(CancellationToken 取消令牌 = default)
    {
        停止内部();

        if (!_配置.启用)
        {
            _日志.LogInformation("扫码枪未启用（系统设置 → 扫码枪设置）");
            return Task.CompletedTask;
        }

        打开通道();

        if (_配置.工作模式 == 扫码工作模式.触发型)
        {
            启动触发监听();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task 异步保存设置(扫码枪配置 候选配置, CancellationToken 取消令牌 = default)
    {
        ArgumentNullException.ThrowIfNull(候选配置);

        _权限.要求(权限.编辑PLC连接);

        校验(候选配置);

        var 快照 = 候选配置.克隆();

        // 先落盘、后应用：写入是原子的，写失败时内存与文件都还是旧值。
        // 2026-11-19 安全加固：主文件与 .bak 都坏时拒绝落盘，绝不拿出厂默认值覆盖现场配置。
        var 结果 = 应用设置加载器.加载带结果();

        var 文档 = 结果.加载结果 switch
        {
            加载结果分类.已加载 => 结果.值!,
            加载结果分类.缺失 => new 应用设置文档(),      // 首次部署：按默认文档创建
            加载结果分类.已从备份恢复 => 结果.值!,
            _ => throw new InvalidOperationException(
                $"应用配置读取失败（主文件与备份均不可用），为避免覆盖现场配置已中止保存：{结果.错误信息}"),
        };

        if (结果.加载结果 is 加载结果分类.已从备份恢复)
        {
            _日志.LogWarning(
                "保存扫码枪设置前发现 appsettings.json 损坏，已从 .bak 恢复后继续：{Path}", 结果.路径);
        }

        文档.扫码枪 = 快照;

        var JSON文本 = JsonSerializer.Serialize(文档, JSON配置.写入);

        await 原子文件
            .异步写入全部文本(配置路径.应用设置, JSON文本, 取消令牌)
            .ConfigureAwait(false);

        _配置.复制自(快照);
        刷新寄存器地址();

        await 异步重启(取消令牌).ConfigureAwait(false);

        _日志.LogInformation(
            "扫码枪设置已保存：{Mode} · {Transport} · 成功 {Ok} / 触发 {Trigger}",
            _配置.工作模式, _配置.通讯模式, _配置.扫码成功寄存器, _配置.扫码触发寄存器);
    }

    /// <inheritdoc />
    public void Dispose() => 停止内部();

    // ── 通道 ────────────────────────────────────────────────────────────

    private void 打开通道()
    {
        var 传输 = _配置.通讯模式 == 扫码通讯模式.TcpIp
            ? new Tcp扫码传输()
            : (扫码枪传输接口)new 串口扫码传输();

        传输.条码已接收 += (_, 条码) => 处理条码(条码);
        传输.连接状态已变更 += (_, 已连接值) => 连接已变更?.Invoke(this, 已连接值);

        if (!传输.打开(_配置, out var 错误))
        {
            _日志.LogWarning("扫码枪打开失败：{Error}", 错误);
            传输.Dispose();
            连接已变更?.Invoke(this, false);
            return;
        }

        _传输 = 传输;

        _日志.LogInformation(
            "扫码枪已连接：{Transport}（{Mode}）",
            _配置.通讯模式 == 扫码通讯模式.TcpIp
                ? $"TCP {_配置.网口地址}:{_配置.网口端口}"
                : $"串口 {_配置.串口号} {_配置.波特率},{_配置.数据位},{_配置.校验},{_配置.停止位}",
            _配置.工作模式);
    }

    private void 停止内部()
    {
        // 先关等码窗口：停止期间不应再因为超时去写 PLC
        关闭等码窗口();

        _触发取消?.Cancel();
        _触发取消?.Dispose();
        _触发取消 = null;

        if (_触发任务 is { } 任务)
        {
            try
            {
                任务.Wait(TimeSpan.FromSeconds(1));
            }
            catch (AggregateException)
            {
                // 停止时的取消异常无需上报
            }

            _触发任务 = null;
        }

        if (_传输 is { } 传输)
        {
            // 通道对象直接丢弃：句柄在 Dispose 里释放，事件订阅随对象一起失效
            传输.Dispose();
            _传输 = null;
        }
    }

    // ── 扫码结果 ────────────────────────────────────────────────────────

    /// <summary>
    /// 把一条条码当作"刚从扫码枪收到"注入（与真实接收走同一条处理链）。
    /// <para>用途：现场排查（没带扫码枪也能验证信号与工单号）与自动化测试。
    /// 注意它<b>不是</b>后门：触发型下同样要处在等码窗口内才算数，
    /// 否则就绕过"超时后晚到的码不算数"这条规矩了。</para>
    /// </summary>
    public void 注入扫码结果(string 条码) => 处理条码(条码);

    private void 处理条码(string 条码)
    {
        var 文本 = 条码?.Trim() ?? string.Empty;

        if (文本.Length == 0)
        {
            return;
        }

        if (!认领扫码结果())
        {
            // 触发型：没有待扫码请求（例如上一件已判超时失败）→ 晚到的码不能算数
            _日志.LogWarning("触发型：收到条码「{Code}」但当前没有待扫码请求，已忽略", 文本);
            return;
        }

        // 先记工单号、后写成功信号：PLC 见到 D4050=1 时，历史记录取到的工单号就是这一条
        _工单号 = 文本;
        工单号已变更?.Invoke(this, 文本);

        _日志.LogInformation("扫码成功：{Code}", 文本);

        _ = Task.Run(() => 异步写信号(1, "扫码成功信号"));
    }

    /// <summary>写扫码结果信号（<c>1</c> = 扫到；<c>2</c> = 触发后超时未扫到）。</summary>
    private async Task 异步写信号(int 值, string 用途)
    {
        if (!_客户端.是否已连接 || _成功寄存器 <= 0)
        {
            _日志.LogWarning("需要写{Use}（{Value}）但 PLC 未连接（或成功信号寄存器未配置）", 用途, 值);
            return;
        }

        try
        {
            await _客户端.异步写入单个寄存器(_成功寄存器, (ushort)值).ConfigureAwait(false);
            _日志.LogInformation("已写{Use}：D{Address} = {Value}", 用途, _成功寄存器, 值);
        }
        catch (InvalidOperationException 异常)
        {
            _日志.LogWarning(异常, "写{Use}失败（D{Address}）", 用途, _成功寄存器);
        }
        catch (IOException 异常)
        {
            _日志.LogWarning(异常, "写{Use}失败（D{Address}）", 用途, _成功寄存器);
        }
        catch (SocketException 异常)
        {
            _日志.LogWarning(异常, "写{Use}失败（D{Address}）", 用途, _成功寄存器);
        }
    }

    // ── 触发型 ──────────────────────────────────────────────────────────

    private void 启动触发监听()
    {
        _触发取消 = new CancellationTokenSource();
        var 令牌 = _触发取消.Token;

        _触发任务 = Task.Run(() => 触发循环(令牌), 令牌);

        _日志.LogInformation(
            "触发型扫码已启动：监听 D{Address}（每 {Interval} ms）",
            _触发寄存器, Math.Clamp(_配置.轮询间隔毫秒, 100, 5000));
    }

    private async Task 触发循环(CancellationToken 取消令牌)
    {
        var 间隔 = TimeSpan.FromMilliseconds(Math.Clamp(_配置.轮询间隔毫秒, 100, 5000));
        using var 计时器 = new PeriodicTimer(间隔);

        try
        {
            while (await 计时器.WaitForNextTickAsync(取消令牌).ConfigureAwait(false))
            {
                if (!_客户端.是否已连接 || _触发寄存器 <= 0)
                {
                    continue;
                }

                ushort[] 值;

                try
                {
                    值 = await _客户端.异步读取保持寄存器(_触发寄存器, 1, 取消令牌).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    continue;
                }
                catch (IOException)
                {
                    continue;
                }
                catch (SocketException)
                {
                    continue;
                }

                if (值.Length == 0 || 值[0] == 0)
                {
                    continue;
                }

                try
                {
                    await _客户端.异步写入单个寄存器(_触发寄存器, 0, 取消令牌).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    continue;
                }
                catch (IOException)
                {
                    continue;
                }
                catch (SocketException)
                {
                    continue;
                }

                // 顺序要紧：**先开窗、后下发触发指令**。
                // 反过来的话，自动感应型扫码枪可能在指令发出后几毫秒就回码，
                // 那条码会撞在"窗口还没开"上被判成"没有待扫码请求"丢掉，
                // 这一件就白白走到超时失败。
                打开等码窗口();
                下发指令(_配置.触发指令, "触发");

                _日志.LogInformation(
                    "触发型：PLC 置位 D{Address}，已写 0 应答并开等码窗口（超时 {Timeout} ms）",
                    _触发寄存器, Math.Clamp(_配置.超时毫秒, 200, 60000));
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
    }
}
