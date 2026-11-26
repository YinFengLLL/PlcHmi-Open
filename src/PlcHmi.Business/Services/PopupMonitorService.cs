using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using 上位机.业务.配置;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// 弹窗按钮的工作模式（2026-10-17 现场要求三选一）。
/// </summary>
public enum 弹窗按钮模式
{
    /// <summary>
    /// 复归型：<b>按住置位、松开复位</b>——给 PLC 一个脉冲。
    /// <para>典型用途："确认"按钮被按住期间保持置位，PLC 程序用这个电平去复位触发线圈，
    /// 于是弹窗消失。安全相关的手势都用这种（不会因为一次误触留下置位）。</para>
    /// </summary>
    复归型 = 0,

    /// <summary>
    /// 置位 ON：<b>点一次即置位并保持</b>。
    /// <para>典型用途：打开某个功能开关（"启动输送"），弹窗只是提示与操作入口。</para>
    /// </summary>
    置位ON = 1,

    /// <summary>
    /// 复位 OFF：<b>点一次即复位并保持</b>。
    /// <para>典型用途：关闭某个功能开关、清除某个状态（"关闭输送"）。</para>
    /// </summary>
    复位OFF = 2,
}

/// <summary>
/// 一个可配置弹窗（现场口径 2026-10-13）：
/// <list type="bullet">
/// <item><b>触发</b>：M 区线圈地址有效（=ON）时，主窗居中显示该弹窗；线圈复位后自动消失。</item>
/// <item><b>内容</b>：文案自定义。</item>
/// <item><b>两个按钮</b>：名称自定义，各对应一个 M 区线圈；工作模式三选一
/// （<see cref="弹窗按钮模式.复归型"/> / <see cref="弹窗按钮模式.置位ON"/> / <see cref="弹窗按钮模式.复位OFF"/>）。</item>
/// </list>
/// </summary>
public sealed class 弹窗定义
{
    /// <summary>唯一键（新增时自动生成）。</summary>
    public string 键 { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>弹窗标题。</summary>
    public string 名称 { get; set; } = string.Empty;

    /// <summary>触发线圈地址（如 M100）：ON 显示，OFF 消失。</summary>
    public string 触发地址 { get; set; } = string.Empty;

    /// <summary>弹窗正文（自定义文案）。</summary>
    public string 内容 { get; set; } = string.Empty;

    /// <summary>按钮一名称（自定义）。</summary>
    public string 按钮一名称 { get; set; } = "确认";

    /// <summary>按钮一对应的 M 线圈（可为空 = 不显示第一颗按钮）。</summary>
    public string 按钮一地址 { get; set; } = string.Empty;

    /// <summary>按钮一的工作模式（复归型 / 置位 ON / 复位 OFF）。</summary>
    public 弹窗按钮模式 按钮一模式 { get; set; } = 弹窗按钮模式.复归型;

    /// <summary>按钮二名称（自定义，可为空 = 不显示第二颗按钮）。</summary>
    public string 按钮二名称 { get; set; } = string.Empty;

    /// <summary>按钮二对应的 M 线圈（可为空）。</summary>
    public string 按钮二地址 { get; set; } = string.Empty;

    /// <summary>按钮二的工作模式（复归型 / 置位 ON / 复位 OFF）。</summary>
    public 弹窗按钮模式 按钮二模式 { get; set; } = 弹窗按钮模式.复归型;

    /// <summary>按钮一模式的中文说明（供界面提示）。</summary>
    [JsonIgnore]
    public string 按钮一模式说明 => 模式说明(按钮一模式);

    /// <summary>按钮二模式的中文说明（供界面提示）。</summary>
    [JsonIgnore]
    public string 按钮二模式说明 => 模式说明(按钮二模式);

    /// <summary>模式 → 给操作员看的说明。</summary>
    public static string 模式说明(弹窗按钮模式 模式) => 模式 switch
    {
        弹窗按钮模式.置位ON => "点一次即置位（ON）并保持",
        弹窗按钮模式.复位OFF => "点一次即复位（OFF）并保持",
        _ => "复归型：按住置位、松开复位",
    };

    /// <summary>模式下拉的选项文本（顺序即枚举值顺序，界面按索引回填）。</summary>
    public static IReadOnlyList<string> 模式选项 { get; } = ["复归型", "置位 ON", "复位 OFF"];
}

/// <summary><c>config/popups.json</c> 的顶层结构。</summary>
public sealed class 弹窗文件
{
    public List<弹窗定义> 弹窗集合 { get; set; } = new();
}

/// <summary>
/// 弹窗监控服务：装载 <c>config/popups.json</c>，连接建立后轮询各弹窗的<b>触发线圈</b>，
/// 触发时上报；并提供按钮的复归型写入（按住置位 / 松开复位）。
///
/// <para><b>为什么自己轮询而不用轮询引擎：</b>轮询引擎按 <c>tags.json</c> 的固定点位工作，
/// 而弹窗触发地址是管理员随时在配置文件里增删的——动态点位自己读最稳（每次只读 1 个线圈，
/// 弹窗数量是"个位数"量级，600ms 一轮的开销可忽略）。</para>
/// </summary>
public sealed partial class 弹窗监控服务
{
    /// <summary>触发线圈地址解析结果（把字符串地址换算成 Modbus 线圈号）。</summary>
    private sealed record 线圈定位(string 键, int 基址, bool 解析成功);

    private static readonly string 存储路径 =
        Path.Combine(配置路径.配置目录, "popups.json");

    private static readonly JsonSerializerOptions 缩进 = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Modbus客户端接口 _客户端;
    private readonly 地址解析器接口 _解析器;
    private readonly 权限守卫接口 _权限;
    private readonly ILogger<弹窗监控服务> _日志;

    private readonly List<弹窗定义> _弹窗 = new();
    private readonly object _同步 = new();

    /// <summary>触发状态变化（当前触发的弹窗键，null = 无）。</summary>
    public event EventHandler<string?>? 触发已变更;

    /// <summary>当前触发的弹窗键；null = 无。</summary>
    public string? 当前触发键 { get; private set; }

    public 弹窗监控服务(
        Modbus客户端接口 客户端,
        地址解析器接口 解析器,
        权限守卫接口 权限,
        ILogger<弹窗监控服务> 日志)
    {
        _客户端 = 客户端;
        _解析器 = 解析器;
        _权限 = 权限;
        _日志 = 日志;

        装载配置();

        // 轮询循环随本服务（单例）常驻，**不等连接事件**。
        //
        // 曾经的写法是"订阅连接状态已变更，转为已连接时启动循环"，
        // 结果是现场一开机就没有弹窗：本服务第一次被解析（主窗 ViewModel 构造）时
        // PLC 往往已经连上了，之后不再有"转为已连接"的事件 —— 循环永远不启动。
        // 现在改为常驻循环，每一轮自己判断连接状态，断开跳过、重连自动继续。
        _ = 轮询循环();
    }

    /// <summary>当前生效的弹窗定义集合（配置文件的只读视图）。</summary>
    public IReadOnlyList<弹窗定义> 弹窗集合
    {
        get { lock (_同步) { return _弹窗.ToList(); } }
    }

    /// <summary>重读配置文件（编辑器保存后调用）。</summary>
    public void 重读配置()
    {
        lock (_同步)
        {
            装载配置();
        }
    }

    /// <summary>把弹窗集合写回配置文件（原子写 + .bak，与其它配置一致）。</summary>
    public async Task 异步保存集合(IReadOnlyList<弹窗定义> 弹窗, CancellationToken 取消令牌 = default)
    {
        // R5：写配置走服务层权限二次校验。弹窗会改变界面行为与 PLC 写入目标，
        // 与手动元件（编辑绑定地址）同口径，仅管理员
        _权限.要求(权限.编辑绑定地址);

        Directory.CreateDirectory(配置路径.配置目录);

        var 新文件 = new 弹窗文件 { 弹窗集合 = 弹窗.ToList() };

        // R3：必须走 原子文件（临时文件 + 替换 + .bak 回滚）。
        // 曾经的 File.WriteAllTextAsync 是"先截断再写"，断电/被杀即半截文件——
        // 这是全工程唯一未走原子写的配置写路径。
        var JSON文本 = JsonSerializer.Serialize(新文件, 缩进);
        await 原子文件.异步写入全部文本(存储路径, JSON文本, 取消令牌).ConfigureAwait(false);

        重读配置();
    }

    /// <summary>
    /// 按下按钮（鼠标 / 触摸按下那一刻）。
    /// <para><b>按模式分流：</b>复归型与置位 ON 都写 <c>true</c>；复位 OFF 写 <c>false</c>。
    /// 三种模式在"按下"时都已完成一次完整写入——<b>单击即可生效</b>，
    /// 不需要按住不放（复归型的区别只在"松开时再写一次"）。</para>
    /// </summary>
    public async Task 异步按下(弹窗定义 定义, int 按钮序号, CancellationToken 取消令牌 = default)
    {
        await 写按钮线圈(定义, 按钮序号, 目标状态(定义, 按钮序号), 取消令牌).ConfigureAwait(false);
    }

    /// <summary>
    /// 松开按钮。<b>只有复归型需要</b>——按住期间置位、松开时复位，给 PLC 一个干净的电平静变。
    /// <para>置位 ON / 复位 OFF 是"点一次就保持"的开关量，松开不能再写回去
    /// （那会把刚置上的位又抹掉，现场表现为"按钮按了没反应"）。</para>
    /// </summary>
    public async Task 异步松开(弹窗定义 定义, int 按钮序号, CancellationToken 取消令牌 = default)
    {
        if (取模式(定义, 按钮序号) is not 弹窗按钮模式.复归型)
        {
            return;
        }

        await 写按钮线圈(定义, 按钮序号, false, 取消令牌).ConfigureAwait(false);
    }

    /// <summary>取某个按钮的工作模式。</summary>
    private static 弹窗按钮模式 取模式(弹窗定义 定义, int 按钮序号) =>
        按钮序号 == 0 ? 定义.按钮一模式 : 定义.按钮二模式;

    /// <summary>按下时该写入的状态：复归型与置位 ON → true，复位 OFF → false。</summary>
    private static bool 目标状态(弹窗定义 定义, int 按钮序号) =>
        取模式(定义, 按钮序号) is 弹窗按钮模式.复位OFF ? false : true;

    private async Task 写按钮线圈(弹窗定义 定义, int 按钮序号, bool 置位, CancellationToken 取消令牌)
    {
        var 地址文本 = 按钮序号 is 0 ? 定义.按钮一地址 : 定义.按钮二地址;

        if (解析线圈(地址文本) is not { } 基址)
        {
            throw new InvalidOperationException($"按钮{(按钮序号 + 1)}地址「{地址文本}」不是有效的 M 线圈地址。");
        }

        if (!_客户端.是否已连接)
        {
            throw new InvalidOperationException("PLC 未连接，无法操作弹窗按钮。");
        }

        await _客户端.异步写入单个线圈(基址, 置位, 取消令牌).ConfigureAwait(false);
    }


    /// <summary>
    /// 把 "M123" 解析成 Modbus 线圈号（基址按机型系列换算，与点动/回零同一套规则）。
    /// <para>只接受 <b>M 区</b>：弹窗的触发与按钮一律走内部继电器，由 PLC 程序决定对外输出——
    /// 这是全工程一致的安全边界（见 手动控制服务 的同名约束）。</para>
    /// </summary>
    private int? 解析线圈(string 地址文本)
    {
        if (string.IsNullOrWhiteSpace(地址文本))
        {
            return null;
        }

        if (!_解析器.尝试解析(地址文本.Trim(), out var PLC地址) || PLC地址 is null)
        {
            return null;
        }

        return PLC地址.元素 is 软元素.M ? PLC地址.Modbus地址 : null;
    }

    /// <summary>地址校验（编辑器用）：返回 null = 合法，否则返回给管理员看的原因。</summary>
    public static string? 校验地址(地址解析器接口 解析器, string 地址文本, string 用途)
    {
        if (string.IsNullOrWhiteSpace(地址文本))
        {
            return null;
        }

        if (!解析器.尝试解析(地址文本.Trim(), out var PLC地址) || PLC地址 is null)
        {
            return $"{用途}「{地址文本}」不是有效的软元件地址。";
        }

        return PLC地址.元素 is 软元素.M
            ? null
            : $"{用途}「{地址文本}」必须是 M 区地址（当前解析为 {PLC地址.元素} 区）。";
    }
}
