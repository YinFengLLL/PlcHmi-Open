using PlcHmi.Core.Enums;

namespace PlcHmi.Core.Models;

/// <summary>一个辅助点位（非锁附/点胶类的点位，如滑螺丝点、相机中心）。</summary>
public sealed class AuxPointDefinition
{
    /// <summary>
    /// 点位名称（界面上可改，改名后不允许与其它点重名）。
    /// <para>服务层按名称定位（编辑 / 删除 / CSV 交换），因此它在任一时刻都是唯一标识，
    /// 但<b>不是不可变的键</b>——改名按"原名定位 + 整体替换"处理。</para>
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>X 坐标基址（D 区）。</summary>
    public int XBaseAddress { get; set; }

    /// <summary>Y 坐标基址（D 区）。</summary>
    public int YBaseAddress { get; set; }

    /// <summary>Z 坐标基址（D 区）。</summary>
    public int ZBaseAddress { get; set; }

    /// <summary>浅拷贝。</summary>
    public AuxPointDefinition Clone() => (AuxPointDefinition)MemberwiseClone();
}

/// <summary>其它点坐标配置。对应 <c>recipe-config.json</c> 的 <c>auxPoints</c> 节。</summary>
public sealed class OtherPointConfig
{
    /// <summary>坐标下限。</summary>
    public double MinValue { get; set; } = -999.99;

    /// <summary>坐标上限。</summary>
    public double MaxValue { get; set; } = 9999.99;

    /// <summary>
    /// 允许的地址区间。
    /// <para><b>2026-10-21：</b>下界由 3500 收到 <b>3760</b>——D3500–D3739 现在是点胶辅助地址的
    /// 结构体数组（30 点 × 8 字），D3740–D3753 是标定，两者都不能再被其它点占用。</para>
    /// </summary>
    public PointTableAddressRange AllowRange { get; set; } = new() { Min = 3760, Max = 3999 };

    /// <summary>点位定义列表。</summary>
    public List<AuxPointDefinition> Point { get; set; } = new();

    /// <summary>按名称查找。</summary>
    public AuxPointDefinition? Find(string nameValue) =>
        Point.FirstOrDefault(PointEntry => string.Equals(PointEntry.Name, nameValue, StringComparison.OrdinalIgnoreCase));
}

/// <summary><c>config/aux-points.json</c> 的顶层结构（辅助点定义主存）。</summary>
public sealed class AuxPointFile
{
    /// <summary>点位定义列表。</summary>
    public List<AuxPointDefinition> Point { get; set; } = new();
}

/// <summary>点胶示教的「动作指令」参数项（提前关胶、运动速度延时…）。</summary>
public sealed class ActionCommandItem
{
    /// <summary>键。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>界面显示名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>D 区地址（序号）。</summary>
    public int BaseAddress { get; set; }

    /// <summary>数据类型（默认 Float32）。</summary>
    public TagDataType DataType { get; set; } = TagDataType.Float32;

    /// <summary>工程单位（可空）。</summary>
    public string? Unit { get; set; }

    /// <summary>允许下限。</summary>
    public double Min { get; set; }

    /// <summary>允许上限。</summary>
    public double Max { get; set; } = 9999.99;

    /// <summary>出厂默认值。</summary>
    public double Default { get; set; }

    /// <summary>本项占用的寄存器数量。</summary>
    public int RegisterCount => DataType switch
    {
        TagDataType.Float32 or TagDataType.Int32 or TagDataType.UInt32 => 2,
        TagDataType.Float64 => 4,
        _ => 1,
    };

    /// <summary>显示名称（为空时回退到键）。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Key : Name;

    /// <summary>值是否在允许范围内。</summary>
    public bool IsInRange(double Value) => Value >= Min && Value <= Max;

    /// <summary>范围提示文本。</summary>
    public string RangeText => $"{Min:0.###} ~ {Max:0.###}";
}

/// <summary>
/// 示教页顶部固定状态灯的地址定义（2026-10-16 现场要求）。
///
/// <para><b>语义：</b>读一个 M 区位——<c>0</c> = 各轴未回原（灯闪烁 + 提示"各轴未回原"），
/// <c>1</c> = 各轴回原完成（灯常亮 + 提示"各轴回原完成"）。</para>
///
/// <para><b>为什么不是一个写元件：</b>这是 PLC 给上位机的状态反馈，页面只读不写；
/// 管理员可在「编辑地址」里改地址（按分组各页一份），改完即时生效。</para>
/// </summary>
public sealed class StatusLampItem
{
    /// <summary>键。</summary>
    public string Key { get; set; } = "回原状态灯";

    /// <summary>界面显示名称。</summary>
    public string Name { get; set; } = "各轴回原状态";

    /// <summary>所属页面分组（<c>锁附</c> / <c>点胶</c>），空表示所有示教页共用。</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>M 区线圈地址（如 <c>M470</c>）。</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// 未配置地址时的缺省值（现场按 PLC 地址分配表在「编辑地址」里改）。
    /// <para>取 M465：M440–M445 是回零线圈、M450/M460/M461 是动作按钮、M470 是点动正转，
    /// 465 落在示教/手动段里且当前未被占用（见 config/recipe-config.json）。</para>
    /// </summary>
    public const string defaultAddress = "M465";

    /// <summary>显示名称（为空时回退到键）。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Key : Name;
}

/// <summary>一个回零按钮（把某轴驱动回原点）。地址为 M 区线圈。</summary>
public sealed class HomeButtonItem
{
    /// <summary>轴标识，如 <c>X</c> / <c>Y</c> / <c>Z</c> / <c>ZZ</c> / <c>GlueX</c> / <c>GlueZ</c>。</summary>
    public string Axis { get; set; } = string.Empty;

    /// <summary>界面显示名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>M 区线圈地址。</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>显示名称（为空时回退到「{轴} 轴回零」）。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"{Axis} 轴回零" : Name;
}

/// <summary>一个点动轴配置（正/反转 M 区线圈 + 位置标签）。</summary>
public sealed class JogButtonItem
{
    /// <summary>轴标识，如 <c>X</c> / <c>ZZ</c> / <c>GlueX</c>。</summary>
    public string Axis { get; set; } = string.Empty;

    /// <summary>界面显示名称，如「点胶 X 轴」。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 所属页面分组（<c>锁附</c> / <c>点胶</c> / <c>标定</c>），空表示所有页面都显示。
    /// <para>同一根轴在不同页面可以有不同显示名（如 Y 轴在点胶页叫「点胶 Y 轴」），
    /// 由此字段区分——点胶 Y 与锁附 Y 是共轴结构，地址相同、名称不同。</para>
    /// </summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>正转 M 区线圈地址。</summary>
    public string ForwardAddress { get; set; } = string.Empty;

    /// <summary>反转 M 区线圈地址。</summary>
    public string ReverseAddress { get; set; } = string.Empty;

    /// <summary>当前位置标签名（可空：为空时界面不显示实时位置）。</summary>
    public string? PositionTag { get; set; }

    /// <summary>显示名称（为空时回退到轴标识）。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Axis : Name;
}

/// <summary>
/// 一个「辅助地址」：页面上与 PLC 交互的具名元件（拍照点坐标、上料点、锁付批号、浮高开关…）。
///
/// <para><b>为什么单列一类</b>：这些元件既不是点表列、也不是动作按钮，但同样要能
/// 由管理员改地址。统一成"具名地址 + 数据类型"后，界面一套表格就能全部呈现与维护，
/// 新增元件只需加配置、不改代码。</para>
/// </summary>
public sealed class AuxAddressItem
{
    /// <summary>
    /// 单个页面分组允许的最大辅助地址数（2026-11-19 现场：锁附示教页「辅助地址」区上限由 10 提到 30）。
    /// <para>键契约是「同一分组内唯一」，因此上限按分组统计；
    /// 编辑器新增前拦截、服务层落盘前再兜底校验一次。</para>
    /// </summary>
    public const int perGroupUpperLimit = 30;

    /// <summary>键（同一分组内唯一）。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>界面显示名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>所属页面分组（<c>锁附</c> / <c>点胶</c> / <c>标定</c>），空表示所有页面都显示。</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>软元件地址文本，如 <c>D2660</c> / <c>M2660</c>（D 区为数值，M 区为开关）。</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>数据类型（D 区数值用 Float32 / Int16…；M 区开关用 布尔）。</summary>
    public TagDataType DataType { get; set; } = TagDataType.Float32;

    /// <summary>工程单位（可空）。</summary>
    public string? Unit { get; set; }

    // ── 结构体数组模式（2026-10-21 现场要求：点胶单点工艺参数）─────────
    //
    // 现场口径：点胶每个点都有一份"单点工艺参数"（提前关胶 / 移动前出胶延时 /
    // 关胶后延时 / 抬针高度 / 点胶速度），在 PLC 里是一个**结构体数组**：
    //   第 n 点（0 基）的该字段地址 = 结构体基址 + n × 结构体长度 + 结构体内偏移
    // 例如基址 3500、长度 8、提前关胶偏移 0 → 第 1 点 D3500、第 2 点 D3508。
    //
    // 2026-10-21 二次口径：成员身份改为「编辑地址」里可勾选（结构体成员），
    // 成员的偏移与结构体长度在保存时按<b>成员顺序 + 数据类型</b>统一重排，
    // 见 <see cref="AuxAddressStructureLayout"/>——手工改一个成员的类型（如抬针高度 REAL→INT），
    // 后面成员的地址必须跟着前移，否则上位机显示的地址与 PLC 实际占用对不上。

    /// <summary>
    /// 是否为"点胶单点工艺参数结构体"的成员。
    /// <para>勾选后在页面上按"基址 + 点序号 × 长度 + 偏移"寻址；不勾选则用 <see cref="Address"/> 固定地址。</para>
    /// </summary>
    public bool StructMember { get; set; }

    /// <summary>结构体数组的基址（D 区序号，如 3500）；0 = 不用数组模式。</summary>
    public int StructBaseAddress { get; set; }

    /// <summary>本字段在结构体内的字偏移（如提前关胶 0、抬针高度 4）。<b>保存时自动重排</b>。</summary>
    public int StructInnerOffset { get; set; }

    /// <summary>结构体长度（每点占用的字数，如 8）。0 = 不用数组模式。<b>保存时自动重排</b>。</summary>
    public int StructLength { get; set; }

    /// <summary>是否跟随点序号寻址（结构体数组模式）。</summary>
    public bool IsStructArray => StructMember && StructBaseAddress > 0 && StructLength > 0;

    /// <summary>
    /// 按点序号（<b>0 基</b>）算出本字段的 D 区地址文本。
    /// <para>非数组模式直接返回配置的固定 <see cref="Address"/>；
    /// 数组模式下 <see cref="Address"/> 不参与寻址（只作为备注/编辑参考）。</para>
    /// </summary>
    public string ByPointAddress(int PointSequence)
    {
        if (!IsStructArray)
        {
            return Address;
        }

        var Sequence = Math.Max(0, PointSequence);
        return $"D{StructBaseAddress + Sequence * StructLength + StructInnerOffset}";
    }

    /// <summary>按点序号算出本字段的 D 区序号（非数组模式返回地址解析结果，解析不出为 0）。</summary>
    public int ByPointIndex(int PointSequence)
    {
        if (IsStructArray)
        {
            return StructBaseAddress + Math.Max(0, PointSequence) * StructLength + StructInnerOffset;
        }

        var Text = Address.Trim();
        return int.TryParse(Text.TrimStart('D', 'd'), out var NumericValue) ? NumericValue : 0;
    }

    /// <summary>本项占用的寄存器数量（布尔按 1 位/1 字处理）。</summary>
    public int RegisterCount => DataType switch
    {
        TagDataType.Float32 or TagDataType.Int32 or TagDataType.UInt32 => 2,
        TagDataType.Float64 => 4,
        _ => 1,
    };

    /// <summary>是否为开关类（M 区布尔）。</summary>
    public bool IsSwitch => DataType == TagDataType.Boolean;

    /// <summary>显示名称（为空时回退到键）。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Key : Name;

    /// <summary>浅拷贝（供界面编辑副本）。</summary>
    public AuxAddressItem Clone() => (AuxAddressItem)MemberwiseClone();
}
