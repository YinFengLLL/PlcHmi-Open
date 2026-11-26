namespace PlcHmi.Core.Models;

/// <summary>
/// <see cref="RecipeConfig"/> 的点胶锁付机扩展段（partial）。
///
/// <para>从 <c>RecipeModels.cs</c> 拆出：原文件逼近 400 行规模门禁；
/// 这些字段与"机种/参数"主结构关系较弱，单独一处更易审查。</para>
/// </summary>
public sealed partial class RecipeConfig
{
    /// <summary>多点表定义（锁附示教 / 点胶示教）。</summary>
    public PointTableConfig PointTable { get; set; } = new();

    /// <summary>标定示教配置。</summary>
    public CalibrationConfig Calibration { get; set; } = new();

    /// <summary>其它点（辅助点）坐标配置。</summary>
    public OtherPointConfig OtherPoint { get; set; } = new();

    /// <summary>点动/动作按钮（出胶阀、电机复位…），按 <see cref="ActionButtonItem.Group"/> 分页展示。</summary>
    public List<ActionButtonItem> ActionButton { get; set; } = new();

    /// <summary>辅助地址（拍照点、上料点、锁付批号、浮高开关…），按 <see cref="AuxAddressItem.Group"/> 分页展示。</summary>
    public List<AuxAddressItem> AuxAddress { get; set; } = new();

    /// <summary>回零按钮（按轴）。</summary>
    public List<HomeButtonItem> Home { get; set; } = new();

    /// <summary>点动轴配置（按轴：正/反转 M 区线圈 + 位置标签）。</summary>
    public List<JogButtonItem> Jog { get; set; } = new();

    /// <summary>
    /// 示教页顶部固定状态灯（读 M 区：0=各轴未回原，1=各轴回原完成）。
    /// <para>按 <see cref="StatusLampItem.Group"/> 分页；列表为空时服务层按
    /// <see cref="StatusLampItem.defaultAddress"/> 合成一条，保证老配置升级后灯也在（见 页面地址服务.状态灯）。</para>
    /// </summary>
    public List<StatusLampItem> StatusLamp { get; set; } = new();
}
