using Microsoft.Extensions.Logging;
using PlcHmi.Business.Configuration;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// 汇川变量表服务。内存持有 <see cref="VariableTableDocument"/>（启动时从 variables.json 预加载），
/// 提供按名查找 / 按地址反查（供 OPC UA 符号寻址）与 CSV 导入。
///
/// <para><b>按地址反查的匹配规则</b>：变量的软元件地址经解析得到 (区域, Modbus地址)，
/// 以此为键建表；查询时要求<b>起始地址与寄存器数量都精确匹配</b>——
/// REAL 变量占 2 字，只有从首地址、按 2 字数量查询才命中，
/// 这样轮询合并块才能识别出“这 2 个寄存器其实是同一个 REAL 节点”。</para>
///
/// <para><b>无法寻址的条目</b>（如 D1210.0 字.位形式）：保留在条目列表里供界面展示与绑定校验拒绝，
/// 但不进入按地址反查表——Modbus 管线无法表达字内位。</para>
/// </summary>
public sealed class VariableTableService : IVariableTableService
{
    private readonly IAddressParser _parser;
    private readonly ILogger<VariableTableService> _log;
    private readonly SemaphoreSlim _writeGateLock = new(1, 1);

    /// <summary>按变量名查找（大小写不敏感）。volatile 引用整体替换，读侧免锁。</summary>
    private volatile Dictionary<string, VariableTableEntry> _nameIndex = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>按 (区域, Modbus地址) 反查：键为变量首地址。volatile 引用整体替换，读侧免锁。</summary>
    private volatile Dictionary<(ModbusArea Area, int startAddress), (VariableTableEntry Entry, int RegisterCount)> _addressIndex = new();

    private volatile VariableTableDocument _document;

    public VariableTableService(VariableTableDocument initialDocument, IAddressParser Parser, ILogger<VariableTableService> Log)
    {
        _parser = Parser;
        _log = Log;
        _document = initialDocument;
        RebuildIndex();
    }

    /// <inheritdoc />
    public IReadOnlyList<VariableTableEntry> EntryCollection => _document.EntryCollection;

    /// <inheritdoc />
    public event EventHandler? alreadyReImport;

    /// <inheritdoc />
    public VariableTableEntry? Find(string? VariableName)
    {
        if (string.IsNullOrWhiteSpace(VariableName))
        {
            return null;
        }

        // 先取 volatile 引用再查：CSV 导入瞬间整体换表，读侧看到的是旧表或新表，
        // 绝不会看到"半建好的索引"（此前重建是原地 Clear，读侧无锁 → 漏项/KeyNotFound）
        var index = _nameIndex;
        return index.TryGetValue(VariableName.Trim(), out var Entry) ? Entry : null;
    }

    /// <inheritdoc />
    public VariableTableEntry? ByAddressFind(ModbusArea areaValue, int startAddressValue, int registerCountValue)
    {
        var index = _addressIndex;
        return index.TryGetValue((areaValue, startAddressValue), out var hit)
            && hit.RegisterCount == registerCountValue ? hit.Entry : null;
    }

    /// <inheritdoc />
    public VariableTableEntry? ByFirstAddressFind(ModbusArea areaValue, int startAddressValue, out int registerCountValue)
    {
        var index = _addressIndex;

        if (index.TryGetValue((areaValue, startAddressValue), out var hit))
        {
            registerCountValue = hit.RegisterCount;
            return hit.Entry;
        }

        registerCountValue = 0;
        return null;
    }

    /// <inheritdoc />
    public async Task<VariableTableImportResult> ImportAsync(string csvFilePath, CancellationToken cancellationToken = default)
    {
        var parseResult = VariableTableCsvParser.ParseText(VariableTableCsvParser.ReadText(csvFilePath));

        await _writeGateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 先落盘后换内存：写入失败（磁盘不可写）时旧表保持不变
            await VariableTableLoader.PersistAsync(parseResult.Document, cancellationToken).ConfigureAwait(false);

            _document = parseResult.Document;
            RebuildIndex();
        }
        finally
        {
            _writeGateLock.Release();
        }

        _log.LogInformation("变量表已导入：{Count} 个变量，跳过 {Skipped} 行（来源 {File}）",
            parseResult.Document.EntryCollection.Count, parseResult.SkipRowNumber, csvFilePath);

        alreadyReImport?.Invoke(this, EventArgs.Empty);

        return new VariableTableImportResult
        {
            ValidCount = parseResult.Document.EntryCollection.Count,
            SkipRowNumber = parseResult.SkipRowNumber,
        };
    }

    /// <summary>
    /// 重建名称索引与地址反查索引。
    /// <para>构建全新字典后<b>整体替换</b> volatile 引用：读侧免锁且永远看到完整快照，
    /// 不会出现原地 Clear 期间的半建好状态。调用方（构造函数 / 异步导入）已持写闸锁。</para>
    /// </summary>
    private void RebuildIndex()
    {
        var newNameIndex = new Dictionary<string, VariableTableEntry>(StringComparer.OrdinalIgnoreCase);
        var newAddressIndex = new Dictionary<(ModbusArea Area, int startAddress), (VariableTableEntry Entry, int RegisterCount)>();

        var beCoverHideBitEntry = new List<string>();

        foreach (var Entry in _document.EntryCollection)
        {
            if (Entry.VariableName.Length > 0)
            {
                newNameIndex[Entry.VariableName] = Entry;
            }

            // 地址无法按当前机型寻址（如中型 PLC 的非法格式）的条目只进名称索引。
            // D/R 字内位（D1210.2 等，汇川位打包）自 2026-09-17 起可寻址：
            // 读写按整字处理，故占 1 字。
            if (_parser.TryParse(Entry.SoftElementAddress, out var addressValue) && addressValue is not null)
            {
                var Key = (addressValue.Area, addressValue.ModbusAddress);

                // 同一字上若已有条目，反查应回到先占者，不被后来的位条目抢走
                if (addressValue.IsIntraWordBit && newAddressIndex.ContainsKey(Key))
                {
                    beCoverHideBitEntry.Add($"{Entry.VariableName}（{Entry.SoftElementAddress}）");
                    continue;
                }

                var registerCountItem = addressValue.IsIntraWordBit ? 1 : _parser.GetRegisterCount(Entry.DataType);
                newAddressIndex[Key] = (Entry, registerCountItem);
            }
        }

        // 先发布两张表，再让后续读取看到新快照
        _nameIndex = newNameIndex;
        _addressIndex = newAddressIndex;

        // 被遮蔽的条目**不能静默丢弃**：同一个 D 字上只有第一个变量能用于反查
        // （Modbus/OPC UA 侧读写的是整字），其余变量按名称仍可寻址。
        // 配置静默降级成"少了一半绑定"比崩溃更难查 —— 所以这里必须留痕。
        if (beCoverHideBitEntry.Count > 0)
        {
            _log.LogWarning(
                "变量表有 {Count} 个条目与同一字上的先占变量共享地址，地址反查将由先占者响应"
                + "（按变量名绑定不受影响）：{Items}",
                beCoverHideBitEntry.Count, string.Join("、", beCoverHideBitEntry.Take(8)));
        }
    }
}
