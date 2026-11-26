using 上位机.核心.枚举;

namespace 上位机.核心.模型;

/// <summary>
/// 一个定位点的四轴坐标。
///
/// <para><b>精度约定：</b>内存与 JSON 中使用 <see cref="double"/>（精度更高、避免运算累计误差），
/// 仅在读写 PLC 时转换为 <c>float</c>（IEEE 754 单精度，占 2 个寄存器，字序 CDAB）。</para>
/// <para>显示一律走 <c>F2</c> 定点两位小数，避免 <c>9999.99023</c> 这类浮点尾数泄漏到界面。</para>
/// </summary>
public sealed record 示教点位
{
    /// <summary>X 轴坐标。</summary>
    public double X { get; set; }

    /// <summary>Y 轴坐标。</summary>
    public double Y { get; set; }

    /// <summary>Z 轴坐标。</summary>
    public double Z { get; set; }

    /// <summary>R 轴坐标。</summary>
    public double R { get; set; }

    /// <summary>空点（四轴全 0）。</summary>
    public static readonly 示教点位 空 = new();

    /// <summary>
    /// 是否为空点。
    /// <para>与 0 比较是安全的——0 在浮点中是精确可表示值，不存在误差。</para>
    /// </summary>
    public bool 是否空 => X == 0 && Y == 0 && Z == 0 && R == 0;

    /// <summary>按轴读取坐标。</summary>
    public double this[轴标识 轴号] => 轴号 switch
    {
        轴标识.X => X,
        轴标识.Y => Y,
        轴标识.Z => Z,
        轴标识.R => R,
        _ => throw new ArgumentOutOfRangeException(nameof(轴号)),
    };

    /// <summary>按轴写入坐标，返回新的点（原实例不变）。</summary>
    public 示教点位 附带(轴标识 轴号, double 值) => 轴号 switch
    {
        轴标识.X => this with { X = 值 },
        轴标识.Y => this with { Y = 值 },
        轴标识.Z => this with { Z = 值 },
        轴标识.R => this with { R = 值 },
        _ => throw new ArgumentOutOfRangeException(nameof(轴号)),
    };

    /// <summary>四轴是否都在允许范围内。</summary>
    public bool 是否在范围内(double 最小值, double 最大值) =>
        X >= 最小值 && X <= 最大值 &&
        Y >= 最小值 && Y <= 最大值 &&
        Z >= 最小值 && Z <= 最大值 &&
        R >= 最小值 && R <= 最大值;
}

/// <summary>
/// 机种参数项定义。对应 <c>config/recipe-config.json</c> 的 <c>parameters</c> 数组。
/// <para>参数项与地址全部配置化——现场加参数只改 JSON，不动代码。</para>
/// </summary>
public sealed class 配方参数规格
{
    /// <summary>参数键，如 <c>X.LocateSpeed</c>，用于在机种数据中索引取值。</summary>
    public string 键 { get; set; } = string.Empty;

    /// <summary>界面显示名称，如 <c>X 轴定位速度</c>。</summary>
    public string 名称 { get; set; } = string.Empty;

    /// <summary>PLC 寄存器地址（D 区序号），如 1600。</summary>
    public int 地址 { get; set; }

    /// <summary>
    /// 绑定的变量表变量名（可选）。绑定后 <see cref="地址"/> 为变量软元件地址的回填值，
    /// OPC UA 模式下按变量名做符号寻址。
    /// </summary>
    public string? 变量 { get; set; }

    /// <summary>
    /// 数据类型，决定占用字数与编解码方式。
    ///
    /// <para><b>为什么必须是配置项</b>：此前 <c>RecipeService</c> 把参数一律按 <c>UInt16</c> 编码、
    /// 只写 1 个寄存器，而现场约定是 Float32（占 2 字）。「配置说浮点、代码写整数」会让 PLC 读到
    /// 错位的值；又因为原来的地址是连续的，后一项的低字还会踩掉前一项的高字。
    /// 显式声明类型后，这类冲突在结构上不可能再发生。</para>
    /// </summary>
    public 标签数据类型 数据类型 { get; set; } = 标签数据类型.Float32;

    /// <summary>
    /// 本项占用的寄存器数量。
    /// <para>与 <c>IAddressResolver.GetRegisterCount</c> 保持同一套规则，改一处必须改两处。</para>
    /// </summary>
    public int 寄存器数量 => 数据类型 switch
    {
        标签数据类型.Int32 or 标签数据类型.UInt32 or 标签数据类型.Float32 => 2,
        标签数据类型.Float64 => 4,
        _ => 1,
    };

    /// <summary>
    /// 本项占用的寄存器区间（含首不含尾），用于地址重叠校验。
    /// </summary>
    public int 地址结束边界 => 地址 + 寄存器数量;

    /// <summary>工程单位，如 <c>mm/s</c>。</summary>
    public string? 单元 { get; set; }

    /// <summary>允许下限。</summary>
    public double 最小 { get; set; }

    /// <summary>允许上限。</summary>
    public double 最大 { get; set; }

    /// <summary>出厂默认值（「恢复默认」时使用）。</summary>
    public double 默认 { get; set; }

    /// <summary>显示名称（<see cref="名称"/> 为空时回退到 <see cref="键"/>）。</summary>
    public string 显示名称 => string.IsNullOrWhiteSpace(名称) ? 键 : 名称;

    /// <summary>范围提示文本，如 <c>1 ~ 1000</c>。</summary>
    public string 范围文本 => $"{最小:0.###} ~ {最大:0.###}";

    /// <summary>值是否在允许范围内。</summary>
    public bool 是否在范围内(double 值) => 值 >= 最小 && 值 <= 最大;
}

// 机种区段 / 配方信息 / 配方数据 / 参数文件 / 配方文件 见 RecipeModels.Recipes.cs
// （同一命名空间下的独立文件，拆开是为了守住单文件 400 行的规模门禁）。

/// <summary>示教点表配置。对应 <c>config/recipe-config.json</c> 的 <c>teach</c> 节。</summary>
public sealed class 示教配置
{
    /// <summary>X 轴坐标区基址（D 区序号）。</summary>
    public int X基址 { get; set; } = 2500;

    /// <summary>Y 轴坐标区基址。</summary>
    public int Y基址 { get; set; } = 2700;

    /// <summary>Z 轴坐标区基址。</summary>
    public int Z基址 { get; set; } = 2900;

    /// <summary>R 轴坐标区基址。</summary>
    public int R基址 { get; set; } = 3100;

    /// <summary>面数。</summary>
    public int 面数量 { get; set; } = 10;

    /// <summary>每面点数。</summary>
    public int 每面点位数量 { get; set; } = 10;

    /// <summary>坐标数据类型（当前仅支持 Float32）。</summary>
    public string 坐标类型 { get; set; } = "Float32";

    /// <summary>直线轴（X/Y/Z）单位文本。</summary>
    public string 坐标单元 { get; set; } = "MM";

    /// <summary>旋转轴（R）单位文本。</summary>
    public string 旋转单元 { get; set; } = "°";

    /// <summary>坐标下限。</summary>
    public double 最小值 { get; set; } = -999.99;

    /// <summary>坐标上限。</summary>
    public double 最大值 { get; set; } = 9999.99;

    /// <summary>一致性比较容差。<b>浮点不能用等号比</b>，详见 docs/02 第 6.4 节。</summary>
    public double 容差 { get; set; } = 0.005;

    /// <summary>面启用标志区基址（每面 1 字）。</summary>
    public int 面启用基址 { get; set; } = 3320;

    /// <summary>点总数 = 面数 × 每面点数。</summary>
    public int 总数点位 => 面数量 * 每面点位数量;

    /// <summary>每点的寄存器数（Float32 占 2 字）。</summary>
    public int 字每点位 => 2;

    /// <summary>读一整面时单轴的连续字数。</summary>
    public int 面块字 => 每面点位数量 * 字每点位;

    /// <summary>指定轴的坐标区基址。</summary>
    public int 轴基址(轴标识 轴号) => 轴号 switch
    {
        轴标识.X => X基址,
        轴标识.Y => Y基址,
        轴标识.Z => Z基址,
        轴标识.R => R基址,
        _ => throw new ArgumentOutOfRangeException(nameof(轴号)),
    };
}

/// <summary>
/// 轴定义。对应 <c>config/appsettings.json</c> 的 <c>Axes</c> 数组。
///
/// <para><b>2026-09-12 清理死字段</b>：本类原先还有 <c>ArrowKind</c> / <c>PositionAddress</c> /
/// <c>JogForwardTag</c> / <c>JogReverseTag</c>（以及由 ArrowKind 派生的 <c>Arrow</c> /
/// <c>IsHorizontal</c> / <c>IsVertical</c>），全项目<b>零读取</b>——现场若改了它们毫无效果。
/// 真实生效的地址来源是 <c>config/tags.json</c>（<c>AxisXPosition</c>、<c>JogXForward</c> 等），
/// 点动命令名由 <c>ManualControlService</c> 按轴名拼装。
/// 留着就是"改了不生效"的陷阱，故整体删除，地址一律以 <c>tags.json</c> 为唯一真相。</para>
/// </summary>
public sealed class 轴定义
{
    /// <summary>轴标识，取值 <c>X</c> / <c>Y</c> / <c>Z</c> / <c>R</c>。</summary>
    public string 轴 { get; set; } = "X";

    /// <summary>界面显示名，如 <c>X 轴</c>。</summary>
    public string 名称 { get; set; } = "X 轴";

    /// <summary>工程单位文本。</summary>
    public string 单元 { get; set; } = "MM";

    /// <summary>解析后的轴标识。</summary>
    public 轴标识 标识 =>
        Enum.TryParse<轴标识>(轴, ignoreCase: true, out var 已解析) ? 已解析 : 轴标识.X;
}

/// <summary>
/// 点动速度档位寄存器的轴地址。对应 <c>config/recipe-config.json</c> 的 <c>jogSpeedLevels</c>。
///
/// <para>点动时向当前轴对应的寄存器写入 <see cref="最小等级"/>–<see cref="最大等级"/>
/// （低 / 中 / 高速），由 PLC 侧据此选择点动速度。一个轴一个地址，随选中轴切换。</para>
/// </summary>
public sealed class 点动速度等级地址
{
    /// <summary>最低档位值（低速）。</summary>
    /// <summary>低速档写入值（2026-10-13 现场口径：低=0、中=1、高=2）。</summary>
    public const int 最小等级 = 0;

    /// <summary>最高档位值（高速）。</summary>
    public const int 最大等级 = 2;

    /// <summary>轴标识，取值 <c>X</c> / <c>Y</c> / <c>Z</c> / <c>R</c>。</summary>
    public string 轴 { get; set; } = string.Empty;

    /// <summary>档位寄存器地址（D 区序号，单字 Int16）。</summary>
    public int 地址 { get; set; }

    /// <summary>绑定的变量表变量名（可选）。绑定后 <see cref="地址"/> 为变量软元件地址的回填值。</summary>
    public string? 变量 { get; set; }

    /// <summary>档位值是否合法（只接受 1 / 2 / 3）。</summary>
    public static bool 是否有效等级(int 等级) => 等级 is >= 最小等级 and <= 最大等级;
}

/// <summary><c>config/recipe-config.json</c> 的整体结构。</summary>
/// <para>点胶锁付机模板新增的点表 / 标定 / 其它点 / 动作配置见
/// <c>RecipeModels.PointTables.cs</c>（同一 partial 类型，拆开是为了守住 400 行规模门禁）。</para>
public sealed partial class 配方配置
{
    /// <summary>机种总数。</summary>
    public int 配方数量 { get; set; } = 20;

    /// <summary>
    /// <b>轴参数类</b>地址允许区间（定位速度 + 点动速度档位等 D 区参数）。
    /// <para>默认 D1200 – D1799，即 PLC 地址分配表的轴 1–轴 6 参数区
    /// （每轴 100 字：轴 1 D1200–D1299 … 轴 6 D1700–D1799，2026-09-17 按分配表更新）。
    /// D1000 – D1099 是延时表专属区、D1100 – D1199 是上位机预留段，都不属于本区间。</para>
    /// </summary>
    public 地址范围 参数地址范围 { get; set; } = new() { 最小 = 1200, 最大 = 1799 };

    /// <summary>示教点表配置（历史遗留：仅用于机种 CSV 交换的点表描述，运行期不再读写 PLC）。</summary>
    public 示教配置 示教 { get; set; } = new();

    /// <summary>控制寄存器配置。</summary>
    public 配方控件配置 控件 { get; set; } = new();

    /// <summary>点动速度档位寄存器（每轴 1 个，写 1/2/3）。</summary>
    public List<点动速度等级地址> 点动速度等级 { get; set; } = new();

    /// <summary>机种参数项定义。</summary>
    public List<配方参数规格> 参数 { get; set; } = new();

    /// <summary>按参数键查找定义。</summary>
    public 配方参数规格? 查找(string 键值) =>
        参数.FirstOrDefault(点位项 => string.Equals(点位项.键, 键值, StringComparison.OrdinalIgnoreCase));

    /// <summary>按轴查找点动速度档位地址。</summary>
    public 点动速度等级地址? 查找点动速度等级(string 轴号) =>
        点动速度等级.FirstOrDefault(坐标X => string.Equals(坐标X.轴, 轴号, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 启动同步报告（原「一致性校验报告」）。
///
/// <para><b>权威方向（2026-10-12 现场口径）：</b>机种区段（D2500–D3999）以机种快照为准；
/// 区段之外的 D 区与 M 区<b>以 PLC 为准</b>——每次开启软件后从 PLC 读回来刷新即可。
/// 因此启动时是"读回覆盖"，本报告记录的是<b>被刷新了哪些项</b>，
/// 而不是"双方不一致、请人工确认"。</para>
/// </summary>
public sealed class 一致性校验报告
{
    /// <summary>本次启动是否无需刷新（PLC 值与本地缓存逐项相同）。</summary>
    public bool 是否一致 { get; init; } = true;

    /// <summary>当前机种号（校验时读到的）。</summary>
    public int 配方号 { get; init; }

    /// <summary>成功读到并在范围内的参数项数。</summary>
    public int 已勾选参数 { get; init; }

    /// <summary>比对的坐标点数（当前恒为 0：点表已归机种快照管理）。</summary>
    public int 已勾选点位 { get; init; }

    /// <summary>刷新项描述列表（旧值 → PLC 值）。</summary>
    public List<string> 差异 { get; init; } = new();

    /// <summary>刷新的项数。</summary>
    public int 差异数量 => 差异.Count;

    /// <summary>给操作员看的一句话摘要。</summary>
    public string 摘要 => 是否一致
        ? $"全局参数已与 PLC 同步（{已勾选参数} 项，无需刷新）。"
        : $"已按 PLC 刷新 {差异数量} 项全局参数（共读回 {已勾选参数} 项）。";
}
