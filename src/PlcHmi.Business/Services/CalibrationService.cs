using System.Globalization;
using Microsoft.Extensions.Logging;
using PlcHmi.Communication.Modbus;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// 标定示教服务：标定中心点坐标、每点偏移量的读写，以及「标定启动」按钮。
/// <para>原「移至指定位置」按钮已按现场要求删除（2026-10-15）：
/// 它与锁附分组的动作按钮「移全指定」（同一个 M460）重复，用动作按钮即可。</para>
///
/// <para><b>坐标读写</b>走 D 区 Float32；<b>动作按钮</b>只置位 M 区线圈（脉冲），
/// 由 PLC 程序执行实际标定动作——与手动页同一道联锁（要求手动使能）。</para>
/// </summary>
public sealed class CalibrationService : ICalibrationService
{
    private readonly IManualWidgetService _manual;
    private readonly IModbusClient _client;
    private readonly RecipeConfig _config;
    private readonly IPermissionGuard _permission;
    private readonly ILogger<CalibrationService> _log;

    public CalibrationService(
        IManualWidgetService Manual,
        IModbusClient Client,
        RecipeConfig configPairObject,
        IPermissionGuard Permission,
        ILogger<CalibrationService> Log)
    {
        _manual = Manual;
        _client = Client;
        _config = configPairObject;
        _permission = Permission;
        _log = Log;
    }

    /// <inheritdoc />
    public CalibrationConfig Config => _config.Calibration;

    /// <inheritdoc />
    public async Task<(double X, double Y, double Z)> ReadCenterAsync(CancellationToken cancellationToken = default)
    {
        var X = await ReadFieldAsync(Config.FindCenter("X"), cancellationToken).ConfigureAwait(false);
        var Y = await ReadFieldAsync(Config.FindCenter("Y"), cancellationToken).ConfigureAwait(false);
        var Z = await ReadFieldAsync(Config.FindCenter("Z"), cancellationToken).ConfigureAwait(false);
        return (X, Y, Z);
    }

    /// <inheritdoc />
    public async Task WriteCenterAsync(double X, double Y, double Z, CancellationToken cancellationToken = default)
    {
        _permission.WantRequire(Permission.EditTeach);

        await WriteFieldAsync(Config.FindCenter("X"), X, cancellationToken).ConfigureAwait(false);
        await WriteFieldAsync(Config.FindCenter("Y"), Y, cancellationToken).ConfigureAwait(false);
        await WriteFieldAsync(Config.FindCenter("Z"), Z, cancellationToken).ConfigureAwait(false);

        _log.LogInformation("标定中心点已写入：X={X} Y={Y} Z={Z}", X, Y, Z);
    }

    /// <inheritdoc />
    public Task<double> ReadOffsetAsync(CancellationToken cancellationToken = default) =>
        ReadFieldAsync(Config.PerPointOffset, cancellationToken);

    /// <inheritdoc />
    public async Task WriteOffsetAsync(double Value, CancellationToken cancellationToken = default)
    {
        _permission.WantRequire(Permission.EditTeach);
        await WriteFieldAsync(Config.PerPointOffset, Value, cancellationToken).ConfigureAwait(false);
        _log.LogInformation("每点偏移量已写入：{Value}", Value);
    }

    /// <inheritdoc />
    public Task CalibrationStartAsync(CancellationToken cancellationToken = default) =>
        _manual.PulseCoilAsync(Config.StartAddress, TimeSpan.Zero, cancellationToken);

    // ── 下相机（2026-10-17 新增）：地址处理模式与上相机一致，但没有 Y ────

    /// <inheritdoc />
    public async Task<(double X, double Z)> ReadBottomCameraCenterAsync(CancellationToken cancellationToken = default)
    {
        var X = await ReadFieldAsync(Config.FindBottomCamera("X"), cancellationToken).ConfigureAwait(false);
        var Z = await ReadFieldAsync(Config.FindBottomCamera("Z"), cancellationToken).ConfigureAwait(false);
        return (X, Z);
    }

    /// <inheritdoc />
    public async Task WriteBottomCameraCenterAsync(double X, double Z, CancellationToken cancellationToken = default)
    {
        _permission.WantRequire(Permission.EditTeach);

        await WriteFieldAsync(Config.FindBottomCamera("X"), X, cancellationToken).ConfigureAwait(false);
        await WriteFieldAsync(Config.FindBottomCamera("Z"), Z, cancellationToken).ConfigureAwait(false);

        _log.LogInformation("下相机标定起点已写入：X={X} Z={Z}", X, Z);
    }

    /// <inheritdoc />
    public Task<double> ReadBottomCameraOffsetAsync(CancellationToken cancellationToken = default) =>
        ReadFieldAsync(Config.BottomCameraPerPointOffset, cancellationToken);

    /// <inheritdoc />
    public async Task WriteBottomCameraOffsetAsync(double Value, CancellationToken cancellationToken = default)
    {
        _permission.WantRequire(Permission.EditTeach);
        await WriteFieldAsync(Config.BottomCameraPerPointOffset, Value, cancellationToken).ConfigureAwait(false);
        _log.LogInformation("下相机标定偏移量已写入：{Value}", Value);
    }

    /// <inheritdoc />
    public Task BottomCameraCalibrationStartAsync(CancellationToken cancellationToken = default) =>
        _manual.PulseCoilAsync(Config.BottomCameraStartAddress, TimeSpan.Zero, cancellationToken);

    private async Task<double> ReadFieldAsync(CalibrationFieldDefinition? fieldItem, CancellationToken cancellationToken)
    {
        if (fieldItem is null || !_client.IsConnected)
        {
            return 0;
        }

        var registerList = await _client
            .ReadHoldingRegisterAsync(fieldItem.BaseAddress, fieldItem.RegisterCount, cancellationToken)
            .ConfigureAwait(false);

        if (registerList.Length < fieldItem.RegisterCount)
        {
            return 0;
        }

        var Value = RegisterCodec.Decode(fieldItem.DataType, registerList, WordOrder.LittleEndian);
        return Convert.ToDouble(Value, CultureInfo.InvariantCulture);
    }

    private async Task WriteFieldAsync(CalibrationFieldDefinition? fieldItem, double Value, CancellationToken cancellationToken)
    {
        if (fieldItem is null)
        {
            throw new InvalidOperationException(
                "标定配置缺少该字段：请到标定页「编辑地址」补上对应地址"
                + "（config/recipe-config.json 的 calibration）。");
        }

        if (!_client.IsConnected)
        {
            throw new InvalidOperationException("PLC 未连接，无法写入标定参数。");
        }

        var registerList = RegisterCodec.Encode(fieldItem.DataType, Value, WordOrder.LittleEndian);

        if (registerList.Length == 1)
        {
            await _client.WriteSingleRegisterAsync(fieldItem.BaseAddress, registerList[0], cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _client.WriteMultiRegisterAsync(fieldItem.BaseAddress, registerList, cancellationToken).ConfigureAwait(false);
        }
    }
}
