using System.Text.Json;
using Microsoft.Extensions.Logging;
using PlcHmi.Business.Configuration;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// 报警定义服务。加载与保存 <c>config/alarms.json</c>。
///
/// <para><b>报警地址只允许落在这两段（需求方划定）：</b></para>
/// <list type="bullet">
///   <item><b>D550 – D554</b> 的位：如 <c>D550.1</c>、<c>D554.2</c>。5 个字 × 16 位 = 80 个报警位。</item>
///   <item><b>M500 – M600</b>：101 个位地址，每个地址一条报警。</item>
/// </list>
///
/// <para>管理员自定义时也只能选这段区域，且<b>最多 100 条</b>报警，每条可单独启用 / 停用。</para>
/// </summary>
public sealed class AlarmDefinitionService : IAlarmDefinitionService
{
    /// <summary>报警可用的 D 区范围（含）。</summary>
    public const int alarmDLowerLimit = 550;

    /// <summary>报警可用的 D 区上限（含）。</summary>
    public const int alarmDUpperLimit = 554;

    /// <summary>报警可用的 M 区范围（含）。</summary>
    public const int alarmMLowerLimit = 500;

    /// <summary>报警可用的 M 区上限（含）。</summary>
    public const int alarmMUpperLimit = 600;

    /// <summary>报警条数上限。</summary>
    public const int maxAlarmCount = 100;

    private readonly IAddressParser _parser;
    private readonly ILogger<AlarmDefinitionService> _log;
    private readonly IPermissionGuard _permission;
    private readonly IConfigHealth? _health;
    private readonly List<AlarmDefinition> _definition = new();

    public AlarmDefinitionService(
        IAddressParser Parser, ILogger<AlarmDefinitionService> Log,
        IPermissionGuard Permission, IConfigHealth? Health = null)
    {
        _parser = Parser;
        _log = Log;
        _permission = Permission;
        _health = Health;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 返回<b>快照</b>而不是内部列表引用：报警扫描服务每 500ms 在后台线程枚举它，
    /// 而界面线程会同时增删改；此前交出内部 List，两个线程一撞就是
    /// <c>Collection was modified</c>——异常被扫描循环吞掉，表现为"改定义的瞬间
    /// 一整轮扫描静默丢失"。快照同时堵住"直接 Add 后调保存"绕过权限的路径。
    /// </remarks>
    public IReadOnlyList<AlarmDefinition> DefinitionCollection => _definition.ToArray();

    /// <inheritdoc />
    public event EventHandler? definitionChanged;

    // ── 加载与保存 ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var filePath = ConfigPath.AlarmCollection;

        var resultValue = await ConfigFileLoader
            .LoadJsonAsync<AlarmConfig>(filePath, JsonConfig.Read, _log, cancellationToken)
            .ConfigureAwait(false);

        // 报警表损坏是**安全级**降级：界面一切正常，但每一个故障位都被无视。
        // 必须登记出去，让操作员看见"报警功能不可信"，而不是安静地显示"无报警"。
        _health.Report("报警配置", resultValue);

        if (resultValue.loadResult is LoadResultSplitCategory.Missing)
        {
            _log.LogInformation("未找到报警配置，写入一份默认配置：{Path}", filePath);

            _definition.Clear();
            _definition.AddRange(CreateDefaultValue());

            // 首次运行写默认配置：启动阶段尚无登录身份，走核心保存
            await SaveCoreAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        _definition.Clear();
        _definition.AddRange(resultValue.Value?.AlarmCollection ?? new List<AlarmDefinition>());

        if (resultValue.loadResult is LoadResultSplitCategory.Loaded)
        {
            _log.LogInformation("已加载 {Count} 条报警定义", _definition.Count);
        }

        definitionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        // 保存即"把当前定义整体落盘"，与新增/修改/移除同权限：
        // 否则直接改内部列表再调保存就能绕过校验（界面门槛见 可编辑报警）
        _permission.WantRequire(Permission.EditBindAddress);

        await SaveCoreAsync(cancellationToken).ConfigureAwait(false);
        definitionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>落盘但不通知界面（初始化 / 已有校验的写路径内部使用）。</summary>
    private async Task SaveCoreAsync(CancellationToken cancellationToken)
    {
        var configPairObject = new AlarmConfig { AlarmCollection = _definition.Select(EntryItem => EntryItem.Clone()).ToList() };
        var jsonText = JsonSerializer.Serialize(configPairObject, JsonConfig.Write);

        try
        {
            await AtomicFile.WriteAllTextAsync(ConfigPath.AlarmCollection, jsonText, cancellationToken).ConfigureAwait(false);
            _log.LogInformation("报警配置已保存（{Count} 条）：{Path}", _definition.Count, ConfigPath.AlarmCollection);
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "保存报警配置失败：{Path}", ConfigPath.AlarmCollection);
            throw;
        }
    }

    // ── 校验 ────────────────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// <b>占用检查为什么不能靠引用排除自身：</b>编辑对话框操作的是 <see cref="AlarmDefinition.Clone"/> 副本，
    /// <c>ReferenceEquals(d, definition)</c> 对副本永远不成立——旧实现因此把「正在编辑的那条」
    /// 也当成冲突来源，管理员只要改个名字就会弹「已被占用」（2026-09-11 需求方反馈）。
    /// 现按 <paramref name="isUpdate"/> 区分：编辑时编号被对话框锁定，同 Id 的条目就是自身，直接跳过。
    /// </remarks>
    public AlarmDefinitionValidate Validate(AlarmDefinition DefinitionItem, bool isUpdate = false)
    {
        var errorList = new List<string>();

        if (string.IsNullOrWhiteSpace(DefinitionItem.Id))
        {
            errorList.Add("报警编号不能为空。");
        }
        else if (!isUpdate && _definition.Any(EntryItem =>
                     string.Equals(EntryItem.Id, DefinitionItem.Id, StringComparison.OrdinalIgnoreCase)))
        {
            errorList.Add($"报警编号「{DefinitionItem.Id}」已被占用。");
        }

        if (string.IsNullOrWhiteSpace(DefinitionItem.Message))
        {
            errorList.Add("报警文本不能为空。");
        }

        if (DefinitionItem.Bit is < -1 or > 15)
        {
            errorList.Add("位号必须填 0–15，或填 -1。");
        }

        ValidateAddress(DefinitionItem, errorList);

        // 新增时检查总数上限（更新已有条目不占新名额）
        if (!isUpdate && _definition.Count >= maxAlarmCount)
        {
            errorList.Add($"报警数量已达上限 {maxAlarmCount} 条，请先删除或停用不用的报警。");
        }

        return new AlarmDefinitionValidate { IsValid = errorList.Count == 0, ErrorCollection = errorList };
    }

    /// <summary>
    /// 校验报警地址是否落在允许区域：
    /// D550–D554 的位，或 M500–M600。
    /// </summary>
    private void ValidateAddress(AlarmDefinition DefinitionItem, List<string> errorList)
    {
        if (string.IsNullOrWhiteSpace(DefinitionItem.Address))
        {
            errorList.Add("读取地址不能为空。");
            return;
        }

        if (!_parser.TryParse(DefinitionItem.Address, out var PlcAddress) || PlcAddress is null)
        {
            errorList.Add($"「{DefinitionItem.Address}」不是有效的软元件地址。");
            return;
        }

        var Sequence = PlcAddress.LogicIndex;

        switch (PlcAddress.ElementItem)
        {
            case SoftElement.D when Sequence >= alarmDLowerLimit && Sequence <= alarmDUpperLimit:
                // D 区一个字有 16 位，必须指定位号才有意义
                if (!DefinitionItem.IsBitMode)
                {
                    errorList.Add($"D 区报警必须指定位号（如 D{Sequence}.1），当前未指定位号。");
                }

                break;

            case SoftElement.M when Sequence >= alarmMLowerLimit && Sequence <= alarmMUpperLimit:
                break;

            default:
                errorList.Add(
                    $"报警地址必须落在 D{alarmDLowerLimit}–D{alarmDUpperLimit}（指定位号，如 D550.1）"
                    + $"或 M{alarmMLowerLimit}–M{alarmMUpperLimit} 内，当前填的是 {PlcAddress.ElementItem}{Sequence}。");
                break;
        }
    }

    // ── 增删改 ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    public void Add(AlarmDefinition DefinitionItem)
    {
        ArgumentNullException.ThrowIfNull(DefinitionItem);

        // 改报警定义等于改"设备什么情况下会报警"。删掉一条真实的报警，
        // 操作员就再也看不到那个故障 —— 管理员专有，且必须在此处兜住。
        _permission.WantRequire(Permission.EditBindAddress);

        if (string.IsNullOrWhiteSpace(DefinitionItem.Id))
        {
            DefinitionItem.Id = NewId();
        }

        _definition.Add(DefinitionItem.Clone());

        _log.LogInformation("已新增报警定义：{Id} {Message}", DefinitionItem.Id, DefinitionItem.Message);
        definitionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Update(AlarmDefinition DefinitionItem)
    {
        ArgumentNullException.ThrowIfNull(DefinitionItem);

        _permission.WantRequire(Permission.EditBindAddress);

        var Sequence = _definition.FindIndex(EntryItem =>
            string.Equals(EntryItem.Id, DefinitionItem.Id, StringComparison.OrdinalIgnoreCase));

        if (Sequence < 0)
        {
            throw new InvalidOperationException($"找不到编号为「{DefinitionItem.Id}」的报警定义。");
        }

        _definition[Sequence] = DefinitionItem.Clone();

        _log.LogInformation("已更新报警定义：{Id} {Message}", DefinitionItem.Id, DefinitionItem.Message);
        definitionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Remove(string Id)
    {
        _permission.WantRequire(Permission.EditBindAddress);

        var alreadyRemove = _definition.RemoveAll(EntryItem =>
            string.Equals(EntryItem.Id, Id, StringComparison.OrdinalIgnoreCase));

        if (alreadyRemove > 0)
        {
            _log.LogInformation("已删除报警定义：{Id}", Id);
            definitionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public string NewId()
    {
        for (var Entry = 1; Entry < 1000; Entry++)
        {
            var candidate = $"A{Entry:000}";

            if (_definition.All(EntryItem => !string.Equals(EntryItem.Id, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }

        return $"A{Guid.NewGuid():N}"[..8].ToUpperInvariant();
    }

    /// <summary>首次运行时的默认报警（对应模拟器 M500 起的报警位）。</summary>
    private static IEnumerable<AlarmDefinition> CreateDefaultValue() =>
    [
        new()
        {
            Id = "A001", Level = AlarmLevel.Critical, Message = "设备急停被按下",
            Address = "M500", Bit = -1, Suggestion = "确认现场安全后复位设备",
        },
        new()
        {
            Id = "A002", Level = AlarmLevel.Warning, Message = "上料气缸超时未到位",
            Address = "M501", Bit = -1, Suggestion = "检查气缸磁性开关与气源压力",
        },
        new()
        {
            Id = "A003", Level = AlarmLevel.Warning, Message = "料仓余料不足",
            Address = "M502", Bit = -1, Suggestion = "及时补料",
        },
        new()
        {
            Id = "A004", Level = AlarmLevel.Critical, Message = "安全门被打开",
            Address = "M503", Bit = -1, Suggestion = "确认无人后关闭安全门再复位",
        },
    ];
}
