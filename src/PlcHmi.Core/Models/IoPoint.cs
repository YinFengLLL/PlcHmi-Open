namespace 上位机.核心.模型;

/// <summary>
/// IO 点位定义。对应 <c>config/io-points.json</c> 中的一条记录。
/// <para>这是本模板的核心设计：<b>程序不需要知道 "X0 是什么"，含义由工程师在界面上填写</b>，
/// 因此换设备时只需改配置、无需改代码。</para>
/// </summary>
public sealed class IO点位
{
    /// <summary>点位编号，如 <c>"X0"</c>、<c>"Y10"</c>。<b>X / Y 使用八进制编号。</b></summary>
    public string 代码 { get; set; } = string.Empty;

    /// <summary>具体含义，如 <c>"上料到位检测"</c>。可在 IO 页面直接编辑。</summary>
    public string 含义 { get; set; } = string.Empty;

    /// <summary>是否启用。未启用时不参与轮询，界面显示为灰色占位。</summary>
    public bool 启用 { get; set; } = true;

    /// <summary>是否反相显示（常闭信号）。</summary>
    public bool 取反 { get; set; }

    /// <summary>附加备注。</summary>
    public string? 注释 { get; set; }
}

/// <summary>IO 点位表元信息。</summary>
public sealed class IO点位表格元数据
{
    /// <summary>机型描述，如 <c>"EASY52X"</c>。</summary>
    public string PLC模型 { get; set; } = "EASY52X";

    /// <summary>X 区 Modbus 起始地址（小型 PLC 为 63488）。</summary>
    public int X基址地址 { get; set; } = 63488;

    /// <summary>Y 区 Modbus 起始地址（小型 PLC 为 64512）。</summary>
    public int Y基址地址 { get; set; } = 64512;

    /// <summary>X / Y 编号进制：<c>"octal"</c> 或 <c>"decimal"</c>。</summary>
    public string 编号基址 { get; set; } = "octal";

    /// <summary>
    /// X 区位区覆盖：<c>"线圈"</c>（FC01，缺省同解析器）或 <c>"离散输入"</c>（FC02）。
    /// <para>部分机型（如汇川 H5U）的物理输入在离散输入区，而线圈区同地址返回无关数据——
    /// 自动探测区分不了"位区失效"与"无关数据"，此时在此显式指定即可以纠偏，无需改代码。</para>
    /// </summary>
    public string? X区域 { get; set; }

    /// <summary>Y 区位区覆盖：<c>"线圈"</c>（FC01，缺省）或 <c>"离散输入"</c>（FC02）。</summary>
    public string? Y区域 { get; set; }

    /// <summary>X 区每组点数（按八进制 8 点一组排布）。</summary>
    public int 分组大小 { get; set; } = 8;

    /// <summary>X 点总数。</summary>
    public int 输入数量 { get; set; }

    /// <summary>Y 点总数。</summary>
    public int 输出数量 { get; set; }
}

/// <summary>IO 点位表。</summary>
public sealed class IO点位表格
{
    public IO点位表格元数据 元 { get; set; } = new();

    /// <summary>X 输入点集合。</summary>
    public List<IO点位> 输入 { get; set; } = new();

    /// <summary>Y 输出点集合。</summary>
    public List<IO点位> 输出 { get; set; } = new();
}

/// <summary>IO 点的实时状态。</summary>
/// <param name="Code">点位编号。</param>
/// <param name="IsOn">逻辑状态（已应用反相）。</param>
/// <param name="Timestamp">采集时间。</param>
public readonly record struct IO点位状态(string 代码, bool 是否开启, DateTime 时间戳);
