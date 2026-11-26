namespace 上位机.核心.模型;

/// <summary>
/// <see cref="配方配置"/> 的点胶锁付机扩展段（partial）。
///
/// <para>从 <c>RecipeModels.cs</c> 拆出：原文件逼近 400 行规模门禁；
/// 这些字段与"机种/参数"主结构关系较弱，单独一处更易审查。</para>
/// </summary>
public sealed partial class 配方配置
{
    /// <summary>多点表定义（锁附示教 / 点胶示教）。</summary>
    public 点表配置 点表 { get; set; } = new();

    /// <summary>标定示教配置。</summary>
    public 标定配置 标定 { get; set; } = new();

    /// <summary>其它点（辅助点）坐标配置。</summary>
    public 其它点配置 其它点 { get; set; } = new();

    /// <summary>点动/动作按钮（出胶阀、电机复位…），按 <see cref="动作按钮项.分组"/> 分页展示。</summary>
    public List<动作按钮项> 动作按钮 { get; set; } = new();

    /// <summary>辅助地址（拍照点、上料点、锁付批号、浮高开关…），按 <see cref="辅助地址项.分组"/> 分页展示。</summary>
    public List<辅助地址项> 辅助地址 { get; set; } = new();

    /// <summary>回零按钮（按轴）。</summary>
    public List<回零按钮项> 回零 { get; set; } = new();

    /// <summary>点动轴配置（按轴：正/反转 M 区线圈 + 位置标签）。</summary>
    public List<点动按钮项> 点动 { get; set; } = new();

    /// <summary>
    /// 示教页顶部固定状态灯（读 M 区：0=各轴未回原，1=各轴回原完成）。
    /// <para>按 <see cref="状态灯项.分组"/> 分页；列表为空时服务层按
    /// <see cref="状态灯项.缺省地址"/> 合成一条，保证老配置升级后灯也在（见 页面地址服务.状态灯）。</para>
    /// </summary>
    public List<状态灯项> 状态灯 { get; set; } = new();
}
