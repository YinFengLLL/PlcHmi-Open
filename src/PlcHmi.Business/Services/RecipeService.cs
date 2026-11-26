using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using PlcHmi.Communication.Modbus;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// 机种（配方）服务。
///
/// <para><b>数据边界：</b>机种 = D2500–D3999 的寄存器快照（<c>recipes.json</c>）；
/// 区段之外的参数是全局记忆（<c>parameters.json</c>），切机种不触碰。</para>
///
/// <para><b>写入时机硬约束：</b>写 PLC 只允许在——
/// ① 操作员按下「下发到 PLC」/「下发机种」；② 切换机种后自动下发一次区段快照。
/// 输入框改动只改内存，绝不直接落 PLC。</para>
/// </summary>
public sealed partial class RecipeService : IRecipeService
{
    /// <summary>逐项写参数之间的间隔，避免把 PLC 缓冲打满。</summary>
    private static readonly TimeSpan writeInterval = TimeSpan.FromMilliseconds(20);

    /// <summary>参数整数比较容差（参数是 UInt16，但配置里是 double）。</summary>
    private const double parameterTolerance = 0.001;

    /// <summary>
    /// 机种区段单次读写的字数上限。
    /// <para>Modbus 单次最多 125 个寄存器，这里取 100 留余量——
    /// 一段 1001 个字要分 11 次读写，不能一把梭。</para>
    /// </summary>
    private const int sectionBlockSize = 100;

    private readonly IModbusClient _client;
    private readonly RecipeStore _store;
    private readonly ParameterStore _parameterRepository;
    private readonly RecipeConfig _config;
    private readonly ILogger<RecipeService> _log;

    /// <summary>切机种 / 下发参数属工程师及以上：都会真实改变设备的运行数据。</summary>
    private readonly IPermissionGuard _permission;

    private int _currentNumber = 1;

    public RecipeService(
        IModbusClient Client,
        RecipeStore Store,
        ParameterStore ParameterRepository,
        RecipeConfig configPairObject,
        IPermissionGuard Permission,
        ILogger<RecipeService> Log)
    {
        _client = Client;
        _store = Store;
        _parameterRepository = ParameterRepository;
        _config = configPairObject;
        _permission = Permission;
        _log = Log;
    }

    /// <inheritdoc />
    public int RecipeCount => _config.RecipeCount;

    /// <inheritdoc />
    public int CurrentRecipeNumber => _currentNumber;

    /// <inheritdoc />
    public string CurrentRecipeName => _store.Find(_currentNumber)?.Name ?? string.Empty;

    /// <inheritdoc />
    public IReadOnlyList<RecipeParameterSpec> ParameterSpec => _config.Parameter;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, double> CurrentParameter => _parameterRepository.Parameter;

    /// <inheritdoc />
    public ConsistencyValidateReport? LastOnceConsistencyReport { get; private set; }

    /// <inheritdoc />
    public IReadOnlyList<RecipeInfo> RecipeCollection
    {
        get
        {
            var list = new List<RecipeInfo>(_config.RecipeCount);

            for (var Number = 1; Number <= _config.RecipeCount; Number++)
            {
                var data = _store.Find(Number);

                list.Add(new RecipeInfo
                {
                    Number = Number,
                    Name = data?.Name ?? string.Empty,
                    HasData = data is { } EntryItem && !EntryItem.IsEmpty,
                    RegisterCount = data?.SectionSnapshot.Count ?? 0,
                });
            }

            return list;
        }
    }

    /// <inheritdoc />
    public event EventHandler? recipeChanged;

    /// <inheritdoc />
    public event EventHandler? parameterChanged;

    // ── 初始化 ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<ConsistencyValidateReport> InitializeAsync(CancellationToken cancellationToken = default)
    {
        // 1. 机种号：优先取 PLC 实际值（断电重启后它才代表现场真相）。
        // 2026-10-21 起机种区段不参与周期采集，改为开机 / 连上后直接读一次（见 RecipeService.PlcNo.cs）。
        var plcNumber = await ReadPlcMachineModelNumberAsync(cancellationToken).ConfigureAwait(false);
        _currentNumber = plcNumber > 0 ? plcNumber : Math.Max(1, _store.CurrentRecipeNumber);

        // 与存储层同步真相：示教落盘（TeachService.异步落盘）用的是
        // _存储.当前配方号。此处不同步会导致"内存按 PLC 机种号、落盘按
        // 旧本地号"两份真相分裂——示教点被写进错误机种，覆盖其点表。
        _store.SetCurrent(_currentNumber);

        _log.LogInformation("当前机种：{No}（来源：{Source}）",
            _currentNumber, plcNumber > 0 ? "PLC" : "本地文件");

        // 2. 载入该机种的参数与点表
        LoadCurrentRecipe();

        // 3. 全局参数（机种区段之外的 D 区）：从 PLC 读回来刷新 —— PLC 是权威。
        //    M 区不含在内：它由轮询引擎按 tags.json 持续刷新。
        var Report = await FromPlcSyncParameterAsync(cancellationToken).ConfigureAwait(false);
        LastOnceConsistencyReport = Report;

        if (Report.IsConsistent)
        {
            _log.LogInformation("启动同步完成：{Summary}", Report.Summary);
        }
        else
        {
            _log.LogInformation(
                "启动同步：已按 PLC 刷新 {Count} 项全局参数{NewLine}{Details}",
                Report.DifferenceCount, Environment.NewLine, string.Join(Environment.NewLine, Report.Difference));

            // 落盘留底（离线启动时仍能显示上次同步到的值）
            await StoreCurrentThisGround(cancellationToken).ConfigureAwait(false);
            WhenParameterChanged();
        }

        recipeChanged?.Invoke(this, EventArgs.Empty);
        return Report;
    }

    // 切换机种见 RecipeService.Switch.cs（partial）。

    // ── 参数下发 ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task WriteParameterAsync(CancellationToken cancellationToken = default)
    {
        _permission.WantRequire(Permission.EditParameterValueList);

        await WriteParameterCoreAsync(cancellationToken).ConfigureAwait(false);

        // 双写要求：参数写入 PLC 的同时留底到 parameters.json
        await StoreCurrentThisGround(cancellationToken).ConfigureAwait(false);

        _log.LogInformation("全局参数已下发并留底（当前机种 {No}）", _currentNumber);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ReadPlcParameterAsync(CancellationToken cancellationToken = default)
    {
        // 读回会覆盖本地参数值 —— 是写本地数据的操作，不是只读
        _permission.WantRequire(Permission.EditParameterValueList);

        if (!_client.IsConnected)
        {
            throw new InvalidOperationException("PLC 未连接，无法读取参数。");
        }

        var Change = new List<string>();
        var errorList = new List<string>();
        var decodedValueList = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var specItem in _config.Parameter)
        {
            // 按配置声明类型读取（Float32 占 2 字），与下发编码同源
            var registerList = await _client
                .ReadHoldingRegisterAsync(specItem.Address, specItem.RegisterCount, cancellationToken)
                .ConfigureAwait(false);

            if (registerList.Length < specItem.RegisterCount)
            {
                errorList.Add(
                    $"{specItem.DisplayName}：PLC 只返回 {registerList.Length} 个字"
                    + $"（D{specItem.Address} 需要 {specItem.RegisterCount} 个）。");
                continue;
            }

            var Value = Convert.ToDouble(
                RegisterCodec.Decode(specItem.DataType, registerList, WordOrder.LittleEndian),
                CultureInfo.InvariantCulture);

            // 服务层范围校验：与下发同源。PLC 里被手工改成超范围值时不允许静默搬进上位机
            if (Value < specItem.Min || Value > specItem.Max)
            {
                errorList.Add(
                    $"{specItem.DisplayName}：PLC 值 {Value:0.###} 超出允许范围 [{specItem.Min:0.###}, {specItem.Max:0.###}]。");
                continue;
            }

            decodedValueList[specItem.Key] = Value;

            _parameterRepository.Parameter.TryGetValue(specItem.Key, out var thisGround);
            if (Math.Abs(Value - thisGround) > parameterTolerance)
            {
                Change.Add($"{specItem.DisplayName}：{thisGround:0.###} → {Value:0.###}");
            }
        }

        // 任一项非法就整体拒绝：绝不允许"读回一半"造成本地与 PLC 各错一半
        if (errorList.Count > 0)
        {
            throw new InvalidOperationException(string.Join("\n", errorList));
        }

        // 全部合法才覆盖内存并留底（与下发的双写对称）
        foreach (var (keyValue, Value) in decodedValueList)
        {
            _parameterRepository.Parameter[keyValue] = Value;
        }

        await StoreCurrentThisGround(cancellationToken).ConfigureAwait(false);

        _log.LogInformation(
            "已从 PLC 读取全局参数：{Total} 项，{Changed} 项与本地不同",
            _config.Parameter.Count, Change.Count);

        if (Change.Count > 0)
        {
            parameterChanged?.Invoke(this, EventArgs.Empty);
        }

        return Change;
    }

    /// <summary>把当前参数逐项写入参数区。含范围校验。</summary>
    private async Task WriteParameterCoreAsync(CancellationToken cancellationToken)
    {
        if (!_client.IsConnected)
        {
            throw new InvalidOperationException("PLC 未连接，无法下发参数。");
        }

        foreach (var specItem in _config.Parameter)
        {
            var Value = _parameterRepository.Parameter.TryGetValue(specItem.Key, out var getToValue) ? getToValue : specItem.Default;

            if (!specItem.IsInRange(Value))
            {
                throw new InvalidOperationException(
                    $"{specItem.DisplayName} 的值 {Value:0.###} 超出允许范围 {specItem.RangeText}，已拒绝下发。");
            }

            // 按配置声明的类型编码，并按占用字数选择单字 / 多字写入。
            // Float32 占 2 字：若仍用 WriteSingleRegisterAsync，只会写进低字，
            // PLC 读到的是错位的值（这正是 2026-09-12 现场实测发现的问题）。
            var registerList = RegisterCodec.Encode(specItem.DataType, Value, WordOrder.LittleEndian);

            if (registerList.Length == 1)
            {
                await _client.WriteSingleRegisterAsync(specItem.Address, registerList[0], cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await _client.WriteMultiRegisterAsync(specItem.Address, registerList, cancellationToken)
                    .ConfigureAwait(false);
            }

            await Task.Delay(writeInterval, cancellationToken).ConfigureAwait(false);
        }

        _log.LogInformation("已下发 {Count} 项参数（机种 {No}）", _config.Parameter.Count, _currentNumber);
    }

    // ── 本地持久化 ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task SaveThisGroundAsync(CancellationToken cancellationToken = default) =>
        _parameterRepository.SaveAsync(cancellationToken);

    /// <inheritdoc />
    public void SetParameter(string keyValue, double Value)
    {
        // 只改内存值（界面绑定回调会在初始化时触发），真正写 PLC 的
        // 异步写入参数 / 异步读取PLC参数 才有 编辑参数值列表 守卫。
        // 这里加守卫会在「用户尚未登录、Watcher 绑定首值」时抛异常，
        // 打断 async void OnStartup，导致 --smoke 与正常启动都卡死。
        var specItem = _config.Find(keyValue);
        if (specItem is null)
        {
            _log.LogWarning("忽略未知参数键：{Key}", keyValue);
            return;
        }

        _parameterRepository.Parameter[keyValue] = Value;
        WhenParameterChanged();
    }

    /// <inheritdoc />
    public async Task RenameAsync(int RecipeNumber, string nameValue, CancellationToken cancellationToken = default)
    {
        // 写本地机种数据（落盘 recipes.json），与读取PLC参数同级：服务层二次校验
        _permission.WantRequire(Permission.EditParameterValueList);

        ValidateRecipeNumber(RecipeNumber);

        var data = _store.GetOrCreate(RecipeNumber);
        data.Name = nameValue ?? string.Empty;

        await _store.SaveAsync(cancellationToken).ConfigureAwait(false);

        _log.LogInformation("机种 {No} 已命名为「{Name}」", RecipeNumber, data.Name);
        recipeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void RestoreDefaultValue()
    {
        // 同上：只改内存值，写 PLC 的下发路径已有权限守卫。
        foreach (var specItem in _config.Parameter)
        {
            _parameterRepository.Parameter[specItem.Key] = specItem.Default;
        }

        _log.LogInformation("全局参数已恢复为默认值（尚未下发 PLC）");
        WhenParameterChanged();
    }

    // ── CSV 导入导出见 RecipeService.Csv.cs（partial）──────────────────

    // ── 内部 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 把全局参数补齐（缺失项用配置默认值）。
    /// <para>参数与机种无关，所以这里只做"补默认"，不做任何按机种的覆盖。</para>
    /// </summary>
    private void LoadCurrentRecipe()
    {
        var parameterValue = _parameterRepository.Parameter;

        foreach (var specItem in _config.Parameter)
        {
            if (!parameterValue.ContainsKey(specItem.Key))
            {
                parameterValue[specItem.Key] = specItem.Default;
            }
        }

        _log.LogDebug("全局参数已就绪：{ParamCount} 项（当前机种 {No}）", parameterValue.Count, _currentNumber);
    }

    /// <summary>把内存中的全局参数写回 <c>parameters.json</c>。</summary>
    private Task StoreCurrentThisGround(CancellationToken cancellationToken = default) =>
        _parameterRepository.SaveAsync(cancellationToken);

    private async Task WriteRegisterAsync(int addressValue, int Value, CancellationToken cancellationToken)
    {
        if (!_client.IsConnected)
        {
            _log.LogWarning("PLC 未连接，寄存器 D{Address} 写入被跳过", addressValue);
            return;
        }

        await _client.WriteSingleRegisterAsync(addressValue, (ushort)Value, cancellationToken)
            .ConfigureAwait(false);
    }

    private void WhenParameterChanged() => parameterChanged?.Invoke(this, EventArgs.Empty);

    private void ValidateRecipeNumber(int RecipeNumber)
    {
        if (RecipeNumber < 1 || RecipeNumber > _config.RecipeCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RecipeNumber), RecipeNumber, $"机种号必须在 1 – {_config.RecipeCount} 之间。");
        }
    }
}
