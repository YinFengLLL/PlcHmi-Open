using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using 上位机.业务.配置;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

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
public sealed class 轴地址服务 : 轴地址服务接口
{
    /// <summary>定位速度参数键的后缀，如 <c>X.LocateSpeed</c>。</summary>
    private const string 定位速度后缀 = ".LocateSpeed";

    /// <summary>轴顺序兜底：配置里读不到参数时用它（点胶锁付机六轴）。</summary>
    private static readonly string[] 默认轴顺序 = ["X", "Y", "Z", "ZZ", "GlueX", "GlueZ"];

    private readonly 配方配置 _配置;
    private readonly 标签仓储接口 _标签;
    private readonly 轮询引擎接口 _轮询;
    private readonly ILogger<轴地址服务> _日志;
    private readonly 轴地址校验器 _校验器;

    /// <summary>改绑定地址属管理员专有：地址写错，轴会跑到别的寄存器上去。</summary>
    private readonly 权限守卫接口 _权限;

    public 轴地址服务(
        配方配置 配置对象,
        标签仓储接口 标签列表,
        地址解析器接口 解析器,
        轮询引擎接口 轮询,
        权限守卫接口 权限,
        ILogger<轴地址服务> 日志)
    {
        _配置 = 配置对象;
        _标签 = 标签列表;
        _轮询 = 轮询;
        _权限 = 权限;
        _日志 = 日志;
        _校验器 = new 轴地址校验器(解析器);

        表格 = 构建();
    }

    /// <inheritdoc />
    public 轴地址表格 表格 { get; private set; }

    /// <inheritdoc />
    public event EventHandler? 地址已变更;

    /// <inheritdoc />
    public 轴地址校验 校验(轴地址表格 候选) => _校验器.校验(候选);

    /// <inheritdoc />
    public async Task 异步保存(轴地址表格 候选, CancellationToken 取消令牌 = default)
    {
        ArgumentNullException.ThrowIfNull(候选);

        // 权限先于校验：越权调用不应留下任何状态，也不该看到校验细节
        _权限.要求(权限.编辑绑定地址);

        var 校验项 = 校验(候选);

        if (!校验项.是否有效)
        {
            throw new InvalidOperationException($"轴地址校验未通过：{校验项.摘要}");
        }

        // 先规范化再写：校验允许 D 区字段省前缀（现场直接填 1252），
        // 但写进 tags.json 的必须是能解析的软元件符号（见 规范寄存器 的注释）
        规范地址(候选);

        应用配置(候选);
        应用标签(候选);

        // 两个文件分别原子落盘：极端情况只会有其中一份生效，界面重载后提示重新保存，
        // 这比"先截断再写"导致文件损坏要安全得多。
        await 异步保存配方配置(取消令牌).ConfigureAwait(false);
        await _标签.异步保存(取消令牌).ConfigureAwait(false);

        // 点位定义已变，采集计划必须重建，否则新地址要等重启才生效
        await _轮询.异步重建方案列表(取消令牌).ConfigureAwait(false);

        表格 = 构建();

        _日志.LogInformation("轴地址已保存并生效（{Count} 个轴）", 表格.轴列表.Count);
        地址已变更?.Invoke(this, EventArgs.Empty);
    }

    // ── 读取当前地址 ────────────────────────────────────────────────────

    private 轴地址表格 构建()
    {
        var 表格项 = new 轴地址表格();

        foreach (var 轴号 in 解析轴顺序())
        {
            var 规格项 = _配置.查找($"{轴号}{定位速度后缀}");
            var 等级 = _配置.查找点动速度等级(轴号);
            var 点动项 = 查找点动(轴号);
            var 位置 = _标签.查找(位置标签名(轴号));
            var 正转标签 = _标签.查找(轴标签名.点动正转(轴号));
            var 反转标签 = _标签.查找(轴标签名.点动反转(轴号));

            表格项.轴列表.Add(new 轴地址条目
            {
                轴 = 轴号,
                名称 = 轴显示名(轴号),
                定位速度地址 = 规格项 is null ? string.Empty : 格式化D区(规格项.地址),
                定位速度变量 = 规格项?.变量 ?? string.Empty,
                点动速度等级地址 = 等级 is null ? string.Empty : 格式化D区(等级.地址),
                位置地址 = 位置?.地址 ?? string.Empty,

                // 点动：配置（recipe-config 的 jogButtons）优先，缺项时退回 tags.json 的 Jog{轴}*
                点动正转地址 = 点动项?.正转地址 ?? 正转标签?.地址 ?? string.Empty,
                点动反转地址 = 点动项?.反转地址 ?? 反转标签?.地址 ?? string.Empty,
                回零地址 = 查找回零(轴号)?.地址 ?? string.Empty,
            });
        }

        return 表格项;
    }

    /// <summary>
    /// 轴顺序取自参数配置中的定位速度项；读不到时回退到固定的六轴。
    /// <para>保留参数键里的原始大小写（如 <c>GlueX</c>），界面上显示的是现场口径。</para>
    /// </summary>
    private IReadOnlyList<string> 解析轴顺序()
    {
        var 轴集合 = _配置.参数
            .Where(点位项 => 点位项.键.EndsWith(定位速度后缀, StringComparison.OrdinalIgnoreCase))
            .Select(点位项 => 点位项.键[..^定位速度后缀.Length])
            .Where(地址值 => 地址值.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return 轴集合.Count > 0 ? 轴集合 : 默认轴顺序;
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
    private void 规范地址(轴地址表格 候选)
    {
        foreach (var 条目项 in 候选.轴列表)
        {
            条目项.定位速度地址 = 规范寄存器(条目项.定位速度地址);
            条目项.位置地址 = 规范寄存器(条目项.位置地址);
            条目项.点动速度等级地址 = 规范寄存器(条目项.点动速度等级地址);

            条目项.点动正转地址 = 条目项.点动正转地址.Trim();
            条目项.点动反转地址 = 条目项.点动反转地址.Trim();
            条目项.回零地址 = 条目项.回零地址.Trim();
        }
    }

    /// <summary>
    /// D 区寄存器地址规范为 <c>D{序号}</c>（如 <c>1252</c> → <c>D1252</c>）；留空原样返回。
    ///
    /// <para>用与 <see cref="应用配置"/> 同一套宽松解析（容忍省略 <c>D</c> 前缀），
    /// 不能换成地址解析器——它要求写明软元件前缀，会把裸数字当成"解析失败"原样放过，
    /// 于是 <c>tags.json</c> 里落下一个采集计划认不出的地址。</para>
    ///
    /// <para>调用前必已通过校验；仍做防御性回退：解析不出来时保留原文，
    /// 而不是编一个"看着合法"的 <c>D0</c>。</para>
    /// </summary>
    private static string 规范寄存器(string 地址值)
    {
        var 文本 = 地址值.Trim();

        if (文本.Length == 0)
        {
            return string.Empty;
        }

        return int.TryParse(文本.TrimStart('D', 'd'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var 序号)
            ? 格式化D区(序号)
            : 文本;
    }

    private void 应用配置(轴地址表格 候选)
    {
        foreach (var 条目项 in 候选.轴列表)
        {
            if (_配置.查找($"{条目项.轴}{定位速度后缀}") is { } 规格项
                && 尝试解析(条目项.定位速度地址, out var 速度地址))
            {
                规格项.地址 = 速度地址;
                var 变量名 = 条目项.定位速度变量.Trim();
                规格项.变量 = 变量名.Length == 0 ? null : 变量名;
            }

            if (_配置.查找点动速度等级(条目项.轴) is { } 等级
                && 尝试解析(条目项.点动速度等级地址, out var 等级地址))
            {
                等级.地址 = 等级地址;
            }

            // 点动方向：配置驱动的新轴直接写 recipe-config 的 jogButtons
            if (查找点动(条目项.轴) is { } 点动项)
            {
                点动项.正转地址 = 条目项.点动正转地址.Trim();
                点动项.反转地址 = 条目项.点动反转地址.Trim();
            }

            // 回零：地址留空表示该轴不提供回零按钮（条目仍在，便于日后补填）
            if (查找回零(条目项.轴) is { } 回零项)
            {
                回零项.地址 = 条目项.回零地址.Trim();
            }
            else if (!string.IsNullOrWhiteSpace(条目项.回零地址))
            {
                _配置.回零.Add(new 回零按钮项
                {
                    轴 = 条目项.轴,
                    名称 = $"{轴显示名(条目项.轴)}回零",
                    地址 = 条目项.回零地址.Trim(),
                });
            }
        }
    }

    /// <summary>按轴查找回零按钮配置（大小写不敏感）。</summary>
    private 回零按钮项? 查找回零(string 轴号) =>
        _配置.回零.FirstOrDefault(项 => string.Equals(项.轴, 轴号, StringComparison.OrdinalIgnoreCase));

    /// <summary>轴的界面显示名（现场口径：锁附 X/Y/Z/ZZ、点胶 X/Z）。</summary>
    private static string 轴显示名(string 轴号) => 轴号.ToUpperInvariant() switch
    {
        "X" => "锁附X轴",
        "Y" => "锁附Y轴",
        "Z" => "锁附Z轴",
        "ZZ" => "锁附ZZ轴",
        "GLUEX" => "点胶X轴",
        "GLUEZ" => "点胶Z轴",
        _ => $"{轴号}轴",
    };

    private void 应用标签(轴地址表格 候选)
    {
        foreach (var 条目项 in 候选.轴列表)
        {
            更新标签(位置标签名(条目项.轴), 条目项.位置地址);

            // 配置里有该轴的点动项时，点动地址已在 应用配置 写进 jogButtons；
            // 只有 tags.json 里的老四轴（X/Y/Z/R）才需要写回标签定义。
            if (查找点动(条目项.轴) is null)
            {
                更新标签(轴标签名.点动正转(条目项.轴), 条目项.点动正转地址);
                更新标签(轴标签名.点动反转(条目项.轴), 条目项.点动反转地址);
            }
        }
    }

    /// <summary>该轴位置回读的点位名：优先用配置点动项声明的标签，缺省回退 <c>Axis{轴}Position</c>。</summary>
    private string 位置标签名(string 轴号)
    {
        var 点动项 = 查找点动(轴号);
        return string.IsNullOrWhiteSpace(点动项?.位置标签) ? 轴标签名.位置(轴号) : 点动项!.位置标签!;
    }

    /// <summary>按轴查找配置里的点动项（首个匹配，大小写不敏感）。</summary>
    private 点动按钮项? 查找点动(string 轴号) =>
        _配置.点动.FirstOrDefault(项 => string.Equals(项.轴, 轴号, StringComparison.OrdinalIgnoreCase));

    private void 更新标签(string 名称值, string 地址值)
    {
        // 留空表示该轴没有这个点位（如点胶 Z、ZZ 无独立位置回读）：不写、也不告警
        if (string.IsNullOrWhiteSpace(地址值))
        {
            return;
        }

        if (!_标签.尝试更新地址(名称值, 地址值))
        {
            // 校验已保证"填了地址就必须能写"；走到这里说明 tags.json 里缺该点位，记警告而非静默跳过
            _日志.LogWarning("点位 {Name} 不存在，地址 {Address} 未写入", 名称值, 地址值);
        }
    }

    private async Task 异步保存配方配置(CancellationToken 取消令牌)
    {
        try
        {
            // 用 camelCase 口径写：recipe-config.json 是人工手写的，别让程序把风格翻成 PascalCase
            var JSON文本 = JsonSerializer.Serialize(_配置, JSON配置.写入人工风格);
            await 原子文件.异步写入全部文本(配置路径.配方配置, JSON文本, 取消令牌).ConfigureAwait(false);
        }
        catch (Exception 异常)
        {
            _日志.LogError(异常, "保存 recipe-config.json 失败：{Path}", 配置路径.配方配置);
            throw;
        }
    }

    // 校验规则（重叠 / 位感知比较 / PLC 保留区 / 轴参数区内部字段）见 轴地址校验器。
    // 那是"填错就撞机"的部分，单独一个类便于一次看全。

    // ── 格式 ────────────────────────────────────────────────────────────

    /// <summary>把 D 区序号格式化为界面书写的软元件符号，如 1202 → <c>D1202</c>。</summary>
    private static string 格式化D区(int 地址值) =>
        "D" + 地址值.ToString(CultureInfo.InvariantCulture);

    /// <summary>解析界面输入的地址序号（容忍带 D / M 前缀的写法）。</summary>
    private static bool 尝试解析(string 文本, out int 值) =>
        int.TryParse(文本.TrimStart('D', 'd', 'M', 'm'), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out 值);
}
