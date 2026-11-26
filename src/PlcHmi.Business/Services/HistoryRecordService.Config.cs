using System.Text.Json;
using Microsoft.Extensions.Logging;
using PlcHmi.Business.Configuration;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// 历史记录服务的配置部分：校验与读写 <c>config/history-record.json</c>。
///
/// <para><b>校验为什么必须在服务层：</b>配置有两个入口——界面编辑器改的，
/// 和现场直接手改 JSON 的。规则只写在界面里，手改的文件就没人管：
/// 触发位填成字元件、DINT 与下一项地址重叠，都能安静地生效，直到数据失真才被发现。</para>
/// </summary>
public sealed partial class HistoryRecordService
{
    /// <inheritdoc />
    public HistoryConfigValidate Validate(HistoryRecordConfig configItem)
    {
        ArgumentNullException.ThrowIfNull(configItem);

        var errorList = new List<string>();

        if (string.IsNullOrWhiteSpace(configItem.Title))
        {
            errorList.Add("数据体名称不能为空。");
        }

        ValidateTriggerAddress(configItem, errorList);

        if (configItem.RetainDays is < HistoryRecordConfig.minRetainDaysNumber or > HistoryRecordConfig.maxRetainDaysNumber)
        {
            errorList.Add($"保留天数必须填 {HistoryRecordConfig.minRetainDaysNumber}–{HistoryRecordConfig.maxRetainDaysNumber} 天。");
        }

        if (configItem.DataItemCollection.Count == 0)
        {
            errorList.Add("至少需要一个数据项（表格至少一列）。");
        }
        else if (configItem.DataItemCollection.Count > HistoryRecordConfig.maxDataItemCount)
        {
            errorList.Add($"数据项最多 {HistoryRecordConfig.maxDataItemCount} 个，当前 {configItem.DataItemCollection.Count} 个。");
        }

        ValidateDataItem(configItem, errorList);

        return new HistoryConfigValidate { IsValid = errorList.Count == 0, ErrorCollection = errorList };
    }

    private void ValidateTriggerAddress(HistoryRecordConfig configItem, List<string> errorList)
    {
        if (string.IsNullOrWhiteSpace(configItem.TriggerAddress))
        {
            errorList.Add("触发位地址不能为空（M 区位地址，如 M700）。");
            return;
        }

        if (!_parser.TryParse(configItem.TriggerAddress.Trim(), out var triggerBit) || triggerBit is null)
        {
            errorList.Add($"触发位「{configItem.TriggerAddress}」不是有效的软元件地址。");
            return;
        }

        if (triggerBit.ElementItem != SoftElement.M)
        {
            errorList.Add($"触发位必须是 M 区（如 M700），当前填的是 {triggerBit.ElementItem}{triggerBit.LogicIndex}。");
        }
    }

    /// <summary>逐项校验名称、类型、小数位与读取地址，并检查地址区间重叠。</summary>
    private void ValidateDataItem(HistoryRecordConfig configItem, List<string> errorList)
    {
        var alreadyUseName = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var OccupiedSpan = new List<(SoftElement ElementItem, int begin, int stop, string Name)>();

        for (var Sequence = 0; Sequence < configItem.DataItemCollection.Count; Sequence++)
        {
            var Item = configItem.DataItemCollection[Sequence];
            var Position = string.IsNullOrWhiteSpace(Item.Name) ? $"第 {Sequence + 1} 项" : $"第 {Sequence + 1} 项「{Item.Name}」";

            if (string.IsNullOrWhiteSpace(Item.Name))
            {
                errorList.Add($"{Position}：名称不能为空。");
            }
            else if (!alreadyUseName.Add(Item.Name.Trim()))
            {
                errorList.Add($"{Position}：名称与其他数据项重复。");
            }

            // 2026-10-24：新增 REAL（32 位浮点）—— 现场有些量 PLC 直接按浮点给，不再放大成整数
            if (Item.DataType is not (TagDataType.Int16 or TagDataType.Int32 or TagDataType.Float32))
            {
                errorList.Add($"{Position}：数值类型只支持 INT（16 位）、DINT（32 位）或 REAL（32 位浮点）。");
            }

            if (Item.DecimalBit is < 0 or > HistoryRecordConfig.maxDecimalBit)
            {
                errorList.Add($"{Position}：小数位必须填 0–{HistoryRecordConfig.maxDecimalBit}。");
            }

            // ASCII 显示只对 DINT 有意义：INT 只有 2 字节凑不出 4 个字符，
            // REAL 的 4 个字节是浮点位模式、拆成字符全是乱码，
            // 而"配置能存但显示不对"比"当场报错"难查得多（2026-10-17 新增）
            if (Item.ShowAsAscii && !Item.SettableAscii)
            {
                errorList.Add($"{Position}：ASCII 显示只支持 DINT（32 位 = 4 字节 = 4 个字符），当前类型是 {Item.TypeText}。");
            }

            ValidateDataItemAddress(Item, Position, OccupiedSpan, errorList);
        }
    }

    private void ValidateDataItemAddress(
        HistoryDataItem Item, string Position,
        List<(SoftElement ElementItem, int begin, int stop, string Name)> OccupiedSpan,
        List<string> errorList)
    {
        if (string.IsNullOrWhiteSpace(Item.Address))
        {
            errorList.Add($"{Position}：读取地址不能为空。");
            return;
        }

        if (!_parser.TryParse(Item.Address.Trim(), out var Address) || Address is null)
        {
            errorList.Add($"{Position}：「{Item.Address}」不是有效的软元件地址。");
            return;
        }

        if (Address.BitIndex >= 0)
        {
            errorList.Add($"{Position}：{Item.Address} 是位地址，数值必须读字元件（如 D2109）。");
            return;
        }

        if (Address.Area != ModbusArea.HoldingRegister)
        {
            errorList.Add($"{Position}：读取地址必须是字元件（D / R 区），当前填的是 {Address.ElementItem}{Address.LogicIndex}。");
            return;
        }

        var wordCount = _parser.GetRegisterCount(Item.DataType);
        var begin = Address.ModbusAddress;
        var stop = begin + wordCount - 1;

        // 区间重叠 = 两列的取值互相"咬"在一起，通常是地址抄错一位。
        // DINT 占 2 字，最容易出现"下一项紧接着写在上一个的低位字上"。
        var conflict = OccupiedSpan.FirstOrDefault(areaBetween => areaBetween.ElementItem == Address.ElementItem && begin <= areaBetween.stop && stop >= areaBetween.begin);

        if (conflict.Name is not null)
        {
            errorList.Add($"{Position}：地址 {Item.Address}（占 {wordCount} 字）与「{conflict.Name}」的地址区间重叠，请检查地址。");
            return;
        }

        OccupiedSpan.Add((Address.ElementItem, begin, stop, string.IsNullOrWhiteSpace(Item.Name) ? Item.Address : Item.Name));
    }

    /// <inheritdoc />
    public async Task SaveConfigAsync(HistoryRecordConfig configItem, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configItem);

        // 改的是"设备动作被记成什么"，界面隐藏按钮只是防误触，真正的边界在这里（R5）
        _permission.WantRequire(Permission.EditHistoryRecord);

        var validateItem = Validate(configItem);

        if (!validateItem.IsValid)
        {
            throw new InvalidOperationException($"历史记录配置不合法：{validateItem.Summary}");
        }

        _config = configItem.Clone();
        await WriteConfigFileAsync(_config, cancellationToken).ConfigureAwait(false);

        // 触发位可能已换：重建电平基线，清掉与旧触发位绑定的握手状态，
        // 避免把新位的当前状态误当成一次动作、或把"待复位"带到新位上
        _lastTrigger = null;
        _pendingReset = false;
        _readRetry = false;
        _readFailedHinted = false;
        _hintedConfigError = false;

        _log.LogInformation("历史记录配置已保存：触发位 {Address}，{Count} 个数据项，保留 {Days} 天",
            _config.TriggerAddress, _config.DataItemCollection.Count, _config.RetainDays);

        configChanged?.Invoke(this, EventArgs.Empty);

        // 保留期可能被改短，顺手清理一次超期文件
        _store.CleanBeyondPeriod(_clock().Date, _config.RetainDays);
    }

    private async Task LoadConfigAsync(CancellationToken cancellationToken)
    {
        var resultValue = await ConfigFileLoader
            .LoadJsonAsync<HistoryRecordConfigFile>(ConfigPath.HistoryRecordConfig, JsonConfig.Read, _log, cancellationToken)
            .ConfigureAwait(false);

        _health?.Report("历史记录配置", resultValue);

        if (resultValue.loadResult is LoadResultSplitCategory.Missing)
        {
            _log.LogInformation("未找到历史记录配置，写入一份默认配置：{Path}", ConfigPath.HistoryRecordConfig);

            _config = CreateDefaultValue();
            await WriteConfigFileAsync(_config, cancellationToken).ConfigureAwait(false);
            return;
        }

        _config = resultValue.Value?.Config ?? CreateDefaultValue();

        _log.LogInformation("已加载历史记录配置：{Title}，触发位 {Address}，{Count} 个数据项",
            _config.Title, _config.TriggerAddress, _config.DataItemCollection.Count);
    }

    private async Task WriteConfigFileAsync(HistoryRecordConfig configItem, CancellationToken cancellationToken)
    {
        var jsonText = JsonSerializer.Serialize(
            new HistoryRecordConfigFile { Config = configItem.Clone() }, JsonConfig.Write);

        Directory.CreateDirectory(ConfigPath.ConfigDirectory);

        await AtomicFile
            .WriteAllTextAsync(ConfigPath.HistoryRecordConfig, jsonText, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>首次运行时的默认数据体（对应一台锁螺丝机，地址与 PLC 程序约定一致）。</summary>
    private static HistoryRecordConfig CreateDefaultValue() => new()
    {
        Title = "锁螺丝记录",
        TriggerAddress = "M700",
        Enabled = true,
        RetainDays = 90,
        DataItemCollection =
        [
            new() { Name = "工位号", Address = "D2100", DataType = TagDataType.Int16, DecimalBit = 0 },
            new() { Name = "机种号", Address = "D2101", DataType = TagDataType.Int16, DecimalBit = 0 },
            new() { Name = "工步", Address = "D2102", DataType = TagDataType.Int16, DecimalBit = 0 },
            new() { Name = "设定扭力", Address = "D2103", DataType = TagDataType.Int32, DecimalBit = 2, Unit = "N·m" },
            new() { Name = "扭力上限", Address = "D2105", DataType = TagDataType.Int32, DecimalBit = 2, Unit = "N·m" },
            new() { Name = "扭力下限", Address = "D2107", DataType = TagDataType.Int32, DecimalBit = 2, Unit = "N·m" },
            new() { Name = "实际扭力", Address = "D2109", DataType = TagDataType.Int32, DecimalBit = 2, Unit = "N·m" },
            new() { Name = "设定角度", Address = "D2111", DataType = TagDataType.Int32, DecimalBit = 1, Unit = "°" },
            new() { Name = "角度上限", Address = "D2113", DataType = TagDataType.Int32, DecimalBit = 1, Unit = "°" },
            new() { Name = "角度下限", Address = "D2115", DataType = TagDataType.Int32, DecimalBit = 1, Unit = "°" },
            new() { Name = "实际角度", Address = "D2117", DataType = TagDataType.Int32, DecimalBit = 1, Unit = "°" },
            new() { Name = "锁付时间", Address = "D2119", DataType = TagDataType.Int32, DecimalBit = 2, Unit = "s" },
            new() { Name = "结果", Address = "D2121", DataType = TagDataType.Int16, DecimalBit = 0 },
            new() { Name = "错误代码", Address = "D2122", DataType = TagDataType.Int16, DecimalBit = 0 },
        ],
    };
}
