using System.Globalization;
using Microsoft.Extensions.Logging;
using PlcHmi.Business.Configuration;
using PlcHmi.Communication.Modbus;
using PlcHmi.Communication.Recipe;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// 通用点表服务：锁附示教与点胶示教共用同一套读写。
///
/// <para><b>布局：</b>每张点表按列分区存放，第 i 点某列地址 = 列基址 + i × 字每点位。
/// 读一整列是<b>一次块读</b>；下发一整列是<b>一次块写</b>。</para>
///
/// <para><b>写入时机硬约束：</b>只有在「保存坐标点」或「参数写入」时才写 PLC；
/// 下发前逐点做坐标范围校验，防止从未示教的空点把轴驱动到原点。</para>
/// </summary>
public sealed partial class PointTableService : IPointTableService
{
    private readonly IModbusClient _client;
    private readonly ITagValueService _valueList;
    private readonly IRecipeService _recipe;
    private readonly RecipeConfig _config;
    private readonly IPermissionGuard _permission;
    private readonly ILogger<PointTableService> _log;

    /// <summary>表键 → 地址映射。</summary>
    private readonly Dictionary<string, PointTableAddressMap> _map = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>表键 → 当前行缓存（永远包含全部点）。</summary>
    private readonly Dictionary<string, List<PointValueRow>> _bufferStore = new(StringComparer.OrdinalIgnoreCase);

    public PointTableService(
        IModbusClient Client,
        ITagValueService ValueList,
        IRecipeService Recipe,
        RecipeConfig configPairObject,
        IPermissionGuard Permission,
        ILogger<PointTableService> Log)
    {
        _client = Client;
        _valueList = ValueList;
        _recipe = Recipe;
        _config = configPairObject;
        _permission = Permission;
        _log = Log;

        foreach (var TableEntry in _config.PointTable.PointTable)
        {
            _map[TableEntry.Key] = new PointTableAddressMap(TableEntry);
            _bufferStore[TableEntry.Key] = AllEmptyLine(TableEntry);
        }
    }

    /// <inheritdoc />
    public event EventHandler<string>? pointTableChanged;

    /// <inheritdoc />
    public IReadOnlyList<PointTableDefinition> AllPointTable => _config.PointTable.PointTable;

    /// <inheritdoc />
    public PointTableDefinition Table(string Key) =>
        _config.PointTable.Find(Key)
        ?? throw new InvalidOperationException(
            $"未配置点表「{Key}」（config/recipe-config.json 的 pointTables）。");

    /// <inheritdoc />
    public int PointCount(string Key) => _map[Table(Key).Key].PointCount;

    /// <inheritdoc />
    /// <remarks>
    /// 返回内部行缓存（列表结构在建表后稳定，仅元素值会变）。
    /// <b>仅限界面线程访问</b>：采集/后台线程请改用事件 + 深拷贝口径。
    /// </remarks>
    public IReadOnlyList<PointValueRow> CurrentPointTable(string Key) => _bufferStore[Table(Key).Key];

    // ── 初始化与加载 ────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        foreach (var TableEntry in _config.PointTable.PointTable)
        {
            SlaveStationKindSnapshotFill(TableEntry);
        }

        _log.LogInformation(
            "已按机种 {No} 的快照载入 {Count} 张点表",
            _recipe.CurrentRecipeNumber, _config.PointTable.PointTable.Count);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task LoadPointTableAsync(string Key, CancellationToken cancellationToken = default)
    {
        var TableEntry = Table(Key);

        SlaveStationKindSnapshotFill(TableEntry);
        pointTableChanged?.Invoke(this, TableEntry.Key);

        _log.LogDebug("已按机种快照载入点表 {Table}", TableEntry.DisplayName);
        return Task.CompletedTask;
    }

    // ── 内存编辑 ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public void SetPointValue(string Key, int Sequence, string ColumnKey, double Value)
    {
        var TableEntry = Table(Key);
        var mapItem = _map[TableEntry.Key];

        if (!mapItem.IndexValid(Sequence))
        {
            return;
        }

        var rowItem = _bufferStore[TableEntry.Key][Sequence];

        // 变化检测：值没变不发事件。曾经每次调用都触发 点表已变更，
        // 界面为整行重建行 VM——输入框每键触发（即使值相同）都在白做工
        if (rowItem.Take(ColumnKey) == Value)
        {
            return;
        }

        rowItem.setup(ColumnKey, Value);
        pointTableChanged?.Invoke(this, TableEntry.Key);
    }

    /// <inheritdoc />
    public void ClearEmptyPoint(string Key, int Sequence)
    {
        var TableEntry = Table(Key);
        var mapItem = _map[TableEntry.Key];

        if (!mapItem.IndexValid(Sequence))
        {
            return;
        }

        _bufferStore[TableEntry.Key][Sequence] = new PointValueRow { Sequence = Sequence };
        _log.LogInformation("已清空 {Table} 第 {Point} 点", TableEntry.DisplayName, Sequence + 1);
        pointTableChanged?.Invoke(this, TableEntry.Key);
    }

    /// <inheritdoc />
    public PointValueRow ReadCurrentPosition(string Key)
    {
        var TableEntry = Table(Key);
        var mapItem = _map[TableEntry.Key];

        // 从空行出发：只填"参与保存坐标且带位置标签"的列。
        // 其余列（指令码、浮高，或本表未列入 保存坐标列 的轴）由调用方沿用目标点自身的值，
        // 不能借别的点的缓存来填——否则会把第 1 点的指令码/浮高复制到所选点。
        var rowItem = new PointValueRow();

        foreach (var columnItem in TableEntry.CoordinateColumn)
        {
            if (string.IsNullOrWhiteSpace(columnItem.PositionTag) || !TableEntry.IsAndSaveCoordinate(columnItem.Key))
            {
                continue;
            }

            // 浮高来自测高地址（D240），不是 Z 轴位置：这里不填，由 异步示教当前位置 单独读
            if (IsFloatHeightTable(TableEntry)
                && string.Equals(columnItem.Key, floatHeightColumnKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (_valueList.TryGetDouble(columnItem.PositionTag, out var Value))
            {
                rowItem.setup(columnItem.Key, Value);
            }
        }

        _ = mapItem;
        return rowItem;
    }

    // ── 示教与下发 ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task TeachCurrentPositionAsync(string Key, int Sequence, CancellationToken cancellationToken = default)
    {
        _permission.WantRequire(Permission.EditTeach);

        var TableEntry = Table(Key);
        var mapItem = _map[TableEntry.Key];

        if (!mapItem.IndexValid(Sequence))
        {
            throw new ArgumentOutOfRangeException(nameof(Sequence), Sequence, "点序号超出范围。");
        }

        var samplingValue = ReadCurrentPosition(Key);
        var rowItem = _bufferStore[TableEntry.Key][Sequence];

        // 浮高只对锁附表生效（2026-10-24 现场口径：点胶示教不写浮高）。
        // 由测高机构给出（独立地址），不是任何轴的位置：配了地址就必须读成功，
        // 否则宁可不示教——把 Z 轴位置或 0 写进浮高列更难查
        var needFloatHeight = IsFloatHeightTable(TableEntry)
                       && TableEntry.FindColumn(floatHeightColumnKey) is not null
                       && TableEntry.IsAndSaveCoordinate(floatHeightColumnKey);

        var floatHeightValue = needFloatHeight ? await ReadFloatHeightAsync(TableEntry, cancellationToken).ConfigureAwait(false) : null;

        if (needFloatHeight && floatHeightValue is null)
        {
            throw new InvalidOperationException(
                $"读取当前浮高（D{TableEntry.CurrentFloatHeightAddress}）失败，已拒绝示教——避免把错误的浮高写进点表。");
        }

        foreach (var columnItem in TableEntry.Column)
        {
            var useCurrentPosition = !string.IsNullOrWhiteSpace(columnItem.PositionTag) && TableEntry.IsAndSaveCoordinate(columnItem.Key);
            var Value = useCurrentPosition ? samplingValue.Take(columnItem.Key) : rowItem.Take(columnItem.Key);

            if (useCurrentPosition
                && floatHeightValue is { } alreadyHasFloatHeight
                && string.Equals(columnItem.Key, floatHeightColumnKey, StringComparison.OrdinalIgnoreCase))
            {
                Value = alreadyHasFloatHeight;
            }

            if (columnItem.DataType != TagDataType.UInt16 && columnItem.DataType != TagDataType.Int16
                && (Value < TableEntry.MinValue || Value > TableEntry.MaxValue))
            {
                throw new InvalidOperationException(
                    $"当前位置 {columnItem.DisplayName} = {Value:0.00} 超出允许范围"
                    + $"（{TableEntry.MinValue:0.00} ~ {TableEntry.MaxValue:0.00}），已拒绝示教。");
            }

            rowItem.setup(columnItem.Key, Value);
        }

        await WriteSinglePointAsync(TableEntry, mapItem, Sequence, cancellationToken).ConfigureAwait(false);

        // 不带落盘 point-tables.json（点表整段属于机种），但必须把这个点并入机种快照：
        // 否则"示教完切页再回来"同样会看到旧值（与下发同一条数据链）
        await AndInMachineModelSnapshotAsync(TableEntry, [Sequence], cancellationToken).ConfigureAwait(false);

        _log.LogInformation("{Table} 已示教第 {Point} 点并保存到本机种", TableEntry.DisplayName, Sequence + 1);
        pointTableChanged?.Invoke(this, TableEntry.Key);
    }

    /// <inheritdoc />
    public async Task WritePointTableAsync(string Key, CancellationToken cancellationToken = default)
    {
        _permission.WantRequire(Permission.EditTeach);

        var TableEntry = Table(Key);
        var mapItem = _map[TableEntry.Key];

        if (!_client.IsConnected)
        {
            throw new InvalidOperationException("PLC 未连接，无法下发点表。");
        }

        // 下发前逐点校验：未示教的点缓存的是 0，直接写会把轴驱动到原点
        var Invalid = new List<string>();

        for (var Sequence = 0; Sequence < mapItem.PointCount; Sequence++)
        {
            var rowItem = _bufferStore[TableEntry.Key][Sequence];

            foreach (var columnItem in TableEntry.CoordinateColumn)
            {
                var Value = rowItem.Take(columnItem.Key);

                if (Value < TableEntry.MinValue || Value > TableEntry.MaxValue)
                {
                    Invalid.Add($"第 {Sequence + 1:00} 点 {columnItem.DisplayName}（{Value:0.##}）");
                    break;
                }
            }
        }

        if (Invalid.Count > 0)
        {
            throw new InvalidOperationException(
                $"点表「{TableEntry.DisplayName}」有 {Invalid.Count} 个点超出坐标范围"
                + $"（{TableEntry.MinValue:0.00} ~ {TableEntry.MaxValue:0.00}），已拒绝下发。请先完成示教：\n"
                + string.Join("\n", Invalid.Take(5))
                + (Invalid.Count > 5 ? $"\n……共 {Invalid.Count} 个" : string.Empty));
        }

        foreach (var columnItem in TableEntry.Column)
        {
            var BufferArea = new ushort[mapItem.ColumnBlockWord(columnItem.Key)];

            for (var Sequence = 0; Sequence < mapItem.PointCount; Sequence++)
            {
                var registerList = RegisterCodec.Encode(
                    columnItem.DataType, _bufferStore[TableEntry.Key][Sequence].Take(columnItem.Key), WordOrder.LittleEndian);

                var OffsetValue = Sequence * columnItem.WordPerPointBit;
                Array.Copy(registerList, 0, BufferArea, OffsetValue, columnItem.WordPerPointBit);
            }

            await _client
                .WriteMultiRegisterAsync(columnItem.BaseAddress, BufferArea, cancellationToken)
                .ConfigureAwait(false);
        }

        // 写 PLC 成功后并入本机种快照：页面重新进入是从"机种快照"装载的，
        // 不并入就会"下发完切页再回来数据不见了"（2026-10-16 现场问题）
        await AndInMachineModelSnapshotAsync(TableEntry, Enumerable.Range(0, mapItem.PointCount).ToList(), cancellationToken)
            .ConfigureAwait(false);

        _log.LogInformation("已下发点表 {Table}（{Count} 点）并保存到本机种", TableEntry.DisplayName, mapItem.PointCount);
    }

    /// <summary>写单个点的全部列（每列一次写，占 1–4 字）。</summary>
    private async Task WriteSinglePointAsync(
        PointTableDefinition TableEntry, PointTableAddressMap mapItem, int Sequence, CancellationToken cancellationToken)
    {
        if (!_client.IsConnected)
        {
            throw new InvalidOperationException("PLC 未连接，无法写入示教点。");
        }

        foreach (var columnItem in TableEntry.Column)
        {
            var registerList = RegisterCodec.Encode(
                columnItem.DataType, _bufferStore[TableEntry.Key][Sequence].Take(columnItem.Key), WordOrder.LittleEndian);

            var addressValue = mapItem.Address(Sequence, columnItem.Key);

            if (registerList.Length == 1)
            {
                await _client.WriteSingleRegisterAsync(addressValue, registerList[0], cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await _client.WriteMultiRegisterAsync(addressValue, registerList, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    // ── 内部 ────────────────────────────────────────────────────────────

    private static List<PointValueRow> AllEmptyLine(PointTableDefinition TableEntry)
    {
        var list = new List<PointValueRow>(TableEntry.PointCount);

        for (var Sequence = 0; Sequence < TableEntry.PointCount; Sequence++)
        {
            list.Add(new PointValueRow { Sequence = Sequence });
        }

        return list;
    }
}
