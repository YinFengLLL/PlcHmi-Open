using Microsoft.Extensions.Logging;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// <see cref="手动控件服务"/> 的「手动页元件（按地址操作）」部分（partial）。
///
/// <para>从主文件拆出：主文件已接近规模门禁，而这三件事（写线圈 / 读线圈 / 脉冲线圈）
/// 与"按轴点动"是两条独立链路——前者按配置里的元件地址工作，后者按轴标签名拼装。</para>
/// </summary>
public sealed partial class 手动控件服务
{
    // ── 手动页元件（按地址操作） ────────────────────────────────────────

    /// <inheritdoc />
    public async Task 异步写入线圈(
        string 地址值, bool 值, CancellationToken 取消令牌 = default)
    {
        _权限.要求(权限.视图全部页面);
        确保启用();

        var Modbus地址 = 解析设备中继(地址值);

        await _客户端.异步写入单个线圈(Modbus地址, 值, 取消令牌).ConfigureAwait(false);

        _日志.LogInformation("手动元件 {Address} 写入 {Value}", 地址值, 值);
    }

    /// <inheritdoc />
    public async Task<bool> 异步读取线圈(string 地址值, CancellationToken 取消令牌 = default)
    {
        _权限.要求(权限.视图全部页面);

        // 只读操作不受手动使能限制：使能关着也该能看到线圈的真实状态
        var Modbus地址 = 解析设备中继(地址值);

        var 线圈列表 = await _客户端.异步读取线圈(Modbus地址, 1, 取消令牌).ConfigureAwait(false);

        return 线圈列表.Length > 0 && 线圈列表[0];
    }

    /// <inheritdoc />
    public async Task 异步脉冲线圈(
        string 地址值, TimeSpan 宽度, CancellationToken 取消令牌 = default)
    {
        _权限.要求(权限.视图全部页面);
        确保启用();

        var Modbus地址 = 解析设备中继(地址值);
        var 脉冲 = 宽度 <= TimeSpan.Zero ? 默认脉冲宽度 : 宽度;

        // 复位是无条件安全出口：即使等待期间被取消（页面切换/断开），
        // 线圈也绝不能停在置位态——否则气缸/命令会卡在动作位。
        // 与 TagWriter.异步脉冲 同款 try/finally 结构，复位写不携带取消令牌。
        try
        {
            await _客户端.异步写入单个线圈(Modbus地址, true, 取消令牌).ConfigureAwait(false);
            await Task.Delay(脉冲, 取消令牌).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await _客户端.异步写入单个线圈(Modbus地址, false, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception 异常)
            {
                // 复位失败必须显式告警：此时线圈可能停在置位态，需要现场介入
                _日志.LogError(异常, "手动元件 {Address} 脉冲复位失败，线圈可能停在置位态", 地址值);
            }
        }

        _日志.LogInformation("手动元件 {Address} 脉冲 {Width}ms", 地址值, 脉冲.TotalMilliseconds);
    }

    /// <summary>
    /// 解析元件地址，并要求必须是 M 区。
    /// <para>这是"不直接控制 Y 点"这条约束的最后一道防线——即使配置文件被手工改错，
    /// 这里也会拒绝执行。</para>
    /// </summary>
    private int 解析设备中继(string 地址值)
    {
        if (!_客户端.是否已连接)
        {
            throw new InvalidOperationException("PLC 未连接，无法执行手动动作。");
        }

        if (!_解析器.尝试解析(地址值, out var PLC地址) || PLC地址 is null)
        {
            throw new InvalidOperationException($"「{地址值}」不是有效的软元件地址。");
        }

        if (PLC地址.元素 != 软元素.M)
        {
            throw new InvalidOperationException(
                $"「{地址值}」必须是 M 区地址（当前解析为 {PLC地址.元素} 区）。"
                + "上位机只允许操作内部继电器，由 PLC 程序决定何时驱动 Y 输出。");
        }

        return PLC地址.Modbus地址;
    }
}
