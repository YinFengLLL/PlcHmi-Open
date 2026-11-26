using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PlcHmi.Business.Configuration;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// 轴地址管理服务。集中维护"随轴切换"的地址，供参数页的「轴地址」区编辑。
///
/// <para><b>点胶锁付机为六轴</b>（锁附 X/Y/Z/ZZ + 点胶 X/Z；点胶 Y 与锁附 Y 共轴），
/// 因此默认轴顺序与兜底值都是这六根轴。</para>
///
/// <para><b>各类地址与各自的落点：</b></para>
/// <list type="bullet">
///   <item>定位速度：D 区 Float32（占 2 字）→ <c>recipe-config.json</c> 的 <c>parameters</c></item>
///   <item>当前位置：D 区 Float32（占 2 字）→ <c>tags.json</c> 的 <c>Axis{轴}Position</c></item>
///   <item>正反转点动：M 区线圈或 D/R 区字内位（如 D1210.2，汇川位打包）→
///   <c>tags.json</c> 的 <c>Jog{轴}Forward</c> / <c>Jog{轴}Reverse</c></item>
///   <item>点动速度档位：D 区 Int16（占 1 字，写 1/2/3）→ <c>recipe-config.json</c> 的 <c>jogSpeedLevels</c></item>
///   <item>轴回零：M 区线圈或 D/R 字内位 → <c>recipe-config.json</c> 的 <c>homeButtons</c></item>
/// </list>
///
/// <para><b>保存后立即生效</b>：原子落盘 → 重载点位定义 → 重建采集计划，不需要重启程序。
/// 这一步很关键——读取计划只在启动时编译一次，不重建就会出现"地址改了却不生效"。</para>
/// </summary>
public sealed class AxisAddressService : IAxisAddressService
{
    /// <summary>定位速度参数键的后缀，如 <c>X.LocateSpeed</c>。</summary>
    private const string positioningSpeedSuffix = ".LocateSpeed";

    /// <summary>轴顺序兜底：配置里读不到参数时用它（点胶锁付机六轴）。</summary>
    private static readonly string[] defaultAxisOrder = ["X", "Y", "Z", "ZZ", "GlueX", "GlueZ"];

    private readonly RecipeConfig _config;
    private readonly ITagRepository _tag;
    private readonly IPollingEngine _polling;
    private readonly ILogger<AxisAddressService> _log;
    private readonly AxisAddressValidator _validator;

    /// <summary>改绑定地址属管理员专有：地址写错，轴会跑到别的寄存器上去。</summary>
    private readonly IPermissionGuard _permission;

    public AxisAddressService(
        RecipeConfig configPairObject,
        ITagRepository tagList,
        IAddressParser Parser,
        IPollingEngine Polling,
        IPermissionGuard Permission,
        ILogger<AxisAddressService> Log)
    {
        _config = configPairObject;
        _tag = tagList;
        _polling = Polling;
        _permission = Permission;
        _log = Log;
        _validator = new AxisAddressValidator(Parser);

        DataGrid = Build();
    }

    /// <inheritdoc />
    public AxisAddressTable DataGrid { get; private set; }

    /// <inheritdoc />
    public event EventHandler? AddressChanged;

    /// <inheritdoc />
    public AxisAddressValidate Validate(AxisAddressTable candidate) => _validator.Validate(candidate);

    /// <inheritdoc />
    public async Task SaveAsync(AxisAddressTable candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        // 权限先于校验：越权调用不应留下任何状态，也不该看到校验细节
        _permission.WantRequire(Permission.EditBindAddress);

        var validateItem = Validate(candidate);

        if (!validateItem.IsValid)
        {
            throw new InvalidOperationException($"轴地址校验未通过：{validateItem.Summary}");
        }

        // 先规范化再写：校验允许 D 区字段省前缀（现场直接填 1252），
        // 但写进 tags.json 的必须是能解析的软元件符号（见 规范寄存器 的注释）
        RuleRangeAddress(candidate);

        ApplyConfig(candidate);
        ApplyTag(candidate);

        // 两个文件分别原子落盘：极端情况只会有其中一份生效，界面重载后提示重新保存，
        // 这比"先截断再写"导致文件损坏要安全得多。
        await SaveRecipeConfigAsync(cancellationToken).ConfigureAwait(false);
        await _tag.SaveAsync(cancellationToken).ConfigureAwait(false);

        // 点位定义已变，采集计划必须重建，否则新地址要等重启才生效
        await _polling.RebuildPlanListAsync(cancellationToken).ConfigureAwait(false);

        DataGrid = Build();

        _log.LogInformation("轴地址已保存并生效（{Count} 个轴）", DataGrid.AxisList.Count);
        AddressChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── 读取当前地址 ────────────────────────────────────────────────────

    private AxisAddressTable Build()
    {
        var GridItem = new AxisAddressTable();

        foreach (var axisNumber in ParseAxisOrder())
        {
            var specItem = _config.Find($"{axisNumber}{positioningSpeedSuffix}");
            var Level = _config.FindJogSpeedLevel(axisNumber);
            var jogItem = FindJog(axisNumber);
            var Position = _tag.Find(PositionTagName(axisNumber));
            var forwardTag = _tag.Find(AxisTagName.JogForward(axisNumber));
            var reverseTag = _tag.Find(AxisTagName.JogReverse(axisNumber));

            GridItem.AxisList.Add(new AxisAddressEntry
            {
                Axis = axisNumber,
                Name = AxisShowName(axisNumber),
                PositioningSpeedAddress = specItem is null ? string.Empty : FormatDArea(specItem.Address),
                PositioningSpeedVariable = specItem?.Variable ?? string.Empty,
                JogSpeedLevelAddress = Level is null ? string.Empty : FormatDArea(Level.Address),
                PositionAddress = Position?.Address ?? string.Empty,

                // 点动：配置（recipe-config 的 jogButtons）优先，缺项时退回 tags.json 的 Jog{轴}*
                JogForwardAddress = jogItem?.ForwardAddress ?? forwardTag?.Address ?? string.Empty,
                JogReverseAddress = jogItem?.ReverseAddress ?? reverseTag?.Address ?? string.Empty,
                HomeAddress = FindHome(axisNumber)?.Address ?? string.Empty,
            });
        }

        return GridItem;
    }

    /// <summary>
    /// 轴顺序取自参数配置中的定位速度项；读不到时回退到固定的六轴。
    /// <para>保留参数键里的原始大小写（如 <c>GlueX</c>），界面上显示的是现场口径。</para>
    /// </summary>
    private IReadOnlyList<string> ParseAxisOrder()
    {
        var axisCollection = _config.Parameter
            .Where(PointItem => PointItem.Key.EndsWith(positioningSpeedSuffix, StringComparison.OrdinalIgnoreCase))
            .Select(PointItem => PointItem.Key[..^positioningSpeedSuffix.Length])
            .Where(addressValue => addressValue.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return axisCollection.Count > 0 ? axisCollection : defaultAxisOrder;
    }

    // ── 规范化与写回配置 ────────────────────────────────────────────────

    /// <summary>
    /// 把候选表里的地址规范成"写进配置文件所需的书写形式"。
    ///
    /// <para><b>为什么必须在落盘前做：</b>校验允许 D 区字段省掉前缀（现场习惯把 <c>D1202</c>
    /// 全选改成 <c>1252</c>），而位置地址会被<b>原样写进 <c>tags.json</c></b>——那里是软元件符号，
    /// 写进裸数字 <c>1221</c> 会让地址解析器解析失败、采集计划静默跳过该点位
    /// （"改了地址却不生效"，最难查的一类）。寄存器类字段一律补全 <c>D</c> 前缀，
    /// 命令类字段（点动 / 回零）保持现场写法（M 区线圈 / D 区字内位）。</para>
    /// </summary>
    private void RuleRangeAddress(AxisAddressTable candidate)
    {
        foreach (var EntryItem in candidate.AxisList)
        {
            EntryItem.PositioningSpeedAddress = RuleRangeRegister(EntryItem.PositioningSpeedAddress);
            EntryItem.PositionAddress = RuleRangeRegister(EntryItem.PositionAddress);
            EntryItem.JogSpeedLevelAddress = RuleRangeRegister(EntryItem.JogSpeedLevelAddress);

            EntryItem.JogForwardAddress = EntryItem.JogForwardAddress.Trim();
            EntryItem.JogReverseAddress = EntryItem.JogReverseAddress.Trim();
            EntryItem.HomeAddress = EntryItem.HomeAddress.Trim();
        }
    }

    /// <summary>
    /// D 区寄存器地址规范为 <c>D{序号}</c>（如 <c>1252</c> → <c>D1252</c>）；留空原样返回。
    ///
    /// <para>用与 <see cref="ApplyConfig"/> 同一套宽松解析（容忍省略 <c>D</c> 前缀），
    /// 不能换成地址解析器——它要求写明软元件前缀，会把裸数字当成"解析失败"原样放过，
    /// 于是 <c>tags.json</c> 里落下一个采集计划认不出的地址。</para>
    ///
    /// <para>调用前必已通过校验；仍做防御性回退：解析不出来时保留原文，
    /// 而不是编一个"看着合法"的 <c>D0</c>。</para>
    /// </summary>
    private static string RuleRangeRegister(string addressValue)
    {
        var Text = addressValue.Trim();

        if (Text.Length == 0)
        {
            return string.Empty;
        }

        return int.TryParse(Text.TrimStart('D', 'd'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var Sequence)
            ? FormatDArea(Sequence)
            : Text;
    }

    private void ApplyConfig(AxisAddressTable candidate)
    {
        foreach (var EntryItem in candidate.AxisList)
        {
            if (_config.Find($"{EntryItem.Axis}{positioningSpeedSuffix}") is { } specItem
                && TryParse(EntryItem.PositioningSpeedAddress, out var speedAddress))
            {
                specItem.Address = speedAddress;
                var VariableName = EntryItem.PositioningSpeedVariable.Trim();
                specItem.Variable = VariableName.Length == 0 ? null : VariableName;
            }

            if (_config.FindJogSpeedLevel(EntryItem.Axis) is { } Level
                && TryParse(EntryItem.JogSpeedLevelAddress, out var levelAddress))
            {
                Level.Address = levelAddress;
            }

            // 点动方向：配置驱动的新轴直接写 recipe-config 的 jogButtons
            if (FindJog(EntryItem.Axis) is { } jogItem)
            {
                jogItem.ForwardAddress = EntryItem.JogForwardAddress.Trim();
                jogItem.ReverseAddress = EntryItem.JogReverseAddress.Trim();
            }

            // 回零：地址留空表示该轴不提供回零按钮（条目仍在，便于日后补填）
            if (FindHome(EntryItem.Axis) is { } homeItem)
            {
                homeItem.Address = EntryItem.HomeAddress.Trim();
            }
            else if (!string.IsNullOrWhiteSpace(EntryItem.HomeAddress))
            {
                _config.Home.Add(new HomeButtonItem
                {
                    Axis = EntryItem.Axis,
                    Name = $"{AxisShowName(EntryItem.Axis)}回零",
                    Address = EntryItem.HomeAddress.Trim(),
                });
            }
        }
    }

    /// <summary>按轴查找回零按钮配置（大小写不敏感）。</summary>
    private HomeButtonItem? FindHome(string axisNumber) =>
        _config.Home.FirstOrDefault(Item => string.Equals(Item.Axis, axisNumber, StringComparison.OrdinalIgnoreCase));

    /// <summary>轴的界面显示名（现场口径：锁附 X/Y/Z/ZZ、点胶 X/Z）。</summary>
    private static string AxisShowName(string axisNumber) => axisNumber.ToUpperInvariant() switch
    {
        "X" => "锁附X轴",
        "Y" => "锁附Y轴",
        "Z" => "锁附Z轴",
        "ZZ" => "锁附ZZ轴",
        "GLUEX" => "点胶X轴",
        "GLUEZ" => "点胶Z轴",
        _ => $"{axisNumber}轴",
    };

    private void ApplyTag(AxisAddressTable candidate)
    {
        foreach (var EntryItem in candidate.AxisList)
        {
            UpdateTag(PositionTagName(EntryItem.Axis), EntryItem.PositionAddress);

            // 配置里有该轴的点动项时，点动地址已在 应用配置 写进 jogButtons；
            // 只有 tags.json 里的老四轴（X/Y/Z/R）才需要写回标签定义。
            if (FindJog(EntryItem.Axis) is null)
            {
                UpdateTag(AxisTagName.JogForward(EntryItem.Axis), EntryItem.JogForwardAddress);
                UpdateTag(AxisTagName.JogReverse(EntryItem.Axis), EntryItem.JogReverseAddress);
            }
        }
    }

    /// <summary>该轴位置回读的点位名：优先用配置点动项声明的标签，缺省回退 <c>Axis{轴}Position</c>。</summary>
    private string PositionTagName(string axisNumber)
    {
        var jogItem = FindJog(axisNumber);
        return string.IsNullOrWhiteSpace(jogItem?.PositionTag) ? AxisTagName.Position(axisNumber) : jogItem!.PositionTag!;
    }

    /// <summary>按轴查找配置里的点动项（首个匹配，大小写不敏感）。</summary>
    private JogButtonItem? FindJog(string axisNumber) =>
        _config.Jog.FirstOrDefault(Item => string.Equals(Item.Axis, axisNumber, StringComparison.OrdinalIgnoreCase));

    private void UpdateTag(string nameValue, string addressValue)
    {
        // 留空表示该轴没有这个点位（如点胶 Z、ZZ 无独立位置回读）：不写、也不告警
        if (string.IsNullOrWhiteSpace(addressValue))
        {
            return;
        }

        if (!_tag.TryUpdateAddress(nameValue, addressValue))
        {
            // 校验已保证"填了地址就必须能写"；走到这里说明 tags.json 里缺该点位，记警告而非静默跳过
            _log.LogWarning("点位 {Name} 不存在，地址 {Address} 未写入", nameValue, addressValue);
        }
    }

    private async Task SaveRecipeConfigAsync(CancellationToken cancellationToken)
    {
        try
        {
            // 用 camelCase 口径写：recipe-config.json 是人工手写的，别让程序把风格翻成 PascalCase
            var jsonText = JsonSerializer.Serialize(_config, JsonConfig.writeHumanStyle);
            await AtomicFile.WriteAllTextAsync(ConfigPath.RecipeConfig, jsonText, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "保存 recipe-config.json 失败：{Path}", ConfigPath.RecipeConfig);
            throw;
        }
    }

    // 校验规则（重叠 / 位感知比较 / PLC 保留区 / 轴参数区内部字段）见 轴地址校验器。
    // 那是"填错就撞机"的部分，单独一个类便于一次看全。

    // ── 格式 ────────────────────────────────────────────────────────────

    /// <summary>把 D 区序号格式化为界面书写的软元件符号，如 1202 → <c>D1202</c>。</summary>
    private static string FormatDArea(int addressValue) =>
        "D" + addressValue.ToString(CultureInfo.InvariantCulture);

    /// <summary>解析界面输入的地址序号（容忍带 D / M 前缀的写法）。</summary>
    private static bool TryParse(string Text, out int Value) =>
        int.TryParse(Text.TrimStart('D', 'd', 'M', 'm'), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out Value);
}
