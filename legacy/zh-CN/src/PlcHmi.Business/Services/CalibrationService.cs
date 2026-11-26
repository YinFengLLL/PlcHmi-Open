using System.Globalization;
using Microsoft.Extensions.Logging;
using 上位机.通信.Modbus;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// 标定示教服务：标定中心点坐标、每点偏移量的读写，以及「标定启动」按钮。
/// <para>原「移至指定位置」按钮已按现场要求删除（2026-10-15）：
/// 它与锁附分组的动作按钮「移全指定」（同一个 M460）重复，用动作按钮即可。</para>
///
/// <para><b>坐标读写</b>走 D 区 Float32；<b>动作按钮</b>只置位 M 区线圈（脉冲），
/// 由 PLC 程序执行实际标定动作——与手动页同一道联锁（要求手动使能）。</para>
/// </summary>
public sealed class 标定服务 : 标定服务接口
{
    private readonly 手动控件服务接口 _手动;
    private readonly Modbus客户端接口 _客户端;
    private readonly 配方配置 _配置;
    private readonly 权限守卫接口 _权限;
    private readonly ILogger<标定服务> _日志;

    public 标定服务(
        手动控件服务接口 手动,
        Modbus客户端接口 客户端,
        配方配置 配置对象,
        权限守卫接口 权限,
        ILogger<标定服务> 日志)
    {
        _手动 = 手动;
        _客户端 = 客户端;
        _配置 = 配置对象;
        _权限 = 权限;
        _日志 = 日志;
    }

    /// <inheritdoc />
    public 标定配置 配置 => _配置.标定;

    /// <inheritdoc />
    public async Task<(double X, double Y, double Z)> 异步读取中心点(CancellationToken 取消令牌 = default)
    {
        var X = await 异步读取字段(配置.查找中心点("X"), 取消令牌).ConfigureAwait(false);
        var Y = await 异步读取字段(配置.查找中心点("Y"), 取消令牌).ConfigureAwait(false);
        var Z = await 异步读取字段(配置.查找中心点("Z"), 取消令牌).ConfigureAwait(false);
        return (X, Y, Z);
    }

    /// <inheritdoc />
    public async Task 异步写入中心点(double X, double Y, double Z, CancellationToken 取消令牌 = default)
    {
        _权限.要求(权限.编辑示教);

        await 异步写入字段(配置.查找中心点("X"), X, 取消令牌).ConfigureAwait(false);
        await 异步写入字段(配置.查找中心点("Y"), Y, 取消令牌).ConfigureAwait(false);
        await 异步写入字段(配置.查找中心点("Z"), Z, 取消令牌).ConfigureAwait(false);

        _日志.LogInformation("标定中心点已写入：X={X} Y={Y} Z={Z}", X, Y, Z);
    }

    /// <inheritdoc />
    public Task<double> 异步读取偏移量(CancellationToken 取消令牌 = default) =>
        异步读取字段(配置.每点偏移, 取消令牌);

    /// <inheritdoc />
    public async Task 异步写入偏移量(double 值, CancellationToken 取消令牌 = default)
    {
        _权限.要求(权限.编辑示教);
        await 异步写入字段(配置.每点偏移, 值, 取消令牌).ConfigureAwait(false);
        _日志.LogInformation("每点偏移量已写入：{Value}", 值);
    }

    /// <inheritdoc />
    public Task 异步标定启动(CancellationToken 取消令牌 = default) =>
        _手动.异步脉冲线圈(配置.启动地址, TimeSpan.Zero, 取消令牌);

    // ── 下相机（2026-10-17 新增）：地址处理模式与上相机一致，但没有 Y ────

    /// <inheritdoc />
    public async Task<(double X, double Z)> 异步读取下相机中心点(CancellationToken 取消令牌 = default)
    {
        var X = await 异步读取字段(配置.查找下相机("X"), 取消令牌).ConfigureAwait(false);
        var Z = await 异步读取字段(配置.查找下相机("Z"), 取消令牌).ConfigureAwait(false);
        return (X, Z);
    }

    /// <inheritdoc />
    public async Task 异步写入下相机中心点(double X, double Z, CancellationToken 取消令牌 = default)
    {
        _权限.要求(权限.编辑示教);

        await 异步写入字段(配置.查找下相机("X"), X, 取消令牌).ConfigureAwait(false);
        await 异步写入字段(配置.查找下相机("Z"), Z, 取消令牌).ConfigureAwait(false);

        _日志.LogInformation("下相机标定起点已写入：X={X} Z={Z}", X, Z);
    }

    /// <inheritdoc />
    public Task<double> 异步读取下相机偏移量(CancellationToken 取消令牌 = default) =>
        异步读取字段(配置.下相机每点偏移, 取消令牌);

    /// <inheritdoc />
    public async Task 异步写入下相机偏移量(double 值, CancellationToken 取消令牌 = default)
    {
        _权限.要求(权限.编辑示教);
        await 异步写入字段(配置.下相机每点偏移, 值, 取消令牌).ConfigureAwait(false);
        _日志.LogInformation("下相机标定偏移量已写入：{Value}", 值);
    }

    /// <inheritdoc />
    public Task 异步下相机标定启动(CancellationToken 取消令牌 = default) =>
        _手动.异步脉冲线圈(配置.下相机启动地址, TimeSpan.Zero, 取消令牌);

    private async Task<double> 异步读取字段(标定字段定义? 字段项, CancellationToken 取消令牌)
    {
        if (字段项 is null || !_客户端.是否已连接)
        {
            return 0;
        }

        var 寄存器列表 = await _客户端
            .异步读取保持寄存器(字段项.基址, 字段项.寄存器数量, 取消令牌)
            .ConfigureAwait(false);

        if (寄存器列表.Length < 字段项.寄存器数量)
        {
            return 0;
        }

        var 值 = 寄存器编解码器.解码(字段项.数据类型, 寄存器列表, 字顺序.小字节序);
        return Convert.ToDouble(值, CultureInfo.InvariantCulture);
    }

    private async Task 异步写入字段(标定字段定义? 字段项, double 值, CancellationToken 取消令牌)
    {
        if (字段项 is null)
        {
            throw new InvalidOperationException(
                "标定配置缺少该字段：请到标定页「编辑地址」补上对应地址"
                + "（config/recipe-config.json 的 calibration）。");
        }

        if (!_客户端.是否已连接)
        {
            throw new InvalidOperationException("PLC 未连接，无法写入标定参数。");
        }

        var 寄存器列表 = 寄存器编解码器.编码(字段项.数据类型, 值, 字顺序.小字节序);

        if (寄存器列表.Length == 1)
        {
            await _客户端.异步写入单个寄存器(字段项.基址, 寄存器列表[0], 取消令牌).ConfigureAwait(false);
        }
        else
        {
            await _客户端.异步写入多个寄存器(字段项.基址, 寄存器列表, 取消令牌).ConfigureAwait(false);
        }
    }
}
