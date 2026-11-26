using System.Globalization;
using Microsoft.Extensions.Logging;
using PlcHmi.Communication.Modbus;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// 页面地址服务：把页面上所有与 PLC 交互的元件地址收口成一份可编辑清单，
/// 并在保存时一次落盘（保存部分见 <c>PageAddressService.Save.cs</c>）。
///
/// <para><b>已取消地址范围限制</b>：不再校验轴参数区 / PLC 保留区 / 区间归属，
/// 只保证地址可解析、元素类型正确、本次提交内不重叠。</para>
/// </summary>
public sealed partial class PageAddressService : IPageAddressService
{
    /// <summary>说明：锁附页的手动轴（标定页复用同样的三根轴）。</summary>

    private readonly IModbusClient _client;
    private readonly TagWriter _writer;
    private readonly RecipeConfig _config;
    private readonly IPermissionGuard _permission;
    private readonly ILogger<PageAddressService> _log;

    public PageAddressService(
        IModbusClient Client,
        TagWriter Writer,
        RecipeConfig configPairObject,
        IPermissionGuard Permission,
        ILogger<PageAddressService> Log)
    {
        _client = Client;
        _writer = Writer;
        _config = configPairObject;
        _permission = Permission;
        _log = Log;
    }

    // ── 收集 ────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public IReadOnlyList<AddressEditItem> CollectSetPointTablePage(string TableKey)
    {
        var Table = _config.PointTable.Find(TableKey)
            ?? throw new InvalidOperationException($"未配置点表「{TableKey}」。");

        var Group = GroupName(Table);
        var list = new List<AddressEditItem>();

        foreach (var columnItem in Table.Column)
        {
            list.Add(new AddressEditItem
            {
                Category = "点表列",
                Key = columnItem.Key,
                Name = $"{columnItem.DisplayName}基址",
                AddressText = $"D{columnItem.BaseAddress}",
                DataTypeText = columnItem.DataType.ToString(),
            });
        }

        // 手动区「当前浮高」（2026-10-24）：只有配了地址的表（锁附页）才显示这一行。
        // 固定元件——可改地址，不可删、不可改类型（REAL 两字）。
        if (Table.CurrentFloatHeightAddress > 0)
        {
            list.Add(new AddressEditItem
            {
                Category = "手动区",
                Key = "当前浮高",
                Name = "当前浮高（REAL，占 2 字）",
                AddressText = $"D{Table.CurrentFloatHeightAddress}",
                DataTypeText = TagDataType.Float32.ToString(),
            });
        }

        // 点胶表有指令码列 = 点胶单点参数走结构体数组：此时辅助地址行才显示"结构体成员"勾选框
        var allowStructMember = Table.HasInstructionCode;

        foreach (var Item in _config.AuxAddress.Where(Item => GroupMatch(Item.Group, Group)))
        {
            list.Add(new AddressEditItem
            {
                Category = "辅助地址",
                Key = Item.Key,
                Name = Item.DisplayName,

                // 成员地址由结构体布局算出（= 第 1 点地址）；非成员就是它自己的固定地址
                AddressText = Item.StructMember ? $"D{Item.StructBaseAddress + Item.StructInnerOffset}" : Item.Address,
                DataTypeText = Item.DataType.ToString(),
                Group = Item.Group,
                Deletable = true,
                StructMember = Item.StructMember,
                SettableStructMember = allowStructMember,
            });
        }

        foreach (var Item in ActionButton(Group))
        {
            list.Add(new AddressEditItem
            {
                Category = "动作按钮",
                Key = Item.Key,
                Name = Item.DisplayName,
                AddressText = Item.Address,
                DataTypeText = TagDataType.Boolean.ToString(),
                Group = Item.Group,

                // 固定按钮（点胶「参数模式」）是页面必备元件：可改地址不可删
                Deletable = !Item.Fixed,
            });
        }

        foreach (var Item in ThisPageJog(Group))
        {
            list.Add(new AddressEditItem
            {
                Category = "点动",
                Key = $"{Item.Axis}|正转",
                Name = $"{Item.DisplayName}·正转",
                AddressText = Item.ForwardAddress,
                DataTypeText = TagDataType.Boolean.ToString(),
            });

            list.Add(new AddressEditItem
            {
                Category = "点动",
                Key = $"{Item.Axis}|反转",
                Name = $"{Item.DisplayName}·反转",
                AddressText = Item.ReverseAddress,
                DataTypeText = TagDataType.Boolean.ToString(),
            });
        }

        foreach (var axisNumber in ThisPageJog(Group).Select(Item => Item.Axis).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var homeItem = FindHome(axisNumber);
            list.Add(new AddressEditItem
            {
                Category = "回零",
                Key = axisNumber,
                Name = $"{axisNumber} 轴回零",
                AddressText = homeItem?.Address ?? string.Empty,
                DataTypeText = TagDataType.Boolean.ToString(),
            });
        }

        // 顶部固定状态灯：读 M 区（0=各轴未回原，1=各轴回原完成）。固定元件——可改地址不可删。
        foreach (var lampItem in StatusLamp(Group))
        {
            list.Add(new AddressEditItem
            {
                Category = "状态灯",
                Key = lampItem.Key,
                Name = lampItem.DisplayName,
                AddressText = lampItem.Address,
                DataTypeText = TagDataType.Boolean.ToString(),
                Group = lampItem.Group,
            });
        }

        return list;
    }

    /// <inheritdoc />
    public IReadOnlyList<AuxAddressItem> AuxAddress(string Group) =>
        _config.AuxAddress.Where(Item => GroupMatch(Item.Group, Group)).ToList();

    /// <inheritdoc />
    /// <remarks>
    /// <b>固定按钮排首位</b>（2026-10-21 现场要求：点胶页的「参数模式」按钮必须在动作区第一个）。
    /// <c>OrderByDescending</c> 是稳定排序，其余按钮的相对顺序与配置一致。
    /// </remarks>
    public IReadOnlyList<ActionButtonItem> ActionButton(string Group) =>
        _config.ActionButton
            .Where(Item => GroupMatch(Item.Group, Group))
            .OrderByDescending(Item => Item.Fixed)
            .ToList();

    /// <inheritdoc />
    /// <remarks>
    /// 配置缺条目时按 <see cref="StatusLampItem.defaultAddress"/> 合成一条（不写回配置）：
    /// 老现场升级后 recipe-config.json 里没有该段，返回空则示教页没灯、管理员也无入口配置。
    /// </remarks>
    public IReadOnlyList<StatusLampItem> StatusLamp(string Group)
    {
        var alreadyMatch = _config.StatusLamp.Where(Item => GroupMatch(Item.Group, Group)).ToList();

        if (alreadyMatch.Count > 0)
        {
            return alreadyMatch;
        }

        return
        [
            new StatusLampItem
            {
                Key = "回原状态灯",
                Name = "各轴回原状态",
                Group = Group,
                Address = StatusLampItem.defaultAddress,
            },
        ];
    }

    // ── 辅助地址读写 ────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<double> ReadAuxAddressAsync(AuxAddressItem Item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(Item);

        if (Item.IsSwitch || !_client.IsConnected)
        {
            return 0;
        }

        if (!TryParse(Item.Address, out var addressValue))
        {
            throw new InvalidOperationException($"辅助地址「{Item.DisplayName}」的地址「{Item.Address}」无效。");
        }

        var registerList = await _client
            .ReadHoldingRegisterAsync(addressValue, Item.RegisterCount, cancellationToken)
            .ConfigureAwait(false);

        if (registerList.Length < Item.RegisterCount)
        {
            return 0;
        }

        var Value = RegisterCodec.Decode(Item.DataType, registerList, WordOrder.LittleEndian);
        return Convert.ToDouble(Value, CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public async Task<bool> ReadAuxSwitchAsync(AuxAddressItem Item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(Item);

        if (!_client.IsConnected)
        {
            return false;
        }

        if (!TryParse(Item.Address, out var addressValue))
        {
            throw new InvalidOperationException($"辅助开关「{Item.DisplayName}」的地址「{Item.Address}」无效。");
        }

        var coilList = await _client
            .ReadCoilAsync(addressValue, 1, cancellationToken)
            .ConfigureAwait(false);

        return coilList.Length > 0 && coilList[0];
    }

    /// <inheritdoc />
    public async Task WriteAuxAddressAsync(AuxAddressItem Item, double Value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(Item);

        _permission.WantRequire(Permission.EditTeach);

        if (!_client.IsConnected)
        {
            throw new InvalidOperationException("PLC 未连接，无法写入。");
        }

        var registerList = RegisterCodec.Encode(Item.DataType, Value, WordOrder.LittleEndian);

        await _client
            .WriteMultiRegisterAsync(Absolute(Item.Address), registerList, cancellationToken)
            .ConfigureAwait(false);

        _log.LogInformation("辅助地址 {Name}（{Address}）写入 {Value}", Item.DisplayName, Item.Address, Value);
    }

    /// <inheritdoc />
    public async Task WriteAuxSwitchAsync(AuxAddressItem Item, bool Value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(Item);

        _permission.WantRequire(Permission.EditTeach);

        await _writer.WriteCoilAddressAsync(Item.Address, Value, cancellationToken).ConfigureAwait(false);
        _log.LogInformation("辅助开关 {Name}（{Address}）写入 {Value}", Item.DisplayName, Item.Address, Value);
    }

    // ── 保存与回写 ──────────────────────────────────────────────────────
    // 见 PageAddressService.Save.cs（partial）：把"收集/读写"与"写回配置"分开，
    // 是为守住单文件 400 行的规模门禁。
}
