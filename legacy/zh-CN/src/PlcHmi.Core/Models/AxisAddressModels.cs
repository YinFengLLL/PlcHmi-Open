namespace 上位机.核心.模型;

/// <summary>
/// 轴相关点位名的拼装约定。
///
/// <para><b>为什么要有这个类</b>：轴位置与点动的点位名原先散落在多处硬拼
/// （<c>ManualControlService</c> 拼 <c>$"Jog{轴}Forward"</c>、各 ViewModel 写死
/// <c>"AxisXPosition"</c>）。一旦要改名或加轴，就得靠全局搜索，极易漏掉一处导致
/// "某根轴不工作"。集中到这里后，改命名只动一个地方。</para>
///
/// <para>点位本身（地址、单位、描述）仍然全部定义在 <c>config/tags.json</c> 里。</para>
/// </summary>
public static class 轴标签名
{
    /// <summary>当前位置点位名，如 <c>AxisXPosition</c>。</summary>
    public static string 位置(string 轴号) => $"Axis{轴号}Position";

    /// <summary>正向点动线圈点名，如 <c>JogXForward</c>。</summary>
    public static string 点动正转(string 轴号) => $"Jog{轴号}Forward";

    /// <summary>反向点动线圈点名，如 <c>JogXReverse</c>。</summary>
    public static string 点动反转(string 轴号) => $"Jog{轴号}Reverse";
}

/// <summary>
/// 一个轴的四类可编辑地址。
///
/// <para><b>为什么这四类要放在同一张表里</b>：它们都是"随选中轴切换"的地址，
/// 改一处就可能同时影响点动、位置显示与参数下发。集中在一张表里由管理员统一维护，
/// 比散落在三个配置文件里分别找要安全得多；也便于做<b>互不重叠</b>校验。</para>
/// </summary>
public sealed class 轴地址条目
{
    /// <summary>轴标识，取值 <c>X</c> / <c>Y</c> / <c>Z</c> / <c>R</c>。</summary>
    public string 轴 { get; set; } = string.Empty;

    /// <summary>界面显示名，如 <c>X 轴</c>。</summary>
    public string 名称 { get; set; } = string.Empty;

    /// <summary>
    /// 定位速度寄存器（<b>D 区，Float32 占 2 字</b>）。
    /// <para>落在 <c>recipe-config.json</c> 的 <c>parameters</c> 里，键为 <c>{轴}.LocateSpeed</c>。</para>
    /// </summary>
    public string 定位速度地址 { get; set; } = string.Empty;

    /// <summary>定位速度绑定的变量表变量名（可选，OPC UA 模式符号寻址用）。</summary>
    public string 定位速度变量 { get; set; } = string.Empty;

    /// <summary>
    /// 当前位置寄存器（<b>D 区，Float32 占 2 字</b>）。
    /// <para>落在 <c>tags.json</c>，点位名 <c>Axis{轴}Position</c>。</para>
    /// </summary>
    public string 位置地址 { get; set; } = string.Empty;

    /// <summary>
    /// 正向点动线圈（<b>M 区</b>）。
    /// <para>落在 <c>tags.json</c>，点位名 <c>Jog{轴}Forward</c>；只置位内部继电器，不直接驱动 Y。</para>
    /// </summary>
    public string 点动正转地址 { get; set; } = string.Empty;

    /// <summary>反向点动线圈（<b>M 区</b>），点位名 <c>Jog{轴}Reverse</c>。</summary>
    public string 点动反转地址 { get; set; } = string.Empty;

    /// <summary>
    /// 点动速度档位寄存器（<b>D 区，Int16 单字</b>，写 1 低 / 2 中 / 3 高）。
    /// <para>落在 <c>recipe-config.json</c> 的 <c>jogSpeedLevels</c> 里。</para>
    /// </summary>
    public string 点动速度等级地址 { get; set; } = string.Empty;

    /// <summary>
    /// 轴回零命令线圈（<b>M 区，或 D/R 字内位</b>）。
    /// <para>落在 <c>recipe-config.json</c> 的 <c>homeButtons</c> 里（按轴一条）。
    /// 留空表示该轴不提供回零按钮。</para>
    /// </summary>
    public string 回零地址 { get; set; } = string.Empty;

    /// <summary>浅拷贝（用于界面编辑副本）。</summary>
    public 轴地址条目 克隆() => (轴地址条目)MemberwiseClone();
}

/// <summary>四轴地址表快照。</summary>
public sealed class 轴地址表格
{
    /// <summary>四轴地址条目。</summary>
    public List<轴地址条目> 轴列表 { get; set; } = new();

    /// <summary>按轴标识查找（大小写不敏感）。</summary>
    public 轴地址条目? 查找(string 轴号) =>
        轴列表.FirstOrDefault(甲 => string.Equals(甲.轴, 轴号, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 深拷贝。
    /// <para>界面必须拿副本去编辑：管理员改到一半点取消时，不能把原表污染成半成品。</para>
    /// </summary>
    public 轴地址表格 克隆() => new()
    {
        轴列表 = 轴列表.Select(甲 => 甲.克隆()).ToList(),
    };
}

/// <summary>轴地址校验结果（与其它管理服务保持同一形状）。</summary>
public sealed class 轴地址校验
{
    /// <summary>是否通过。</summary>
    public bool 是否有效 { get; init; } = true;

    /// <summary>错误列表（为空表示通过）。</summary>
    public List<string> 错误集合 { get; init; } = new();

    /// <summary>给操作员看的一句话。</summary>
    public string 摘要 => 是否有效 ? "校验通过" : string.Join("；", 错误集合);
}
