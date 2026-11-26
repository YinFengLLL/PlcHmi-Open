using System.Globalization;
using Microsoft.Extensions.Logging;
using PlcHmi.Business.Configuration;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// 参数表格服务的「条目校验」部分。
///
/// <para><b>为什么拆成 partial 文件</b>：主文件已越过项目规模门禁的基线，
/// 而校验是一整块纯规则代码（无副作用、可单独阅读），
/// 抽出来后主文件只留加载 / 保存 / 增删改。</para>
/// </summary>
public sealed partial class ParameterTableService
{
    /// <summary>
    /// 启动期检查延时地址是否互相重叠，重叠即登记配置健康告警（界面常驻横幅）。
    ///
    /// <para><b>为什么必须示警：</b>延时自 2026-10-16 起按 <b>DINT 占 2 字</b>下发。
    /// 老现场升级时 <c>param-tables.json</c> 属于"现场文件"不会被覆盖，
    /// 里面仍是 16 位时代的相邻地址（D1000 / D1001 / D1002）——这样的两条延时会互相吃掉
    /// 高位字（表现为"某条延时偶尔变成几万秒"）。不下发/编辑时不会报错，
    /// 等到现场发现时可能已经跑错了几批工件，所以在启动期就摆到明面上。</para>
    /// </summary>
    private void CheckDelayAddressOverlap()
    {
        var overlap = new List<string>();

        for (var item = 0; item < _delay.Count; item++)
        {
            if (!_parser.TryParse(_delay[item].Address, out var addressA) || addressA is null)
            {
                continue;
            }

            for (var item2 = item + 1; item2 < _delay.Count; item2++)
            {
                if (!_parser.TryParse(_delay[item2].Address, out var addressB) || addressB is null)
                {
                    continue;
                }

                if (Math.Abs(addressA.ModbusAddress - addressB.ModbusAddress) < ParameterEntryDefinition.delayOccupyWordCount)
                {
                    overlap.Add($"「{_delay[item].Name}」({_delay[item].Address}) 与 「{_delay[item2].Name}」({_delay[item2].Address})");
                }
            }
        }

        if (overlap.Count == 0)
        {
            return;
        }

        _log.LogWarning("参数表（延时）存在 {Count} 处地址重叠：{Detail}", overlap.Count, string.Join("；", overlap));

        _health.ReportFallback("参数表（延时）",
            $"延时按 32 位整数（DINT）存毫秒、每条占 {ParameterEntryDefinition.delayOccupyWordCount} 个字，"
            + $"以下条目地址重叠会互相覆盖高位字：{string.Join("；", overlap)}。"
            + "请在参数页「编辑延时」里改成间隔 2 的地址（如 D1000 / D1002 / D1004 …）。");
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>占用检查为什么不能靠引用排除自身：</b>编辑对话框操作的是 <see cref="ParameterEntryDefinition.Clone"/> 副本，
    /// <c>ReferenceEquals(x, item)</c> 对副本永远不成立——旧实现因此把「正在编辑的那条」也当成冲突来源，
    /// 管理员改个名字就会弹「已被占用」（2026-09-11 需求方反馈）。
    /// 现按 <paramref name="isUpdate"/> 区分：编辑时同 Id 的条目就是自身，直接跳过。
    /// </remarks>
    public ParameterEntryValidate Validate(ParameterTableCategory Category, ParameterEntryDefinition Entry, bool isUpdate = false)
    {
        var errorList = new List<string>();

        if (string.IsNullOrWhiteSpace(Entry.Name))
        {
            errorList.Add("名称不能为空。");
        }

        var list = Get(Category);

        if (!isUpdate && list.Any(coordinateX =>
                string.Equals(coordinateX.Id, Entry.Id, StringComparison.OrdinalIgnoreCase)))
        {
            errorList.Add($"编号「{Entry.Id}」已被占用。");
        }

        // 地址校验：延时必须 D 区、按钮必须 M 区
        var allow = Category == ParameterTableCategory.Delay ? delayElement : buttonElement;
        var expected = Category == ParameterTableCategory.Delay ? "D 区" : "M 区";

        if (string.IsNullOrWhiteSpace(Entry.Address))
        {
            errorList.Add("地址不能为空。");
        }
        else if (!_parser.TryParse(Entry.Address, out var PlcAddress) || PlcAddress is null)
        {
            errorList.Add($"「{Entry.Address}」不是有效的软元件地址。");
        }
        else if (!allow.Contains(PlcAddress.ElementItem))
        {
            errorList.Add($"{expected}地址要求：当前填的是 {PlcAddress.ElementItem} 区。"
                       + (Category == ParameterTableCategory.Button
                           ? "按钮只操作内部继电器，由 PLC 决定何时驱动输出。"
                           : string.Empty));
        }
        else if (Category == ParameterTableCategory.Button &&
                 (PlcAddress.ModbusAddress < buttonAddressMin || PlcAddress.ModbusAddress > buttonAddressMax))
        {
            errorList.Add(
                $"按钮地址必须在 M{buttonAddressMin}–M{buttonAddressMax} 范围内（当前 {Entry.Address}）。"
                + "M5000 以下是系统保留段：运行命令、手动使能、气缸与点动、报警位都在那里，"
                + "自定义开关放过去会撞掉某个功能。");
        }
        else if (Category == ParameterTableCategory.Delay &&
                 (PlcAddress.ModbusAddress < delayAddressMin || PlcAddress.ModbusAddress > delayAddressMax))
        {
            errorList.Add(
                $"延时地址必须在 D{delayAddressMin}–D{delayAddressMax} 范围内（当前 {Entry.Address}）。"
                + "D1000–D1099 是延时表专属区（每个延时占 2 字），D1100–D1199 为上位机预留段，"
                + "D1200–D1799 是轴参数区，放过去会与预留规划冲突。");
        }
        else if (Category == ParameterTableCategory.Delay)
        {
            ValidateDelayAddressNotOverlap(Entry, PlcAddress, isUpdate, errorList);
        }

        // 延时才校验数值范围
        if (Category == ParameterTableCategory.Delay)
        {
            if (Entry.Max <= Entry.Min)
            {
                errorList.Add($"取值范围无效：上限 {Entry.Max:0.###} 必须大于下限 {Entry.Min:0.###}。");
            }
            else if (Entry.Value < Entry.Min || Entry.Value > Entry.Max)
            {
                errorList.Add($"当前值 {Entry.Value:0.###} 超出范围 {Entry.Min:0.###} – {Entry.Max:0.###}。");
            }
        }

        return new ParameterEntryValidate { IsValid = errorList.Count == 0, ErrorCollection = errorList };
    }

    /// <summary>
    /// 延时地址跨度重叠校验（2026-10-16 起延时按 <b>DINT 存毫秒，占 2 个字</b>）。
    ///
    /// <para><b>为什么必须校验：</b>每条延时占 D[n]、D[n+1] 两字，两条延时若落在相邻/相同地址
    /// （如 D1000 与 D1001），后写的那条会把前一条的<b>高位字覆盖掉</b>——
    /// 表现为"某条延时偶尔变成几万秒"，查起来毫无线索。16 位时代最多两条共用一个字，
    /// 代价只是覆盖；32 位之后是"高位字被吃"，必须拦死。</para>
    ///
    /// <para>与所有其它延时条目比较（含停用项）：停用项仍占着规划里的地址，
    /// 只比启用的会让"重新启用即冲突"埋成定时炸弹。</para>
    /// </summary>
    private void ValidateDelayAddressNotOverlap(
        ParameterEntryDefinition Entry, PlcAddress addressValue, bool isUpdate, List<string> errorList)
    {
        // DINT 跨 2 字：占用 [起始, 起始 + 1]
        var thisTimesStartPoint = addressValue.ModbusAddress;
        var thisTimesEndPoint = thisTimesStartPoint + ParameterEntryDefinition.delayOccupyWordCount - 1;

        foreach (var otherItem in _delay)
        {
            // 编辑自身时跳过同 Id 的那条（对话框传的是克隆副本，比不了引用）
            if (isUpdate && string.Equals(otherItem.Id, Entry.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!_parser.TryParse(otherItem.Address, out var otherAddress) || otherAddress is null)
            {
                continue;   // 对方地址本身无效：由它自己的校验报错，这里不重复报
            }

            var pairSideStartPoint = otherAddress.ModbusAddress;
            var pairSideEndPoint = pairSideStartPoint + ParameterEntryDefinition.delayOccupyWordCount - 1;

            if (thisTimesStartPoint <= pairSideEndPoint && pairSideStartPoint <= thisTimesEndPoint)
            {
                errorList.Add(
                    $"地址与「{otherItem.Name}」({otherItem.Address}) 重叠：延时按 32 位整数（DINT）存毫秒、"
                    + $"每条占 {ParameterEntryDefinition.delayOccupyWordCount} 个字（{Entry.Address} 占用 "
                    + $"D{thisTimesStartPoint.ToString(CultureInfo.InvariantCulture)}–"
                    + $"D{thisTimesEndPoint.ToString(CultureInfo.InvariantCulture)}）。"
                    + "请改成间隔 2 的地址（如 D1000 / D1002 / D1004 …）。");
                return;
            }
        }
    }
}
