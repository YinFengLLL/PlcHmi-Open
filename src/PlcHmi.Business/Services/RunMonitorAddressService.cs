using Microsoft.Extensions.Logging;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// 运行监控页元件地址管理。详见 <see cref="IRunMonitorAddressService"/>。
///
/// <para>地址的唯一真相是 tags.json 的同名点位：构造时从点位仓储取当前地址，
/// 保存时写回仓储并落盘、重建采集计划。<see cref="RunMonitorComponent.DefaultAddress"/>
/// 只作恢复默认的参照，不参与运行。</para>
/// </summary>
public sealed class RunMonitorAddressService : IRunMonitorAddressService
{
    /// <summary>运行页元件的出厂定义（显示名 / 标签名 / 默认地址 / 区类型固定）。</summary>
    private static readonly RunMonitorComponent[] factoryDefinitionList =
    [
        new() { DisplayName = "当前设备状态", TagName = "RunStatus", DefaultAddress = "D6",
            IsCommand = false, DataType = TagDataType.UInt16, Group = TagGroup.Fast,
            Remark = "0=空闲中 1=运行中 2=待机中 3=复位中 4=报警中（UInt16，占 1 字）" },
        new() { DisplayName = "当前节拍", TagName = "CycleTime", DefaultAddress = "D1100",
            IsCommand = false, DataType = TagDataType.UInt32, Group = TagGroup.Fast,
            Remark = "UInt32，缩放 0.001，单位秒，占 2 字" },
        new() { DisplayName = "当前产量", TagName = "OutputCount", DefaultAddress = "D1102",
            IsCommand = false, DataType = TagDataType.UInt32, Group = TagGroup.Slow, Remark = "UInt32，占 2 字" },
        new() { DisplayName = "目标产量", TagName = "TargetCount", DefaultAddress = "D1104",
            IsCommand = false, DataType = TagDataType.UInt32, Group = TagGroup.Slow,
            Remark = "UInt32，占 2 字；达成率 = 当前产量 ÷ 目标产量" },
        new() { DisplayName = "良品数量", TagName = "GoodCount", DefaultAddress = "D1106",
            IsCommand = false, DataType = TagDataType.UInt32, Group = TagGroup.Slow, Remark = "UInt32，占 2 字" },
        new() { DisplayName = "不良品数量", TagName = "BadCount", DefaultAddress = "D1108",
            IsCommand = false, DataType = TagDataType.UInt32, Group = TagGroup.Slow, Remark = "UInt32，占 2 字" },

        // 主页面左下角三盏状态灯与「已锁附颗数」（2026-10-16 现场要求）：
        // 三个完成标志是 M 区<b>位</b>（0=未完成 / 1=完成，FC01 读线圈，只读不写）；
        // 颗数是 D 区 INT（16 位有符号，占 1 字），只显示不参与统计。
        new() { DisplayName = "锁附完成", TagName = "ScrewDone", DefaultAddress = "M490",
            IsCommand = false, IsBitComponent = true, DataType = TagDataType.Boolean, Group = TagGroup.Fast,
            Remark = "M 区位：0=未完成 1=完成（FC01 读线圈）" },
        new() { DisplayName = "点胶完成", TagName = "GlueDone", DefaultAddress = "M491",
            IsCommand = false, IsBitComponent = true, DataType = TagDataType.Boolean, Group = TagGroup.Fast,
            Remark = "M 区位：0=未完成 1=完成（FC01 读线圈）" },
        new() { DisplayName = "加工完成", TagName = "ProcessDone", DefaultAddress = "M492",
            IsCommand = false, IsBitComponent = true, DataType = TagDataType.Boolean, Group = TagGroup.Fast,
            Remark = "M 区位：0=未完成 1=完成（FC01 读线圈）" },
        new() { DisplayName = "已锁附颗数", TagName = "ScrewCount", DefaultAddress = "D1116",
            IsCommand = false, DataType = TagDataType.Int16, Group = TagGroup.Fast,
            Remark = "INT（16 位有符号，占 1 字）" },

        new() { DisplayName = "启动按钮", TagName = "CmdStart", DefaultAddress = "M200",
            IsCommand = true, DataType = TagDataType.Boolean, Group = TagGroup.Command,
            Remark = "M 区，脉冲写（置位 200ms 后自动复位）" },
        new() { DisplayName = "停止按钮", TagName = "CmdStop", DefaultAddress = "M201",
            IsCommand = true, DataType = TagDataType.Boolean, Group = TagGroup.Command, Remark = "M 区，脉冲写" },
        new() { DisplayName = "复位按钮", TagName = "CmdReset", DefaultAddress = "M202",
            IsCommand = true, DataType = TagDataType.Boolean, Group = TagGroup.Command, Remark = "M 区，脉冲写" },
        new() { DisplayName = "报警清除按钮", TagName = "CmdAlarmClear", DefaultAddress = "M203",
            IsCommand = true, DataType = TagDataType.Boolean, Group = TagGroup.Command,
            Remark = "M 区，脉冲写；不要放进报警源区 M500–M600" },
        new() { DisplayName = "数据清除", TagName = "CmdDataClear", DefaultAddress = "M1000",
            IsCommand = true, DataType = TagDataType.Boolean, Group = TagGroup.Command,
            Remark = "M 区，复归型脉冲写，由 PLC 清零累计数据" },
    ];

    private readonly ITagRepository _tag;
    private readonly IAddressParser _parser;
    private readonly IPollingEngine _polling;
    private readonly IPermissionGuard _permission;
    private readonly ILogger<RunMonitorAddressService> _log;
    private readonly List<RunMonitorComponent> _componentList = [];

    public RunMonitorAddressService(
        ITagRepository tagList,
        IAddressParser Parser,
        IPollingEngine Polling,
        IPermissionGuard Permission,
        ILogger<RunMonitorAddressService> Log)
    {
        _tag = tagList;
        _parser = Parser;
        _polling = Polling;
        _permission = Permission;
        _log = Log;

        foreach (var DefinitionItem in factoryDefinitionList)
        {
            var tagItem = _tag.Find(DefinitionItem.TagName);

            if (tagItem is null)
            {
                // tags.json 被外部改动删掉了点位：地址退回出厂默认并留痕，
                // 不抛异常——页面还要能打开，保存时校验会拦住缺失的点位
                _log.LogWarning("点位 {Tag} 不在 tags.json 中，运行页元件 {Name} 退回默认地址",
                    DefinitionItem.TagName, DefinitionItem.DisplayName);
            }

            var copy = DefinitionItem.Clone();
            copy.Address = tagItem?.Address ?? DefinitionItem.DefaultAddress;
            _componentList.Add(copy);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<RunMonitorComponent> ComponentCollection => _componentList;

    /// <inheritdoc />
    public event EventHandler? AddressChanged;

    /// <inheritdoc />
    public RunMonitorAddressValidate Validate(IReadOnlyList<RunMonitorComponent> candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var errorList = new List<string>();
        var IsOccupied = new List<occupiedRange>();

        foreach (var EntryItem in candidate)
        {
            if (string.IsNullOrWhiteSpace(EntryItem.Address))
            {
                errorList.Add($"{EntryItem.DisplayName}：地址不能为空。");
                continue;
            }

            if (!_parser.TryParse(EntryItem.Address, out var PlcAddress) || PlcAddress is null)
            {
                errorList.Add($"{EntryItem.DisplayName}：「{EntryItem.Address}」不是有效的软元件地址。");
                continue;
            }

            if (PlcAddress.IsIntraWordBit)
            {
                errorList.Add(
                    $"{EntryItem.DisplayName}：「{EntryItem.Address}」不允许使用字内位地址——"
                    + "运行页监控量为整字读写、命令为线圈，请填 D6、M200 这类完整地址。");
                continue;
            }

            if (NeedMArea(EntryItem) && PlcAddress.ElementItem != SoftElement.M)
            {
                errorList.Add(
                    $"{EntryItem.DisplayName}：「{EntryItem.Address}」必须是 M 区地址（当前解析为 {PlcAddress.ElementItem} 区）。"
                    + (EntryItem.IsCommand
                        ? "上位机只置位内部继电器，由 PLC 程序决定何时驱动 Y 输出。"
                        : "该元件是 M 区位状态量（0=未完成 / 1=完成），请填 M490 这类位地址。"));
                continue;
            }

            if (!NeedMArea(EntryItem) && PlcAddress.ElementItem != SoftElement.D)
            {
                errorList.Add(
                    $"{EntryItem.DisplayName}：「{EntryItem.Address}」必须是 D 区寄存器地址（当前解析为 {PlcAddress.ElementItem} 区）。");
                continue;
            }

            // 占用字数以 tags.json 中该点位的数据类型为准（UInt32 占 2 字）
            var tagItem = _tag.Find(EntryItem.TagName);
            var wordCount = tagItem is null ? 1 : Math.Max(1, _parser.GetRegisterCount(tagItem.DataType));
            IsOccupied.Add(new occupiedRange(EntryItem.DisplayName, PlcAddress.ElementItem, PlcAddress.ModbusAddress, wordCount));
        }

        CheckCandidateOverlap(errorList, IsOccupied);
        CheckPairPerOtherPoint(errorList, candidate, IsOccupied);
        CheckRetainArea(errorList, IsOccupied);

        return new RunMonitorAddressValidate { IsValid = errorList.Count == 0, ErrorCollection = errorList };
    }

    /// <inheritdoc />
    public async Task SaveAsync(IReadOnlyList<RunMonitorComponent> candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        // 服务层权限兜底：界面隐藏按钮只是防误触，绕过界面直接调服务也进不来（审查标准 R5）
        _permission.WantRequire(Permission.EditBindAddress);

        if (candidate.Count != _componentList.Count)
        {
            throw new InvalidOperationException(
                $"候选元件数（{candidate.Count}）与运行页元件数（{_componentList.Count}）不一致，拒绝保存。");
        }

        var validateItem = Validate(candidate);
        if (!validateItem.IsValid)
        {
            throw new InvalidOperationException($"运行监控页地址校验未通过：\n{validateItem.Summary}");
        }

        foreach (var EntryItem in candidate)
        {
            if (_tag.TryUpdateAddress(EntryItem.TagName, EntryItem.Address))
            {
                continue;
            }

            // 现场 tags.json 里没有这个点位：覆盖升级时它属于"现场文件"，不会被新版覆盖，
            // 于是新版自带的点位（如工位状态三灯 + 已锁附颗数）在现场是缺的。
            // 按出厂定义补建后随整表保存一起落盘——否则管理员改完地址根本存不下去。
            if (!_tag.TryAdd(new TagDefinition
                {
                    Name = EntryItem.TagName,
                    Address = EntryItem.Address,
                    DataType = EntryItem.DataType,
                    AccessQuery = EntryItem.IsCommand ? TagAccessQuery.Write : TagAccessQuery.Read,
                    Group = EntryItem.Group,
                    Description = EntryItem.DisplayName,
                }))
            {
                throw new InvalidOperationException(
                    $"点位 \"{EntryItem.TagName}\"（{EntryItem.DisplayName}）不在 tags.json 中，且补建失败，无法写入地址。");
            }

            _log.LogWarning("运行页元件 {Name} 不在现场 tags.json 中，已按出厂定义补建点位 {Tag}",
                EntryItem.DisplayName, EntryItem.TagName);
        }

        await _tag.SaveAsync(cancellationToken).ConfigureAwait(false);

        foreach (var EntryItem in candidate)
        {
            _componentList.First(thisGroundItem => thisGroundItem.TagName == EntryItem.TagName).Address = EntryItem.Address;
        }

        // 点位定义已变，采集计划必须重建，否则新地址要等重启才生效
        await _polling.RebuildPlanListAsync(cancellationToken).ConfigureAwait(false);

        _log.LogInformation("运行监控页元件地址已保存并生效（{Count} 个元件）", candidate.Count);
        AddressChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public string FindAddress(string TagName) =>
        _componentList.FirstOrDefault(EntryItem =>
            string.Equals(EntryItem.TagName, TagName, StringComparison.OrdinalIgnoreCase))?.Address ?? string.Empty;

    // ── 校验辅助 ────────────────────────────────────────────────────────

    /// <summary>
    /// 该元件是否必须落在 M 区：命令（上位机写 M 位）与位元件（上位机读 M 位）都要 M 区，
    /// 其余是 D 区监控量。
    /// </summary>
    private static bool NeedMArea(RunMonitorComponent EntryItem) => EntryItem.IsCommand || EntryItem.IsBitComponent;

    private sealed record occupiedRange(string Tag, SoftElement ElementItem, int startPoint, int wordCount)
    {
        public int EndBoundary => startPoint + wordCount;
    }

    private static void CheckCandidateOverlap(List<string> errorList, List<occupiedRange> IsOccupied)
    {
        // 只比同软元件区：D100 与 M100 的 Modbus 地址都是 100，跨区比较会误报
        for (var item = 0; item < IsOccupied.Count; item++)
        {
            for (var item2 = item + 1; item2 < IsOccupied.Count; item2++)
            {
                var itemA = IsOccupied[item];
                var itemB = IsOccupied[item2];

                if (itemA.ElementItem == itemB.ElementItem &&
                    itemA.startPoint < itemB.EndBoundary && itemB.startPoint < itemA.EndBoundary)
                {
                    errorList.Add(
                        $"{itemA.Tag}（{itemA.ElementItem}{itemA.startPoint}，占 {itemA.wordCount} 字）与 "
                        + $"{itemB.Tag}（{itemB.ElementItem}{itemB.startPoint}）地址重叠。");
                }
            }
        }
    }

    /// <summary>
    /// 与 tags.json 中其它点位比对（本次编辑的运行页点位除外）。
    /// <b>不能只比可读点位</b>：CmdStart 这类只写点位同样占着地址。
    /// </summary>
    private void CheckPairPerOtherPoint(
        List<string> errorList, IReadOnlyList<RunMonitorComponent> candidate, List<occupiedRange> IsOccupied)
    {
        var alreadyEdit = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var EntryItem in candidate)
        {
            alreadyEdit.Add(EntryItem.TagName);
        }

        foreach (var tagItem in _tag.All)
        {
            if (alreadyEdit.Contains(tagItem.Name))
            {
                continue;
            }

            if (!_parser.TryParse(tagItem.Address, out var PlcAddress) || PlcAddress is null)
            {
                continue;
            }

            var wordCount = PlcAddress.IsIntraWordBit ? 1 : _parser.GetRegisterCount(tagItem.DataType);
            var other = new occupiedRange($"点位 {tagItem.Name}（{tagItem.Address}）",
                PlcAddress.ElementItem, PlcAddress.ModbusAddress, Math.Max(1, wordCount));

            foreach (var range in IsOccupied.Where(occupiedItem => occupiedItem.ElementItem == other.ElementItem))
            {
                if (range.startPoint < other.EndBoundary && other.startPoint < range.EndBoundary)
                {
                    errorList.Add(
                        $"{range.Tag}（{range.ElementItem}{range.startPoint}）与{other.Tag}冲突。");
                }
            }
        }
    }

    /// <summary>
    /// 与系统保留区比对。这些区段不在 tags.json 里，但一样不能被运行页地址占用
    /// （尤其命令位落进报警源区会被 500ms 的报警扫描当成报警位读走）。
    /// </summary>
    private static void CheckRetainArea(List<string> errorList, List<occupiedRange> IsOccupied)
    {
        var retain = new List<occupiedRange>
        {
            new("报警位区", SoftElement.M, 500, 101),
            new("报警字区", SoftElement.D, 550, 5),
            new("延时表专属区", SoftElement.D, ParameterTableService.delayAddressMin,
                ParameterTableService.delayAddressMax - ParameterTableService.delayAddressMin + 1),
            new("按钮表区", SoftElement.M, ParameterTableService.buttonAddressMin,
                ParameterTableService.buttonAddressMax - ParameterTableService.buttonAddressMin + 1),
        };

        foreach (var range in IsOccupied)
        {
            foreach (var block in retain.Where(retainItem => retainItem.ElementItem == range.ElementItem))
            {
                if (range.startPoint < block.EndBoundary && block.startPoint < range.EndBoundary)
                {
                    errorList.Add(
                        $"{range.Tag}（{range.ElementItem}{range.startPoint}）落在{block.Tag}"
                        + $"（{block.ElementItem}{block.startPoint}–{block.ElementItem}{block.EndBoundary - 1}）内，请改到别处。");
                }
            }
        }
    }
}
