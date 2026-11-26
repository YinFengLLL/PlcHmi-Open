namespace PlcHmi.Core.Models;

/// <summary>
/// 一条可在界面上编辑的 PLC 元件地址。
///
/// <para><b>为什么要统一成一种形状</b>：页面上与 PLC 交互的元件有五类来源
/// （点表列基址、辅助地址、动作按钮、回零线圈、点动线圈，标定页另有中心点/偏移/启动），
/// 但管理员的诉求只有一个——"都能改地址"。收口成同一形状后，
/// 界面一套对话框、服务一次落盘即可覆盖全部元件。</para>
/// </summary>
public sealed class AddressEditItem
{
    /// <summary>类别：<c>点表列</c> / <c>辅助地址</c> / <c>动作按钮</c> / <c>回零</c> / <c>点动</c> / <c>标定字段</c> / <c>标定按钮</c>。</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>键（类别内唯一，用于回写定位）。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>界面显示名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>地址文本，如 <c>D2500</c> / <c>M440</c>。</summary>
    public string AddressText { get; set; } = string.Empty;

    /// <summary>数据类型文本（<c>Float32</c> / <c>UInt16</c> / <c>布尔</c>…），仅辅助地址可改。</summary>
    public string DataTypeText { get; set; } = "Float32";

    /// <summary>所属页面分组（仅辅助地址用）。</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>是否允许删除（辅助地址为管理员自定义条目，允许删除；点表列等固定元件不允许）。</summary>
    public bool Deletable { get; set; }

    /// <summary>是否新增加的条目（保存时追加，而不是就地更新）。</summary>
    public bool Insert { get; set; }

    // ── 点胶单点工艺参数结构体（2026-10-21）────────────────────────────

    /// <summary>
    /// 该项是否为"结构体成员"（仅点胶页辅助地址有意义）。
    /// <para>勾选后地址由「基址 + 点序号 × 长度 + 偏移」实时算出，对话框里的地址栏只作显示；
    /// 保存时服务层按成员顺序与数据类型重排偏移与长度（见 <see cref="AuxAddressStructureLayout"/>）。</para>
    /// </summary>
    public bool StructMember { get; set; }

    /// <summary>编辑器的这一行是否显示"结构体成员"勾选框（仅点胶页的辅助地址为 true）。</summary>
    public bool SettableStructMember { get; set; }
}

/// <summary>一个页面分组的地址编辑请求。</summary>
public sealed class AddressEditRequest
{
    /// <summary>对话框标题（如「锁附示教」）。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>可编辑项集合（就地编辑）。</summary>
    public List<AddressEditItem> ItemCollection { get; set; } = new();

    /// <summary>被删除的条目（对话框里点了「删除」的行）。</summary>
    public List<AddressEditItem> DeletedItem { get; set; } = new();

    /// <summary>新建条目的默认分组（"＋ 新增"按钮用）。</summary>
    public string DefaultGroup { get; set; } = string.Empty;
}
