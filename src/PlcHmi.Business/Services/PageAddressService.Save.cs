using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PlcHmi.Business.Configuration;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// <see cref="PageAddressService"/> 的保存与回写部分（partial）。
///
/// <para>从主文件拆出：主文件逼近 400 行规模门禁；"收集/读写 PLC"与"写回 JSON 配置"
/// 是两类不同职责，分开更易审查。</para>
/// </summary>
public sealed partial class PageAddressService
{
    // ── 保存 ────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task SavePointTablePageAsync(
        string TableKey, AddressEditRequest Request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(Request);

        _permission.WantRequire(Permission.EditBindAddress);

        var Table = _config.PointTable.Find(TableKey)
            ?? throw new InvalidOperationException($"未配置点表「{TableKey}」。");

        var Group = GroupName(Table);

        Remove(Request);

        foreach (var Item in Request.ItemCollection)
        {
            switch (Item.Category)
            {
                case "点表列":
                    var columnItem = Table.FindColumn(Item.Key)
                        ?? throw new InvalidOperationException($"点表「{Table.DisplayName}」不存在列「{Item.Key}」。");
                    columnItem.BaseAddress = ParseDArea(Item);
                    break;

                case "手动区":
                    // 手动区的固定显示元件（当前浮高）：只改地址，类型固定 REAL
                    if (string.Equals(Item.Key, "当前浮高", StringComparison.OrdinalIgnoreCase))
                    {
                        Table.CurrentFloatHeightAddress = ParseDArea(Item);
                    }

                    break;

                case "辅助地址":
                    ApplyAuxAddress(Item, Group);
                    break;

                case "动作按钮":
                    ApplyActionButton(Item, Group);
                    break;

                case "点动":
                    ApplyJog(Item, Group);
                    break;

                case "回零":
                    ApplyHome(Item);
                    break;

                case "状态灯":
                    ApplyStatusLamp(Item, Group);
                    break;

                default:
                    break;
            }
        }

        // 结构体成员统一重排（2026-10-21）：勾选 / 取消勾选、改数据类型都会改变成员偏移与结构体长度。
        // 此前这些参数只从 JSON 读、保存时不重算 —— 现场把「抬针高度」改成 INT16 后，
        // 后面的「点胶速度」仍停在旧偏移（显示 D3506，PLC 实际已是 D3505）。
        var StructLength = AuxAddressStructureLayout.ReLayout(_config.AuxAddress, Group);

        // 兜底：统计的是应用完新增/修改后的 _配置.辅助地址 全量状态，落盘前拒绝超标配置。
        ValidateAuxAddressUpperLimit();

        await Persist(cancellationToken).ConfigureAwait(false);

        _log.LogInformation(
            "点表页 {Table} 的地址已保存（结构体成员 {Count} 个，长度 {Length} 字）",
            Table.DisplayName, AuxAddressStructureLayout.Member(_config.AuxAddress, Group).Count, StructLength);
    }

    /// <inheritdoc />
    public async Task SaveCalibrationPageAsync(AddressEditRequest Request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(Request);

        _permission.WantRequire(Permission.EditBindAddress);

        Remove(Request);

        foreach (var Item in Request.ItemCollection)
        {
            switch (Item.Category)
            {
                case "标定字段":
                    ApplyCalibrationField(Item);
                    break;

                case "辅助地址":
                    ApplyAuxAddress(Item, "标定");
                    break;

                case "动作按钮":
                    ApplyActionButton(Item, "标定");
                    break;

                case "标定按钮":
                    // 标定页有两个启动按钮（「移至指定位置」2026-10-15 删除，
                    // 与锁附分组动作按钮「移全指定」同址重复）；下相机 2026-10-17 新增
                    if (Item.Key == "启动")
                    {
                        _config.Calibration.StartAddress = Item.AddressText.Trim();
                    }
                    else if (Item.Key == "下相机启动")
                    {
                        _config.Calibration.BottomCameraStartAddress = Item.AddressText.Trim();
                    }

                    break;

                case "点动":
                    ApplyJog(Item, "锁附");
                    break;

                case "回零":
                    ApplyHome(Item);
                    break;

                default:
                    break;
            }
        }

        // 兜底：统计的是应用完新增/修改后的 _配置.辅助地址 全量状态，落盘前拒绝超标配置。
        ValidateAuxAddressUpperLimit();

        await Persist(cancellationToken).ConfigureAwait(false);
        _log.LogInformation("标定页的地址已保存");
    }

    // ── 应用（写回配置） ────────────────────────────────────────────────

    private void Remove(AddressEditRequest Request)
    {
        foreach (var Item in Request.DeletedItem)
        {
            // 键在「同一分组内唯一」：项.分组 非空时只删本组同名项，避免误删跨组的同名项；
            // 为空则保持全局匹配以兼容老请求（老编辑器没给分组）。
            var limitGroup = !string.IsNullOrWhiteSpace(Item.Group);

            switch (Item.Category)
            {
                case "辅助地址":
                    _config.AuxAddress.RemoveAll(item =>
                        string.Equals(item.Key, Item.Key, StringComparison.OrdinalIgnoreCase)
                        && (!limitGroup || GroupMatch(item.Group, Item.Group)));
                    break;

                case "动作按钮":
                    // 动作按钮是管理员自定义的现场动作入口，允许删除；
                    // 但"固定按钮"（点胶参数模式）是页面必备元件，界面上本就没有删除入口，
                    // 这里再挡一道：配置被手工删掉也不会让按钮消失（下次加载还会被补齐）
                    _config.ActionButton.RemoveAll(item =>
                        !item.Fixed
                        && string.Equals(item.Key, Item.Key, StringComparison.OrdinalIgnoreCase)
                        && (!limitGroup || GroupMatch(item.Group, Item.Group)));
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>应用一个动作按钮（支持新增与改名改址）。</summary>
    private void ApplyActionButton(AddressEditItem Item, string Group)
    {
        // 键的唯一性契约是「同一分组内唯一」，旧逻辑全局按键查找会命中别的分组的同名键并就地改掉。
        var currentHas = _config.ActionButton.FirstOrDefault(item =>
            GroupMatch(item.Group, Group)
            && string.Equals(item.Key, Item.Key, StringComparison.OrdinalIgnoreCase));

        if (currentHas is null)
        {
            currentHas = new ActionButtonItem
            {
                Key = Item.Key,
                Group = string.IsNullOrWhiteSpace(Item.Group) ? Group : Item.Group,

                // 自定义动作默认按"脉冲 + 需二次确认"处理：现场新增的按钮不动作为默认，
                // 想常按的按钮由管理员在 JSON 里把 是否脉冲 改成 false。
                IsPulse = true,
                IsDanger = true,
            };

            _config.ActionButton.Add(currentHas);
        }

        currentHas.Name = Item.Name;
        currentHas.Address = Item.AddressText.Trim();
    }

    private void ApplyAuxAddress(AddressEditItem Item, string Group)
    {
        // 键的唯一性契约是「同一分组内唯一」，旧逻辑全局按键查找会命中别的分组的同名键并就地改掉。
        var currentHas = _config.AuxAddress.FirstOrDefault(item =>
            GroupMatch(item.Group, Group)
            && string.Equals(item.Key, Item.Key, StringComparison.OrdinalIgnoreCase));

        var target = currentHas ?? new AuxAddressItem();

        target.Key = Item.Key;
        target.Name = Item.Name;
        target.Group = string.IsNullOrWhiteSpace(Item.Group) ? Group : Item.Group;
        target.DataType = ParseType(Item.DataTypeText);

        if (Item.StructMember)
        {
            // 成员的真实地址由 辅助地址结构布局.重新排布 统一算（保存末尾执行）：
            // 这里只保证基址存在，地址栏填的值不参与寻址
            target.StructMember = true;

            if (target.StructBaseAddress <= 0)
            {
                target.StructBaseAddress = AuxAddressStructureLayout.defaultBaseAddress;
            }
        }
        else
        {
            // 取消勾选 = 退回固定地址。结构体参数必须清零：
            // 留着旧基址/长度，下次加载时的老配置补齐会把它又认回成员（勾选框自己变了回去）
            target.StructMember = false;
            target.StructBaseAddress = 0;
            target.StructInnerOffset = 0;
            target.StructLength = 0;

            if (!int.TryParse(Item.AddressText.Trim().TrimStart('D', 'd'), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var Sequence) || Sequence <= 0)
            {
                throw new InvalidOperationException(
                    $"辅助地址「{Item.Name}」的地址「{Item.AddressText}」无效（应为 D 区序号）。");
            }

            target.Address = "D" + Sequence.ToString(CultureInfo.InvariantCulture);
        }

        if (currentHas is null)
        {
            _config.AuxAddress.Add(target);
        }
    }

    /// <summary>
    /// 应用一条状态灯地址（缺失即新增）。
    /// <para><b>只接受 M 区</b>：状态灯用 FC01 读线圈取布尔，写成 D 区会去读一个字的位——
    /// 语义完全不同的两种元件，宁可在保存时明确报错，也不要让它"能存但读错"。</para>
    /// </summary>
    private void ApplyStatusLamp(AddressEditItem Item, string Group)
    {
        var Address = Item.AddressText.Trim();
        var integerPartSplit = Address.TrimStart('M', 'm');

        if (Address.Length == integerPartSplit.Length
            || !int.TryParse(integerPartSplit, NumberStyles.Integer, CultureInfo.InvariantCulture, out var Sequence)
            || Sequence <= 0)
        {
            throw new InvalidOperationException(
                $"状态灯「{Item.Name}」的地址「{Item.AddressText}」无效（应为 M 区线圈，如 M470）。");
        }

        var currentHas = _config.StatusLamp.FirstOrDefault(item =>
            string.Equals(item.Key, Item.Key, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Group, Item.Group, StringComparison.OrdinalIgnoreCase));

        var target = currentHas ?? new StatusLampItem();

        if (currentHas is null)
        {
            target.Key = string.IsNullOrWhiteSpace(Item.Key) ? "回原状态灯" : Item.Key;
            target.Group = string.IsNullOrWhiteSpace(Item.Group) ? Group : Item.Group;
            _config.StatusLamp.Add(target);
        }

        target.Name = Item.Name;
        target.Address = "M" + Sequence.ToString(CultureInfo.InvariantCulture);
    }

    // ── 内部（收集 / 保存共用） ─────────────────────────────────────────

    private static int ParseDArea(AddressEditItem Item)
    {
        if (!int.TryParse(Item.AddressText.Trim().TrimStart('D', 'd'), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var Sequence) || Sequence <= 0)
        {
            throw new InvalidOperationException($"「{Item.Name}」的地址「{Item.AddressText}」无效（应为 D 区序号）。");
        }

        return Sequence;
    }

    private static int Absolute(string addressValue)
    {
        if (!TryParse(addressValue, out var Address))
        {
            throw new InvalidOperationException($"地址「{addressValue}」无效。");
        }

        return Address;
    }

    /// <summary>
    /// 解析 D 区地址（绝对值）。<b>只接受 D 区</b>：本方法的结果经
    /// <c>异步写入多个寄存器</c> 写保持寄存器，此前把 'M','m' 也一起 TrimStart，
    /// "M200" 与 "D200" 都解析成 200——手改 JSON 把辅助地址写成 M 区时，
    /// 值会被静默写进 D200（另一个语义完全不同的寄存器）。
    /// </summary>
    private static bool TryParse(string addressValue, out int Address) =>
        int.TryParse(addressValue.Trim().TrimStart('D', 'd'), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out Address);

    private static TagDataType ParseType(string TypeText) =>
        Enum.TryParse<TagDataType>(TypeText, ignoreCase: true, out var Kind) ? Kind : TagDataType.Float32;

    private string GroupName(PointTableDefinition Table) =>
        string.IsNullOrWhiteSpace(Table.JogGroup) ? Table.Key : Table.JogGroup;

    private static bool GroupMatch(string componentGroup, string PageGroup) =>
        string.IsNullOrWhiteSpace(componentGroup)
        || string.IsNullOrWhiteSpace(PageGroup)
        || string.Equals(componentGroup, PageGroup, StringComparison.OrdinalIgnoreCase);

    private IReadOnlyList<JogButtonItem> ThisPageJog(string Group) =>
        _config.Jog.Where(Item => GroupMatch(Item.Group, Group)).ToList();

    private JogButtonItem? FindJog(string axisNumber, string Group) =>
        _config.Jog.FirstOrDefault(Item =>
            string.Equals(Item.Axis, axisNumber, StringComparison.OrdinalIgnoreCase) && GroupMatch(Item.Group, Group));

    private HomeButtonItem? FindHome(string axisNumber) =>
        _config.Home.FirstOrDefault(Item => string.Equals(Item.Axis, axisNumber, StringComparison.OrdinalIgnoreCase));

    private ActionButtonItem? FindActionButton(string Key) =>
        _config.ActionButton.FirstOrDefault(Item => string.Equals(Item.Key, Key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 落盘前兜底校验：每个分组的辅助地址不得超过 <see cref="AuxAddressItem.perGroupUpperLimit"/>。
    /// <para><b>统计口径</b>：按 <see cref="AuxAddressItem.Group"/> 原样分组（<c>string.Empty</c>
    /// 作为独立的「全局」组），空分组项不摊进其它分组——它与页面规则一致（空分组视为全局、
    /// 所有页面都能看到），单独统计最安全，不会让某个具名分组莫名超标。</para>
    /// <para>界面侧已在新增前拦截；这里再挡一次，覆盖手工构造请求 / 直接改配置的场景。</para>
    /// </summary>
    private void ValidateAuxAddressUpperLimit()
    {
        foreach (var groupValue in _config.AuxAddress.GroupBy(Item => Item.Group, StringComparer.OrdinalIgnoreCase))
        {
            if (groupValue.Count() > AuxAddressItem.perGroupUpperLimit)
            {
                var showGroupName = string.IsNullOrWhiteSpace(groupValue.Key) ? "全局" : groupValue.Key;
                throw new InvalidOperationException(
                    $"「{showGroupName}」分组的辅助地址超过 {AuxAddressItem.perGroupUpperLimit} 个上限，已拒绝保存。");
            }
        }
    }

    private async Task Persist(CancellationToken cancellationToken)
    {
        var jsonText = JsonSerializer.Serialize(_config, JsonConfig.writeHumanStyle);
        await AtomicFile.WriteAllTextAsync(ConfigPath.RecipeConfig, jsonText, cancellationToken).ConfigureAwait(false);
    }
}
