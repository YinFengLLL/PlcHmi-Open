using System.Text.Json;
using Microsoft.Extensions.Logging;
using PlcHmi.Business.Configuration;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// 参数页自定义表格服务（延时表 / 按钮表）。
///
/// <para><b>地址约束：</b>延时表绑 D 区（数值）、按钮表绑 M 区（命令位）。
/// 按钮只操作内部继电器，由 PLC 决定何时驱动实际输出。</para>
/// </summary>
public sealed partial class ParameterTableService : IParameterTableService
{
    /// <summary>延时表允许的软元件区。</summary>
    private static readonly SoftElement[] delayElement = [SoftElement.D];

    /// <summary>按钮表允许的软元件区。</summary>
    private static readonly SoftElement[] buttonElement = [SoftElement.M];

    /// <summary>
    /// 按钮表允许的 M 区地址范围（含两端）。
    ///
    /// <para>M5000 以下已被系统占用（M200–M203 运行命令、M210 手动使能、M220–M225 气缸、
    /// M500–M600 报警位），所以自定义按钮统一放在 <b>M5000–M5099</b>，
    /// 与系统点位彻底隔开，避免管理员加一个开关就撞掉某个功能。</para>
    /// </summary>
    public const int buttonAddressMin = 5000;

    /// <inheritdoc cref="buttonAddressMin" />
    public const int buttonAddressMax = 5099;

    /// <summary>
    /// 延时表允许的 D 区地址范围（含两端）。
    ///
    /// <para><b>地址规划（2026-09-12 需求方确认）</b>：D1000–D1099 为<b>延时表专属区</b>；
    /// D1100–D1500 为上位机预留段（用途待定）。原先把 D1000–D1500 记为
    /// 「PLC 公共区、禁止占用」的说法已作废——PLC 侧已释放整段给上位机使用。</para>
    ///
    /// <para>把延时锁进专属区的原因与按钮表相同：延时条目可以由管理员增删改地址，
    /// 不设边界就可能撞掉别的功能（轴速度参数区、示教点表区都在同一片 D 区里）。</para>
    ///
    /// <para><b>2026-10-16：</b>延时改为 32 位（DINT，占 2 字），故上限收到 <b>1098</b>——
    /// 否则最后一条会跨到 D1100，侵入上位机预留段。专属区因此可放 50 条延时。</para>
    /// </summary>
    public const int delayAddressMin = 1000;

    /// <inheritdoc cref="delayAddressMin" />
    public const int delayAddressMax = 1098;

    private readonly IAddressParser _parser;
    private readonly TagWriter _writer;
    private readonly ILogger<ParameterTableService> _log;

    private readonly List<ParameterEntryDefinition> _delay = new();
    private readonly List<ParameterEntryDefinition> _button = new();
    private readonly IConfigHealth? _health;
    private readonly IVariableTableService? _variableTable;

    /// <summary>服务层权限守卫。增删改条目属管理员专有（改的是"表结构"，不是数值）。</summary>
    private readonly IPermissionGuard _permission;

    /// <summary>防止下一次保存时把"加载期间"的默认值又写一遍。</summary>
    private bool _hasUnsavedChangeMove;

    public ParameterTableService(
        IAddressParser Parser, TagWriter Writer, ILogger<ParameterTableService> Log,
        IPermissionGuard Permission, IConfigHealth? Health = null, IVariableTableService? VariableTable = null)
    {
        _parser = Parser;
        _writer = Writer;
        _log = Log;
        _permission = Permission;
        _health = Health;
        _variableTable = VariableTable;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 返回<b>快照</b>而不是内部列表引用：此前交出内部 List，
    /// 调用方既可以直接 Add 绕过权限校验，也会在枚举时撞上 UI 线程的增删
    /// （Collection was modified）。
    /// </remarks>
    public IReadOnlyList<ParameterEntryDefinition> DelayCollection => _delay.ToArray();

    /// <inheritdoc cref="DelayCollection" />
    public IReadOnlyList<ParameterEntryDefinition> ButtonCollection => _button.ToArray();

    /// <inheritdoc />
    public event EventHandler? entryChanged;

    /// <inheritdoc />
    public IReadOnlyList<ParameterEntryDefinition> Get(ParameterTableCategory Category) =>
        Category == ParameterTableCategory.Delay ? _delay.ToArray() : _button.ToArray();

    // ── 加载与保存 ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var filePath = ConfigPath.ParameterTable;

        var resultValue = await ConfigFileLoader
            .LoadJsonAsync<ParameterTableConfig>(filePath, JsonConfig.Read, _log, cancellationToken)
            .ConfigureAwait(false);

        _health.Report("参数表（延时/按钮）", resultValue);

        if (resultValue.loadResult is LoadResultSplitCategory.Missing)
        {
            _log.LogInformation("未找到参数表配置，写入一份默认配置：{Path}", filePath);

            _delay.Clear();
            _delay.AddRange(ParameterTableDefaultValue.CreateDelay(delayAddressMin));

            _button.Clear();
            _button.AddRange(ParameterTableDefaultValue.CreateButton());

            // 首次运行写默认配置：走核心保存（启动阶段尚无登录身份，
            // 不能因为有权限校验而让初始化失败）
            await SaveCoreAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        _delay.Clear();
        _delay.AddRange(resultValue.Value?.DelayCollection ?? new List<ParameterEntryDefinition>());

        _button.Clear();
        _button.AddRange(resultValue.Value?.ButtonCollection ?? new List<ParameterEntryDefinition>());

        SortAll();
        ApplyVariableBinding();
        CheckDelayAddressOverlap();

        if (resultValue.loadResult is LoadResultSplitCategory.Loaded)
        {
            _log.LogInformation("已加载参数表：延时 {D} 条、按钮 {B} 条", _delay.Count, _button.Count);
        }

        entryChanged?.Invoke(this, EventArgs.Empty);
    }

    // 延时地址重叠的启动期检查见 ParamTableService.Validate.cs（同为校验职责）。

    /// <summary>变量绑定回填：绑定了变量的条目用变量表中的软元件地址刷新地址字段。</summary>
    private void ApplyVariableBinding()
    {
        VariableBindingBackfill.ApplyEntryBinding(_delay, "延时", _variableTable, _parser, _health);
        VariableBindingBackfill.ApplyEntryBinding(_button, "按钮", _variableTable, _parser, _health);
    }

    /// <inheritdoc />
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        // 保存的是"表结构"（地址 / 范围 / 条目集合），与增删改同权限：
        // 界面隐藏按钮只是防误触，边界在服务层（R5）
        _permission.WantRequire(Permission.EditBindAddress);

        await SaveCoreAsync(cancellationToken).ConfigureAwait(false);
        entryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 落盘但<b>不</b>通知界面。
    /// <para>实时下发延时后由 <see cref="WriteDelayAsync"/> 调用：
    /// 若走 <see cref="SaveAsync"/>，<c>ItemsChanged</c> 会重建整张表格，
    /// 打断用户在其他行正在进行的编辑。</para>
    /// </summary>
    private async Task SaveCoreAsync(CancellationToken cancellationToken)
    {
        SortAll();

        var configPairObject = new ParameterTableConfig
        {
            DelayCollection = _delay.Select(EntryItem => EntryItem.Clone()).ToList(),
            ButtonCollection = _button.Select(item2 => item2.Clone()).ToList(),
        };

        try
        {
            var jsonText = JsonSerializer.Serialize(configPairObject, JsonConfig.Write);
            await AtomicFile.WriteAllTextAsync(ConfigPath.ParameterTable, jsonText, cancellationToken).ConfigureAwait(false);

            _hasUnsavedChangeMove = false;
            _log.LogInformation("参数表已保存：延时 {D} 条、按钮 {B} 条", _delay.Count, _button.Count);
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "保存参数表失败：{Path}", ConfigPath.ParameterTable);
            throw;
        }
    }

    // ── 校验见 ParamTableService.Validate.cs（partial）──────────────────

    // ── 增删改 ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    public void Add(ParameterTableCategory Category, ParameterEntryDefinition Entry)
    {
        ArgumentNullException.ThrowIfNull(Entry);

        // 角色校验在参数校验之前：被拒绝的调用不留任何状态痕迹
        _permission.WantRequire(Permission.EditBindAddress);

        var list = Change(Category);

        if (string.IsNullOrWhiteSpace(Entry.Id))
        {
            Entry.Id = NewId(Category);
        }

        if (Entry.Order <= 0)
        {
            Entry.Order = list.Count == 0 ? 1 : list.Max(coordinateX => coordinateX.Order) + 1;
        }

        list.Add(Entry.Clone());
        SortAll();
        _hasUnsavedChangeMove = true;

        _log.LogInformation("已新增{Kind}条目：{Name}", Category == ParameterTableCategory.Delay ? "延时" : "按钮", Entry.Name);
        entryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Update(ParameterTableCategory Category, ParameterEntryDefinition Entry)
    {
        ArgumentNullException.ThrowIfNull(Entry);

        _permission.WantRequire(Permission.EditBindAddress);

        var list = Change(Category);
        var Sequence = list.FindIndex(coordinateX => string.Equals(coordinateX.Id, Entry.Id, StringComparison.OrdinalIgnoreCase));

        if (Sequence < 0)
        {
            throw new InvalidOperationException($"找不到编号为「{Entry.Id}」的条目。");
        }

        list[Sequence] = Entry.Clone();
        SortAll();
        _hasUnsavedChangeMove = true;

        _log.LogInformation("已更新{Kind}条目：{Name}", Category == ParameterTableCategory.Delay ? "延时" : "按钮", Entry.Name);
        entryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Remove(ParameterTableCategory Category, string Id)
    {
        _permission.WantRequire(Permission.EditBindAddress);

        var alreadyRemove = Change(Category).RemoveAll(coordinateX =>
            string.Equals(coordinateX.Id, Id, StringComparison.OrdinalIgnoreCase));

        if (alreadyRemove > 0)
        {
            _hasUnsavedChangeMove = true;
            _log.LogInformation("已删除参数表条目：{Id}", Id);
            entryChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public string NewId(ParameterTableCategory Category)
    {
        var prefix = Category == ParameterTableCategory.Delay ? "delay" : "btn";
        var list = Get(Category);

        for (var Entry = 1; Entry < 1000; Entry++)
        {
            var candidate = $"{prefix}-{Entry}";

            if (list.All(coordinateX => !string.Equals(coordinateX.Id, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }

        return $"{prefix}-{Guid.NewGuid():N}"[..12];
    }

    // ── 按钮置位 / 复位 ─────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task SetButtonAsync(
        ParameterEntryDefinition Entry, bool on, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(Entry);

        // 置位/复位改的是设备状态，工程师及以上即可（不是改表结构）
        _permission.WantRequire(Permission.EditParameterValueList);

        // 服务层再校验一次：即使配置文件被手工改错，也不会写错区。
        // 界面上编辑器只让填 M 区不够——手工编辑 JSON 同样要被拦住。
        // isUpdate: true —— 按钮是已存在的条目，同 Id 就是它自己，不能报「已被占用」。
        var validateItem = Validate(ParameterTableCategory.Button, Entry, isUpdate: true);

        if (!validateItem.IsValid)
        {
            throw new InvalidOperationException(validateItem.Summary);
        }

        await _writer.WriteCoilAddressAsync(Entry.Address, on, cancellationToken).ConfigureAwait(false);

        _log.LogInformation("按钮「{Name}」({Address}) 已{Action}",
            Entry.Name, Entry.Address, on ? "置位" : "复位");
    }

    /// <inheritdoc />
    public async Task WriteDelayAsync(
        ParameterEntryDefinition Entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(Entry);

        _permission.WantRequire(Permission.EditParameterValueList);

        // 服务层再校验一次：即使配置文件被手工改错，也不会写错区、写出范围。
        // isUpdate: true —— 延时是已存在的条目，同 Id 就是它自己，不能报「已被占用」。
        var validateItem = Validate(ParameterTableCategory.Delay, Entry, isUpdate: true);

        if (!validateItem.IsValid)
        {
            throw new InvalidOperationException(validateItem.Summary);
        }

        // 界面与配置按秒，PLC 存 <b>32 位整数毫秒（DINT）</b>：0.500 S → 500（2026-10-16 现场确认，
        // 此前是 16 位毫秒，上限只有 65.535 S，等几十秒的工位延时根本不够用）。
        // 必须先在 double 域判定范围再转 int：条目.值 是 double 且校验只保证
        // 最小 < 值 < 最大（最大本身可以是任意大的 double），先乘后转会让
        // 超大值在 unchecked 下回绕成小正数，静默写错延时
        var millisecondsValue = Entry.Value * 1000;

        if (millisecondsValue is < 0 or > int.MaxValue)
        {
            throw new InvalidOperationException(
                $"延时 {Entry.Value:0.###} S 换算为 {millisecondsValue:0} ms，超出 32 位整数（DINT）范围（0–{int.MaxValue}）。");
        }

        var milliseconds = (int)Math.Round(millisecondsValue, MidpointRounding.AwayFromZero);

        await _writer.WriteRegisterAddress32Async(Entry.Address, milliseconds, cancellationToken)
            .ConfigureAwait(false);

        // 静默落盘：保证「PLC 里的值 = 配置文件里的值」，
        // 否则断电重启后配置退回旧值，与 PLC 不一致。
        await SaveCoreAsync(cancellationToken).ConfigureAwait(false);

        _log.LogInformation("延时「{Name}」({Address}) 已实时下发：{Seconds:0.###} S = {Millis} ms",
            Entry.Name, Entry.Address, Entry.Value, milliseconds);
    }

    // ── 内部 ────────────────────────────────────────────────────────────

    private List<ParameterEntryDefinition> Change(ParameterTableCategory Category) =>
        Category == ParameterTableCategory.Delay ? _delay : _button;

    private void SortAll()
    {
        _delay.Sort((item, item2) => item.Order.CompareTo(item2.Order));
        _button.Sort((item, item2) => item.Order.CompareTo(item2.Order));
    }

    /// <summary>是否有未保存的改动（供界面提示）。</summary>
    public bool HasUnsavedChange => _hasUnsavedChangeMove;
}
