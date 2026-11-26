using 上位机.核心.模型;

namespace 上位机.核心.抽象;

/// <summary>
/// 页面地址服务：统一呈现与保存"页面上所有与 PLC 交互的元件地址"。
///
/// <para><b>覆盖范围</b>：示教页（点表列基址 / 辅助地址 / 动作按钮 / 回零 / 点动 / 状态灯）、
/// 标定页（中心点 / 偏移量 / 标定启动 / 点动 / 回零 / 动作按钮 / 辅助地址）。
/// 管理员通过一个对话框即可改全部，服务层做权限校验与原子落盘。</para>
///
/// <para><b>已取消地址范围限制</b>（2026-10-12）：不再校验轴参数区、PLC 保留区与区间归属，
/// 只保证地址可解析、元素类型正确、本次提交内不重叠。</para>
/// </summary>
public interface 页面地址服务接口
{
    /// <summary>收集某张点表页（锁附 / 点胶）的全部可编辑地址项。</summary>
    IReadOnlyList<地址编辑项> 收集点表页(string 表键);

    /// <summary>收集标定 / 其它点页的全部可编辑地址项。</summary>
    IReadOnlyList<地址编辑项> 收集标定页();

    /// <summary>按分组取辅助地址定义（界面表格用）。</summary>
    IReadOnlyList<辅助地址项> 辅助地址(string 分组);

    /// <summary>按分组取动作按钮定义（界面按钮区用）。</summary>
    IReadOnlyList<动作按钮项> 动作按钮(string 分组);

    /// <summary>
    /// 按分组取示教页顶部固定状态灯定义（0=各轴未回原，1=各轴回原完成）。
    /// <para>配置里没有该分组的条目时返回一条按缺省地址合成的条目——老配置升级后
    /// 灯照样出现、管理员照样能改地址，不需要手工编辑 JSON。</para>
    /// </summary>
    IReadOnlyList<状态灯项> 状态灯(string 分组);

    /// <summary>保存点表页的地址编辑结果（含新增 / 删除）。要求管理员权限（编辑绑定地址）。</summary>
    Task 异步保存点表页(string 表键, 地址编辑请求 请求, CancellationToken 取消令牌 = default);

    /// <summary>保存标定页的地址编辑结果。要求管理员权限。</summary>
    Task 异步保存标定页(地址编辑请求 请求, CancellationToken 取消令牌 = default);

    /// <summary>读取一个辅助地址的当前值（D 区数值）。</summary>
    Task<double> 异步读取辅助地址(辅助地址项 项, CancellationToken 取消令牌 = default);

    /// <summary>
    /// 按输入顺序批量读取辅助地址的 D 区数值（2026-11-19 性能改造）。
    /// <para>把连续/邻近的 D 区地址合并成一次保持寄存器读请求，避免"一行一个请求"的 N+1：
    /// 辅助地址上限提到 30 行后，逐行读会在 3s 回读节拍里连发 30 次，独占串口闸锁、
    /// 把快组 100ms 采集周期挤出去。</para>
    /// <para>开关类（M 区布尔）与地址无效/单项失败的项返回 <c>null</c>，调用方应跳过该行，
    /// 与逐行读取时"单项容错、读不到就跳过"的语义一致。</para>
    /// </summary>
    /// <param name="项列表">按界面行顺序传入的定义（结构体数组项的 <c>地址</c> 已按当前点序号换算好）。</param>
    /// <param name="取消令牌">取消令牌。</param>
    /// <returns>与 <paramref name="项列表"/> 等长、按下标对齐的结果；<c>null</c> = 该项未读到。</returns>
    Task<IReadOnlyList<double?>> 异步批量读取辅助地址(
        IReadOnlyList<辅助地址项> 项列表, CancellationToken 取消令牌 = default);

    /// <summary>
    /// 读取一个辅助开关的当前状态（M 区线圈）。
    /// <para>2026-11-12：辅助地址区改为"实时显示 PLC 值"，开关也必须能读回来——
    /// 只写不读时，PLC 程序把线圈复位了界面还显示勾选着。</para>
    /// </summary>
    Task<bool> 异步读取辅助开关(辅助地址项 项, CancellationToken 取消令牌 = default);

    /// <summary>写入一个辅助地址（D 区数值）。要求工程师权限（编辑示教）。</summary>
    Task 异步写入辅助地址(辅助地址项 项, double 值, CancellationToken 取消令牌 = default);

    /// <summary>写入一个辅助开关（M 区线圈）。要求工程师权限（编辑示教）。</summary>
    Task 异步写入辅助开关(辅助地址项 项, bool 值, CancellationToken 取消令牌 = default);
}
