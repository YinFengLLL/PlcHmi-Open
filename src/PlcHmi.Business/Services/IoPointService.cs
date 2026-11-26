using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PlcHmi.Business.Configuration;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// IO 点位服务。管理 X / Y 点位表及实时状态。
///
/// <para><b>设计要点：</b>每个 IO 点的“含义”由工程师在界面上填写并保存回 JSON，
/// 程序本身不需要知道 “X0 是什么”。因此换设备时零改代码。</para>
/// </summary>
public sealed class IoPointService : IIoPointService
{
    private readonly ILogger<IoPointService> _log;
    private readonly IConfigHealth? _health;
    private readonly IPermissionGuard _permission;
    private readonly ConcurrentDictionary<string, IoPointState> _state = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 代码 → 点位定义索引。轮询热路径每个 IO 点每周期都要反查定义
    /// （是否取反），线性搜索使整个快组退化为 O(n²)/周期；改为加载与
    /// 新增一栏时整体重建（引用原子替换），查找 O(1)。
    /// </summary>
    private Dictionary<string, IoPoint> _pointIndex = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 权限守卫是<b>必填</b>参数，刻意不给默认值：本项目审查标准 R5 要求写操作在服务层校验，
    /// 而"缺少守卫时默认放行"正是该规则要拦的失效模式（界面隐藏按钮只是防误触，不是边界）。
    /// 测试若不需要覆盖权限分支，请显式传入 <see cref="AllowAllPermissionGuard"/>。
    /// </summary>
    public IoPointService(IPermissionGuard Permission, ILogger<IoPointService> Log, IConfigHealth? Health = null)
    {
        ArgumentNullException.ThrowIfNull(Permission);

        _permission = Permission;
        _log = Log;
        _health = Health;
        DataGrid = new IoPointGrid();
        Load();
    }

    /// <inheritdoc />
    public IoPointGrid DataGrid { get; private set; }

    /// <inheritdoc />
    public event EventHandler<IoPointState>? StateChanged;

    /// <summary>未命中时的空状态（静态单例：每秒数百次未命中不该每次 new 一个对象）。</summary>
    private static readonly IoPointState emptyState = new(string.Empty, false, DateTime.MinValue);

    /// <inheritdoc />
    public IoPointState GetState(string CodeRawValue) =>
        _state.TryGetValue(CodeRawValue, out var StateValue) ? StateValue : emptyState;

    /// <inheritdoc />
    public void UpdateState(string CodeRawValue, bool SourceValue)
    {
        var PointItem = FindPoint(CodeRawValue);
        var logic = PointItem?.Negate == true ? !SourceValue : SourceValue;

        // 2026-11-19 性能改造：把"取时间 + 写字典"降到只做一次，事件只在真实翻转时抛。
        // 注意【时间戳必须每次刷新】：现场与回归用例都用"时间戳是否随周期推进"
        // 来证明该点仍被采样（IO 位区回退测试就卡在这条上），跳过它会变成功能退化。
        var exists = _state.TryGetValue(CodeRawValue, out var old);
        var alreadyToggle = !exists || old.IsOpenStart != logic;

        var newStateValue = new IoPointState(CodeRawValue, logic, DateTime.Now);
        _state[CodeRawValue] = newStateValue;

        if (alreadyToggle)
        {
            StateChanged?.Invoke(this, newStateValue);
        }
    }

    /// <inheritdoc />
    public void UpdateMeaning(string CodeRawValue, string Meaning)
    {
        // R5：改的是会被 异步保存 落盘的数据，与保存同权限
        _permission.WantRequire(Permission.EditIoPoint);

        var PointItem = FindPoint(CodeRawValue);
        if (PointItem is null)
        {
            _log.LogWarning("尝试修改不存在的 IO 点含义：{Code}", CodeRawValue);
            return;
        }

        PointItem.Meaning = Meaning;
        _log.LogDebug("已更新 {Code} 含义为 \"{Meaning}\"", CodeRawValue, Meaning);
    }

    /// <inheritdoc />
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        // R5：写操作在服务层二次校验。异步保存 会整体改写 io-points.json
        // （含"含义"字段），与 异步新增一栏 同为管理员专有（权限.编辑IO点位）。
        // 此前只校验了新增一栏，直接调保存即可绕过——界面门槛不是边界。
        _permission.WantRequire(Permission.EditIoPoint);

        try
        {
            var jsonText = JsonSerializer.Serialize(DataGrid, JsonConfig.Write);
            await AtomicFile.WriteAllTextAsync(ConfigPath.IoPointCollection, jsonText, cancellationToken).ConfigureAwait(false);
            _log.LogInformation("IO 点位表已保存到 {Path}", ConfigPath.IoPointCollection);
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "保存 IO 点位表失败");
            throw;
        }
    }

    /// <inheritdoc />
    public Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        Load();
        return Task.CompletedTask;
    }

    // ── 新增一栏（v2.1）────────────────────────────────────────────────
    //
    // 汇川 X / Y 的编址上限。这个数与 汇川地址解析器 里软元件规格的 最大序号
    // 是同一个事实的两份描述（X 基址 0xF800、Y 基址 0xFC00，序号上限 1023，
    // 即八进制 1777）。解析器没有把这个数暴露成公共常量，这里只能各自持有一份；
    // 若要改，两处必须一起改 —— 超限的点会在轮询编译时被静默跳过，很难查。
    private const int maxOctalIndex = 1023;

    /// <inheritdoc />
    public string PreviewNextRow(string prefix)
    {
        var (sideList, prefixSpec) = GetSide(prefix);
        var startPoint = CalculateNextRowStartPoint(sideList);

        return $"{prefixSpec}{Convert.ToString(startPoint, 8)} — " +
               $"{prefixSpec}{Convert.ToString(startPoint + GroupSize() - 1, 8)}";
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IoPoint>> AddNewRowAsync(string prefix, CancellationToken cancellationToken = default)
    {
        // 权限先于一切：越权调用不应留下任何状态，也不该看到参数校验的细节。
        // 新增一栏会改写 io-points.json 并改变采集范围，属管理员专有（权限.编辑IO点位）。
        _permission.WantRequire(Permission.EditIoPoint);

        var (sideList, prefixSpec) = GetSide(prefix);
        var perGroupPointCount = GroupSize();
        var startPoint = CalculateNextRowStartPoint(sideList);

        var NewItemList = new List<IoPoint>(perGroupPointCount);
        var bothHasNumber = sideList
            .Select(PointItem => PointItem.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var Sequence = startPoint; Sequence < startPoint + perGroupPointCount; Sequence++)
        {
            if (Sequence > maxOctalIndex)
            {
                // 先整栏校验再写入：不做"加到一半失败"的部分写入，
                // 否则配置里会留下一截半栏，界面和轮询各看到一半。
                throw new InvalidOperationException(
                    $"{prefixSpec} 区编号已到上限（八进制 {Convert.ToString(maxOctalIndex, 8)}），无法再新增一栏。");
            }

            var CodeRawValue = prefixSpec + Convert.ToString(Sequence, 8);

            if (!bothHasNumber.Add(CodeRawValue))
            {
                throw new InvalidOperationException($"点位 {CodeRawValue} 已存在，无法新增。");
            }

            NewItemList.Add(new IoPoint { Code = CodeRawValue, Meaning = string.Empty, Enabled = true });
        }

        sideList.AddRange(NewItemList);

        // 新点也要能被 查找点位 反查到（轮询热路径走索引）
        RebuildPointIndex();

        // 元数据里的数量字段虽然目前只有 JSON 记录作用，也应保持一致，
        // 否则现场工程师对照 config 会看到两套对不上的数字。
        if (IsInputSide(prefixSpec))
        {
            DataGrid.Element.InputCount = sideList.Count;
        }
        else
        {
            DataGrid.Element.OutputCount = sideList.Count;
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);

        _log.LogInformation("已新增 {Prefix} 区一栏 {First} — {Last}（{Count} 点），点位总数 {Total}",
            prefixSpec, NewItemList[0].Code, NewItemList[^1].Code, NewItemList.Count, sideList.Count);

        return NewItemList;
    }

    /// <summary>取某一侧的点位列表与规范前缀（大写 X / Y）。</summary>
    private (List<IoPoint> PointItemList, string prefixSpec) GetSide(string prefix) =>
        IsInputSide(prefix) ? (DataGrid.Input, "X") : (DataGrid.Output, "Y");

    private static bool IsInputSide(string prefix) =>
        string.Equals(prefix, "X", StringComparison.OrdinalIgnoreCase);

    private int GroupSize() =>
        DataGrid.Element.GroupSize > 0 ? DataGrid.Element.GroupSize : 8;

    /// <summary>
    /// 下一栏的起始序号：取该侧最大的八进制序号，向上取整到下一个分组边界。
    /// <para>空表从 0 开始；已有 X0–X27（最大序号 23）则下一栏起点为 24，即八进制 30。</para>
    /// </summary>
    private int CalculateNextRowStartPoint(IReadOnlyList<IoPoint> PointItemList)
    {
        var perGroupPointCount = GroupSize();
        var maxIndex = -1;

        foreach (var PointItem in PointItemList)
        {
            var numberList = new string(PointItem.Code.SkipWhile(char.IsLetter).ToArray());
            var indexValue = ManualParseOctal(numberList, PointItem.Code);

            if (indexValue > maxIndex)
            {
                maxIndex = indexValue;
            }
        }

        if (maxIndex < 0)
        {
            return 0;
        }

        return (maxIndex / perGroupPointCount + 1) * perGroupPointCount;
    }

    private static int ManualParseOctal(string numberList, string CodeRawValue)
    {
        var Value = 0;

        foreach (var channel in numberList)
        {
            var numberItem = channel - '0';

            if (numberItem is < 0 or > 7)
            {
                throw new InvalidOperationException(
                    $"点位 {CodeRawValue} 的编号「{numberList}」不是合法的八进制，无法计算下一栏起点。");
            }

            Value = Value * 8 + numberItem;
        }

        return Value;
    }

    /// <summary>查找 IO 点定义。</summary>
    public IoPoint? FindPoint(string CodeRawValue) =>
        _pointIndex.TryGetValue(CodeRawValue, out var PointItem) ? PointItem : null;

    /// <summary>把点位按八进制分组（每 8 点一组），便于界面按组排布。</summary>
    public static IEnumerable<IGrouping<string, IoPoint>> ByOctalGroup(IReadOnlyList<IoPoint> PointItemList)
    {
        // X0-X7、X10-X17……按“前缀 + 高位数字”分组
        return PointItemList.GroupBy(pointEntry =>
        {
            var numberList = new string(pointEntry.Code.SkipWhile(char.IsLetter).ToArray());
            if (numberList.Length <= 1)
            {
                return "0";
            }

            return numberList[..^1];
        });
    }

    /// <summary>
    /// 整体重建代码 → 点位索引（加载 / 新增一栏时调用）。
    /// <para>先加输入侧再加输出侧（TryAdd 保持输入侧优先，与旧版
    /// FirstOrDefault 先查输入的语义一致）；整表替换是原子引用赋值，
    /// 轮询线程无需加锁。</para>
    /// </summary>
    private void RebuildPointIndex()
    {
        var index = new Dictionary<string, IoPoint>(
            DataGrid.Input.Count + DataGrid.Output.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var PointItem in DataGrid.Input.Concat(DataGrid.Output))
        {
            index.TryAdd(PointItem.Code, PointItem);
        }

        _pointIndex = index;
    }

    private void Load()
    {
        var resultValue = ConfigFileLoader.LoadJson<IoPointGrid>(
            ConfigPath.IoPointCollection, JsonConfig.Read, _log);

        _health.Report("IO 点位表", resultValue);

        if (resultValue.Value is null)
        {
            return;
        }

        DataGrid = resultValue.Value;
        _state.Clear();
        RebuildPointIndex();

        if (resultValue.loadResult is LoadResultSplitCategory.Loaded)
        {
            _log.LogInformation("已加载 IO 点位表：X {InputCount} 点、Y {OutputCount} 点（机型 {Model}）",
                DataGrid.Input.Count, DataGrid.Output.Count, DataGrid.Element.PlcModel);
        }
    }
}
