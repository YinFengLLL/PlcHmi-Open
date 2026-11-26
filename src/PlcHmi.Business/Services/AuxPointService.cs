using System.Globalization;
using Microsoft.Extensions.Logging;
using PlcHmi.Business.Configuration;
using PlcHmi.Communication.Modbus;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// 辅助点（其它点）服务：管理非锁附/点胶类的辅助点位（滑螺丝点、相机中心、锁杆中心…）。
///
/// <para>点位定义（名称 + X/Y/Z 基址）落盘 <c>config/aux-points.json</c>，
/// 坐标值存放在 PLC 的对应寄存器中；支持增删改查与 CSV 导入导出。</para>
/// </summary>
public sealed partial class AuxPointService : IAuxPointService
{
    private readonly IModbusClient _client;
    private readonly RecipeConfig _config;
    private readonly IPermissionGuard _permission;
    private readonly ILogger<AuxPointService> _log;

    private readonly List<AuxPointDefinition> _point = new();

    /// <summary>
    /// 点位列表的同步锁。此前本服务完全无同步：CSV 导入的 Clear()+AddRange 与
    /// UI 枚举 <see cref="PointCollection"/> 并发会抛 Collection was modified。
    /// </summary>
    private readonly object _sync = new();

    public AuxPointService(
        IModbusClient Client,
        RecipeConfig configPairObject,
        IPermissionGuard Permission,
        ILogger<AuxPointService> Log)
    {
        _client = Client;
        _config = configPairObject;
        _permission = Permission;
        _log = Log;

        // 先以配置中的默认点位打底；落盘文件存在时会在初始化阶段整体覆盖
        foreach (var PointEntry in _config.OtherPoint.Point)
        {
            _point.Add(PointEntry.Clone());
        }
    }

    /// <inheritdoc />
    public event EventHandler? changed;

    /// <inheritdoc />
    // 快照交出：与 报警定义服务/参数表格服务/手动元件服务 统一口径。
    // 曾经直接交出内部 List，调用方既可绕过权限 Add，枚举期间遭遇 Clear() 还会抛异常。
    public IReadOnlyList<AuxPointDefinition> PointCollection
    {
        get { lock (_sync) { return _point.ToArray(); } }
    }

    /// <inheritdoc />
    public OtherPointConfig Config => _config.OtherPoint;

    // ── 初始化 ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var result = await ConfigFileLoader
            .LoadJsonAsync<AuxPointFile>(ConfigPath.OtherPoint, JsonConfig.Read, _log, cancellationToken)
            .ConfigureAwait(false);

        if (result.Value is null)
        {
            _log.LogInformation("未找到其它点定义文件，按配置默认点位启动：{Path}", ConfigPath.OtherPoint);
            return;
        }

        lock (_sync)
        {
            _point.Clear();

            foreach (var PointEntry in result.Value.Point)
            {
                _point.Add(PointEntry);
            }

            _log.LogInformation("已加载其它点定义：{Count} 个", _point.Count);
        }

        changed?.Invoke(this, EventArgs.Empty);
    }

    // ── 坐标读写 ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<(double X, double Y, double Z)> ReadCoordinateAsync(
        AuxPointDefinition PointItem, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(PointItem);

        return (await ReadFieldAsync(PointItem.XBaseAddress, cancellationToken).ConfigureAwait(false),
                await ReadFieldAsync(PointItem.YBaseAddress, cancellationToken).ConfigureAwait(false),
                await ReadFieldAsync(PointItem.ZBaseAddress, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task WriteCoordinateAsync(
        AuxPointDefinition PointItem, double X, double Y, double Z, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(PointItem);

        _permission.WantRequire(Permission.EditTeach);

        var range = _config.OtherPoint;

        foreach (var (nameValue, Value) in new[] { ("X", X), ("Y", Y), ("Z", Z) })
        {
            if (Value < range.MinValue || Value > range.MaxValue)
            {
                throw new InvalidOperationException(
                    $"{PointItem.Name} 的 {nameValue} = {Value:0.00} 超出允许范围"
                    + $"（{range.MinValue:0.00} ~ {range.MaxValue:0.00}）。");
            }
        }

        if (!_client.IsConnected)
        {
            throw new InvalidOperationException("PLC 未连接，无法写入坐标。");
        }

        await WriteFieldAsync(PointItem.XBaseAddress, X, cancellationToken).ConfigureAwait(false);
        await WriteFieldAsync(PointItem.YBaseAddress, Y, cancellationToken).ConfigureAwait(false);
        await WriteFieldAsync(PointItem.ZBaseAddress, Z, cancellationToken).ConfigureAwait(false);

        _log.LogInformation("已写入其它点 {Name}：X={X} Y={Y} Z={Z}", PointItem.Name, X, Y, Z);
    }

    // ── 增删改查 ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task AddAsync(AuxPointDefinition PointItem, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(PointItem);
        _permission.WantRequire(Permission.EditBindAddress);

        if (string.IsNullOrWhiteSpace(PointItem.Name))
        {
            throw new InvalidOperationException("点位名称不能为空。");
        }

        lock (_sync)
        {
            if (_config.OtherPoint.Find(PointItem.Name) is not null
                || _point.Any(PointEntry => string.Equals(PointEntry.Name, PointItem.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"其它点「{PointItem.Name}」已存在。");
            }

            ValidateAddress(PointItem);

            _point.Add(PointItem.Clone());
        }

        await PersistAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task EditAsync(string originalName, AuxPointDefinition PointItem, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(PointItem);
        _permission.WantRequire(Permission.EditBindAddress);

        if (string.IsNullOrWhiteSpace(PointItem.Name))
        {
            throw new InvalidOperationException("点位名称不能为空。");
        }

        lock (_sync)
        {
            var index = _point.FindIndex(PointEntry => string.Equals(PointEntry.Name, originalName, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
            {
                throw new InvalidOperationException($"其它点「{originalName}」不存在。");
            }

            // 允许改名（2026-10-15 现场反馈），但改名后不能与别的点撞名——
            // 名称是"按名查找 / 删除 / CSV 导入"的依据，重名会让后续操作指向错误的点。
            // 只排除自己，这样"只改坐标、名称原样提交"不会被误判。
            if (_point.Where((PointEntry, Sequence) => Sequence != index)
                    .Any(PointEntry => string.Equals(PointEntry.Name, PointItem.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"其它点「{PointItem.Name}」已存在，请换一个名称。");
            }

            ValidateAddress(PointItem);

            _point[index] = PointItem.Clone();
        }

        await PersistAsync(cancellationToken).ConfigureAwait(false);

        if (!string.Equals(originalName, PointItem.Name, StringComparison.Ordinal))
        {
            _log.LogInformation("其它点已改名：{From} → {To}", originalName, PointItem.Name);
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string nameValue, CancellationToken cancellationToken = default)
    {
        _permission.WantRequire(Permission.EditBindAddress);

        lock (_sync)
        {
            var index = _point.FindIndex(PointEntry => string.Equals(PointEntry.Name, nameValue, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
            {
                throw new InvalidOperationException($"其它点「{nameValue}」不存在。");
            }

            _point.RemoveAt(index);
        }

        await PersistAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IReadOnlyList<AuxPointDefinition> Query(string keyword)
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                return _point.ToList();
            }

            var Key = keyword.Trim();
            return _point
                .Where(PointEntry => PointEntry.Name.Contains(Key, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    // CSV 导入导出见 AuxPointService.Csv.cs（partial）。

    // ── 内部 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 地址基本校验。
    /// <para><b>2026-10-12：</b>按现场要求取消地址区间限制——不再要求落在
    /// <c>其它点.允许范围</c> 内；只保留"必须是正整数"这一条录入错误判定。</para>
    /// </summary>
    private static void ValidateAddress(AuxPointDefinition PointItem)
    {
        foreach (var (nameValue, addressValue) in new[]
                 {
                     ("X", PointItem.XBaseAddress), ("Y", PointItem.YBaseAddress), ("Z", PointItem.ZBaseAddress),
                 })
        {
            if (addressValue <= 0)
            {
                throw new InvalidOperationException($"{PointItem.Name} 的 {nameValue} 地址必须是正整数。");
            }
        }
    }

    private async Task<double> ReadFieldAsync(int addressValue, CancellationToken cancellationToken)
    {
        if (!_client.IsConnected)
        {
            return 0;
        }

        var registerList = await _client.ReadHoldingRegisterAsync(addressValue, 2, cancellationToken).ConfigureAwait(false);

        if (registerList.Length < 2)
        {
            return 0;
        }

        var Value = RegisterCodec.Decode(TagDataType.Float32, registerList, WordOrder.LittleEndian);
        return Convert.ToDouble(Value, CultureInfo.InvariantCulture);
    }

    private async Task WriteFieldAsync(int addressValue, double Value, CancellationToken cancellationToken)
    {
        var registerList = RegisterCodec.Encode(TagDataType.Float32, Value, WordOrder.LittleEndian);
        await _client.WriteMultiRegisterAsync(addressValue, registerList, cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        var fileItem = new AuxPointFile { Point = _point.Select(PointEntry => PointEntry.Clone()).ToList() };
        var jsonText = System.Text.Json.JsonSerializer.Serialize(fileItem, JsonConfig.Write);
        await AtomicFile.WriteAllTextAsync(ConfigPath.OtherPoint, jsonText, cancellationToken).ConfigureAwait(false);
        changed?.Invoke(this, EventArgs.Empty);
    }
}
