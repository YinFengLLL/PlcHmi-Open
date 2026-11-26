using Microsoft.Extensions.Logging;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;
using 上位机.通信.Modbus;
using 上位机.通信.仿真;

namespace 上位机.业务.服务;

/// <summary>
/// Modbus 运行模式管理器。在运行期把上位机在「真实 PLC / 模拟 PLC」之间切换，
/// 并负责模拟模式下供外部工具离线调试的从站启停。
///
/// <para><b>切换的完整动作：</b>停连接（断开当前对端）→ 替换客户端实现（真实 ↔ 仿真）→
/// 起/停模拟从站 → 重启连接。全程不重建 DI、不重启程序。</para>
///
/// <para><b>状态一致性的关键：</b>连接管理器注入的是 <see cref="可切换Modbus客户端"/> 这一层代理，
/// 它始终指向"当前生效"的实现。因此切换只发生在服务层这一处，其它服务无感知。</para>
///
/// <para><b>不落盘：</b>运行模式仅存在于内存（<see cref="_选项"/> 的模拟开关只在运行期改动，
/// 保存连接参数时由 <see cref="运行模式策略.持久化模拟开关"/> 强制写 false）。下次启动照旧进入真实 PLC。</para>
/// </summary>
public sealed class Modbus运行模式管理器 : 运行模式管理器接口, IAsyncDisposable
{
    private readonly 可切换Modbus客户端 _客户端代理;
    private readonly FluentModbusTCP客户端 _真实客户端;
    private readonly 仿真Modbus客户端 _仿真客户端;
    private readonly 虚拟PLC从站 _从站;
    private readonly 连接管理器接口 _连接;
    private readonly PLC连接选项 _选项;
    private readonly 权限守卫接口 _权限;
    private readonly ILogger<Modbus运行模式管理器> _日志;

    /// <summary>切换是"停—换—起"三段式，必须串行化，避免两次切换交叉执行。</summary>
    private readonly SemaphoreSlim _切换闸锁 = new(1, 1);

    private volatile 运行模式 _当前模式;
    private volatile bool _是否切换中;

    public Modbus运行模式管理器(
        可切换Modbus客户端 客户端代理,
        FluentModbusTCP客户端 真实客户端,
        仿真Modbus客户端 仿真客户端,
        虚拟PLC从站 从站,
        连接管理器接口 连接,
        PLC连接选项 选项,
        权限守卫接口 权限,
        ILogger<Modbus运行模式管理器> 日志)
    {
        _客户端代理 = 客户端代理;
        _真实客户端 = 真实客户端;
        _仿真客户端 = 仿真客户端;
        _从站 = 从站;
        _连接 = 连接;
        _选项 = 选项;
        _权限 = 权限;
        _日志 = 日志;

        // 初始模式跟随 DI 装配（由 使用模拟器 决定）：产品启动流程会把 Modbus 的 使用模拟器
        // 强制为 false（见 App 启动策略），因此现场每次启动都是真实 PLC；测试可注入 true。
        _当前模式 = _选项.使用模拟器 ? 运行模式.模拟PLC : 运行模式.真实PLC;
    }

    /// <inheritdoc />
    public 运行模式 当前模式 => _当前模式;

    /// <inheritdoc />
    public bool 可在运行期切换 => 运行模式策略.可运行期切换(_选项.通讯协议);

    /// <inheritdoc />
    public bool 是否切换中 => _是否切换中;

    /// <inheritdoc />
    public bool 模拟从站是否运行 => _从站.是否运行;

    /// <inheritdoc />
    public string 模拟从站端点文本 => _从站.端点文本;

    /// <inheritdoc />
    public string 模拟从站状态文本 => _从站.状态文本;

    /// <inheritdoc />
    public event EventHandler? 模式已变更;

    /// <inheritdoc />
    public async Task 异步切换(运行模式 目标, CancellationToken 取消令牌 = default)
    {
        if (!可在运行期切换)
        {
            throw new InvalidOperationException(
                "当前通讯协议不支持运行期切换运行模式。OPC UA 模式请改用「通讯模式 + 重启程序」的方式切换。");
        }

        // 切到模拟模式会解除对真实设备的反馈约束：必须是管理员显式授权
        _权限.要求(权限.切换运行模式);

        await _切换闸锁.WaitAsync(取消令牌).ConfigureAwait(false);

        try
        {
            if (目标 == _当前模式)
            {
                return;
            }

            // 进入模拟模式前先拉起从站：监听失败就在"什么都没改变"的状态下明确报错，
            // 而不是切完一半留个外部工具连不上的僵局。
            if (目标 == 运行模式.模拟PLC)
            {
                await _从站.启动Async(取消令牌).ConfigureAwait(false);
            }

            _是否切换中 = true;

            try
            {
                await _连接.异步停止(取消令牌).ConfigureAwait(false);

                if (目标 == 运行模式.模拟PLC)
                {
                    _客户端代理.切换实现(_仿真客户端);
                    _选项.使用模拟器 = true;
                }
                else
                {
                    _客户端代理.切换实现(_真实客户端);
                    _选项.使用模拟器 = false;
                }

                await _连接.异步启动(取消令牌).ConfigureAwait(false);

                if (目标 == 运行模式.真实PLC)
                {
                    await _从站.异步停止().ConfigureAwait(false);
                }

                _当前模式 = 目标;

                _日志.LogWarning("运行模式已切换为 {Mode}：{Detail}", 目标,
                    目标 == 运行模式.模拟PLC
                        ? $"模拟从站监听 {_从站.端点文本}，可用 Modbus 工具离线调试"
                        : "已恢复真实 PLC 连接");
            }
            finally
            {
                _是否切换中 = false;
            }

            模式已变更?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _切换闸锁.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _从站.异步停止().ConfigureAwait(false);
        _切换闸锁.Dispose();
    }
}
