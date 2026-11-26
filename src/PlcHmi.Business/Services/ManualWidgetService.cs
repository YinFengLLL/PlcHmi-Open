using System.Text.Json;
using Microsoft.Extensions.Logging;
using PlcHmi.Business.Configuration;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// 手动页元件服务。加载与保存 <c>config/manual-widgets.json</c>。
///
/// <para><b>核心约束：元件只能绑定 M 区地址。</b>
/// 上位机只置位内部继电器，由 PLC 程序决定何时驱动 Y 输出——
/// 避免绕过 PLC 的联锁逻辑。绑定 Y 点的配置会在校验阶段被拒绝。</para>
/// </summary>
public sealed class ManualComponentService : IManualComponentService
{
    private readonly IAddressParser _parser;
    private readonly ILogger<ManualComponentService> _log;
    private readonly IConfigHealth? _health;
    private readonly IPermissionGuard _permission;
    private readonly IVariableTableService? _variableTable;
    private readonly List<ManualComponentDefinition> _component = new();

    public ManualComponentService(
        IAddressParser Parser, ILogger<ManualComponentService> Log,
        IPermissionGuard Permission, IConfigHealth? Health = null, IVariableTableService? VariableTable = null)
    {
        _parser = Parser;
        _log = Log;
        _permission = Permission;
        _health = Health;
        _variableTable = VariableTable;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 返回<b>快照</b>而不是内部列表引用：此前交出内部 List，
    /// 调用方可以直接 Add 后调保存绕过权限校验，也会在枚举时
    /// 撞上别处的增删（Collection was modified）。
    /// </remarks>
    public IReadOnlyList<ManualComponentDefinition> ComponentCollection => _component.ToArray();

    /// <inheritdoc />
    public event EventHandler? componentChanged;

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var filePath = ConfigPath.ManualComponent;

        var resultValue = await ConfigFileLoader
            .LoadJsonAsync<ManualComponentConfig>(filePath, JsonConfig.Read, _log, cancellationToken)
            .ConfigureAwait(false);

        _health.Report("手动页元件配置", resultValue);

        if (resultValue.loadResult is LoadResultSplitCategory.Missing)
        {
            _log.LogInformation("未找到手动页元件配置，写入一份默认配置：{Path}", filePath);

            _component.Clear();
            _component.AddRange(CreateDefaultValue());
            Sort();

            // 首次运行写默认配置：启动阶段尚无登录身份，走核心保存
            await SaveCoreAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        _component.Clear();
        _component.AddRange(resultValue.Value?.ComponentCollection ?? new List<ManualComponentDefinition>());
        Sort();
        ApplyVariableBinding();

        if (resultValue.loadResult is LoadResultSplitCategory.Loaded)
        {
            _log.LogInformation("已加载 {Count} 个手动页元件", _component.Count);
        }

        componentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 变量绑定回填：绑定了变量的元件用变量表中的软元件地址刷新地址字段。
    /// <para>变量失效时保留原地址并登记健康警告——禁止无声降级。</para>
    /// </summary>
    private void ApplyVariableBinding()
    {
        foreach (var ComponentItem in _component)
        {
            ComponentItem.Address = VariableBindingBackfill.ParseAddress(ComponentItem.Variable, _variableTable, _parser, ComponentItem.Address, out var error);

            if (error.Length > 0)
            {
                _health?.ReportFallback($"手动元件「{ComponentItem.Name}」", error);
            }

            ComponentItem.StateAddress = VariableBindingBackfill.ParseAddress(
                ComponentItem.StateValueVariable, _variableTable, _parser, ComponentItem.StateAddress, out error);

            if (error.Length > 0)
            {
                _health?.ReportFallback($"手动元件「{ComponentItem.Name}」", error);
            }
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        // 保存即"把当前元件整体落盘"，与增删改同权限（管理员）：
        // 否则直接改内部列表再调保存就能绕过校验（R5）
        _permission.WantRequire(Permission.EditManualComponent);

        await SaveCoreAsync(cancellationToken).ConfigureAwait(false);
        componentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>落盘但不通知界面（初始化 / 已有校验的写路径内部使用）。</summary>
    private async Task SaveCoreAsync(CancellationToken cancellationToken)
    {
        Sort();

        var configPairObject = new ManualComponentConfig { ComponentCollection = _component.Select(componentEntry => componentEntry.Clone()).ToList() };
        var jsonText = JsonSerializer.Serialize(configPairObject, JsonConfig.Write);

        try
        {
            await AtomicFile.WriteAllTextAsync(ConfigPath.ManualComponent, jsonText, cancellationToken)
                .ConfigureAwait(false);

            _log.LogInformation("手动页元件已保存（{Count} 个）：{Path}", _component.Count, ConfigPath.ManualComponent);
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "保存手动页元件失败：{Path}", ConfigPath.ManualComponent);
            throw;
        }
    }

    /// <inheritdoc />
    public ManualComponentValidate Validate(ManualComponentDefinition ComponentItem)
    {
        var errorList = new List<string>();

        if (string.IsNullOrWhiteSpace(ComponentItem.Name))
        {
            errorList.Add("元件名称不能为空。");
        }

        switch (ComponentItem.Kind)
        {
            case ManualComponentType.Cylinder:
                // 单线圈气缸：一个地址，置位伸出、复位缩回
                ValidateContinue(ComponentItem.Address, "控制地址", required: true, errorList);
                break;

            case ManualComponentType.Button:
                ValidateContinue(ComponentItem.Address, "触发地址", required: true, errorList);
                break;

            case ManualComponentType.Indicator:
                ValidateContinue(ComponentItem.StateAddress, "监测地址", required: true, errorList);
                break;
        }

        return new ManualComponentValidate { IsValid = errorList.Count == 0, ErrorCollection = errorList };
    }

    /// <inheritdoc />
    public void Add(ManualComponentDefinition ComponentItem)
    {
        ArgumentNullException.ThrowIfNull(ComponentItem);

        // 增删元件是管理员专有：元件定义决定"手动页上有什么按钮、按下去动哪个 M 点"，
        // 改错了等于给操作员一个会误动作的界面。
        _permission.WantRequire(Permission.EditManualComponent);

        if (string.IsNullOrWhiteSpace(ComponentItem.Id))
        {
            ComponentItem.Id = NewId(ComponentItem.Kind);
        }

        if (ComponentItem.Order <= 0)
        {
            ComponentItem.Order = _component.Count == 0 ? 1 : _component.Max(alreadyHasItem => alreadyHasItem.Order) + 1;
        }

        _component.Add(ComponentItem.Clone());
        Sort();

        _log.LogInformation("已新增手动页元件：{Name}", ComponentItem.Name);
        componentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Update(ManualComponentDefinition ComponentItem)
    {
        ArgumentNullException.ThrowIfNull(ComponentItem);

        _permission.WantRequire(Permission.EditManualComponent);

        var Sequence = _component.FindIndex(pendingUpdateItem => string.Equals(pendingUpdateItem.Id, ComponentItem.Id, StringComparison.OrdinalIgnoreCase));

        if (Sequence < 0)
        {
            throw new InvalidOperationException($"找不到 Id 为「{ComponentItem.Id}」的元件。");
        }

        _component[Sequence] = ComponentItem.Clone();
        Sort();

        _log.LogInformation("已更新手动页元件：{Name}", ComponentItem.Name);
        componentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Remove(string Id)
    {
        _permission.WantRequire(Permission.EditManualComponent);

        var alreadyRemove = _component.RemoveAll(componentEntry => string.Equals(componentEntry.Id, Id, StringComparison.OrdinalIgnoreCase));

        if (alreadyRemove > 0)
        {
            _log.LogInformation("已删除手动页元件：{Id}", Id);
            componentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public string NewId(ManualComponentType type)
    {
        var prefix = type switch
        {
            ManualComponentType.Cylinder => "cyl",
            ManualComponentType.Button => "btn",
            _ => "lamp",
        };

        // 保证不与现有 Id 冲突
        for (var Entry = 1; Entry < 1000; Entry++)
        {
            var candidate = $"{prefix}-{Entry}";

            if (_component.All(componentEntry => !string.Equals(componentEntry.Id, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }

        return $"{prefix}-{Guid.NewGuid():N}"[..12];
    }

    // ── 内部 ────────────────────────────────────────────────────────────

    /// <summary>校验一个地址是否为 M 区。</summary>
    private void ValidateContinue(string addressValue, string TagName, bool required, List<string> errorList)
    {
        if (string.IsNullOrWhiteSpace(addressValue))
        {
            if (required)
            {
                errorList.Add($"{TagName}不能为空。");
            }

            return;
        }

        if (!_parser.TryParse(addressValue, out var PlcAddress) || PlcAddress is null)
        {
            errorList.Add($"{TagName}「{addressValue}」不是有效的软元件地址。");
            return;
        }

        if (PlcAddress.ElementItem != SoftElement.M)
        {
            errorList.Add($"{TagName}「{addressValue}」必须是 M 区地址（当前解析为 {PlcAddress.ElementItem} 区）。"
                       + "上位机只允许操作内部继电器，由 PLC 程序决定何时驱动 Y 输出。");
        }
    }

    private void Sort() => _component.Sort((item, item2) => item.Order.CompareTo(item2.Order));

    /// <summary>首次运行时的默认元件（单线圈气缸，顺序与旧版硬编码一致）。</summary>
    private static IEnumerable<ManualComponentDefinition> CreateDefaultValue() =>
    [
        new()
        {
            Id = "cyl-1", Kind = ManualComponentType.Cylinder, Name = "上料气缸", Order = 1,
            Address = "M220",
        },
        new()
        {
            Id = "cyl-2", Kind = ManualComponentType.Cylinder, Name = "压装气缸", Order = 2,
            Address = "M221",
        },
        new()
        {
            Id = "cyl-3", Kind = ManualComponentType.Cylinder, Name = "顶升气缸", Order = 3,
            Address = "M222",
        },
    ];
}
