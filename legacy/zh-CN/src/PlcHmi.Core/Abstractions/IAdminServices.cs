using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.核心.抽象;

/// <summary>
/// 手动页元件服务。
///
/// <para><b>职责：</b>加载与保存 <c>config/manual-widgets.json</c>，提供元件增删改与校验。</para>
///
/// <para><b>硬约束：</b>元件只能绑定 <b>M 区（辅助继电器）</b>地址。
/// 上位机只置位内部继电器，由 PLC 程序决定何时驱动 Y 输出——
/// 这样上位机不会绕过 PLC 的联锁逻辑直接动作执行机构。
/// 绑定 Y 点的配置会在 <see cref="校验"/> 中被拒绝。</para>
/// </summary>
public interface 手动元件服务接口
{
    /// <summary>当前元件列表（已按 Order 排序）。</summary>
    IReadOnlyList<手动元件定义> 元件集合 { get; }

    /// <summary>元件列表变化。</summary>
    event EventHandler? 元件已变更;

    /// <summary>初始化：从文件加载。文件不存在时写入一份默认配置。</summary>
    Task 异步初始化(CancellationToken 取消令牌 = default);

    /// <summary>把当前元件列表写回文件。</summary>
    Task 异步保存(CancellationToken 取消令牌 = default);

    /// <summary>校验元件定义（名称、地址类型、必填项）。</summary>
    手动元件校验 校验(手动元件定义 元件项);

    /// <summary>新增元件（<b>仅内存</b>，需再调 <see cref="异步保存"/>）。</summary>
    void 添加(手动元件定义 元件项);

    /// <summary>更新元件（按 Id 匹配，<b>仅内存</b>）。</summary>
    void 更新(手动元件定义 元件项);

    /// <summary>删除元件（<b>仅内存</b>）。</summary>
    void 移除(string 标识);

    /// <summary>生成一个新的唯一 Id。</summary>
    string 新标识(手动元件类型 type);
}

/// <summary>
/// 报警定义服务。管理 <c>config/alarms.json</c>。
///
/// <para><b>工作原理：</b>上位机周期性读取每条定义绑定的 PLC 地址，
/// 按位或整字判断是否触发，再映射成配置好的报警文本。
/// 管理员可在「报警记录」页改文本与读取地址，不用改代码。</para>
///
/// <para><b>地址约束：</b>只允许绑定 <b>M 区或 D 区</b>。
/// X / Y 点专供 IO 监控页使用，不作为报警源。</para>
/// </summary>
public interface 报警定义服务接口
{
    /// <summary>当前报警定义列表。</summary>
    IReadOnlyList<报警定义> 定义集合 { get; }

    /// <summary>定义列表变化（增删改或重新加载）。</summary>
    event EventHandler? 定义已变更;

    /// <summary>初始化：从文件加载。文件不存在时写入一份默认配置。</summary>
    Task 异步初始化(CancellationToken 取消令牌 = default);

    /// <summary>写回文件。</summary>
    Task 异步保存(CancellationToken 取消令牌 = default);

    /// <summary>校验定义（编号、文本、地址类型、位号范围）。</summary>
    /// <param name="definition">待校验的定义（编辑时传的是克隆副本）。</param>
    /// <param name="isUpdate">
    /// 是否为编辑已有条目。编辑传 <c>true</c>：编号相同的已有条目就是正在编辑的那条自身，
    /// 不再报「已被占用」——克隆副本无法用引用比较排除自身（见 AlarmDefinitionService.Validate 注释）。
    /// </param>
    报警定义校验 校验(报警定义 定义项, bool 是否更新 = false);

    /// <summary>新增（<b>仅内存</b>）。</summary>
    void 添加(报警定义 定义项);

    /// <summary>更新（按 Id 匹配，<b>仅内存</b>）。</summary>
    void 更新(报警定义 定义项);

    /// <summary>删除（<b>仅内存</b>）。</summary>
    void 移除(string 标识);

    /// <summary>生成一个新的报警编号。</summary>
    string 新标识();
}

/// <summary>
/// 参数页自定义表格服务（延时表 / 按钮表）。
///
/// <para><b>地址约束：</b>延时表绑定 <b>D 区</b>（可读可写的数值），
/// 按钮表绑定 <b>M 区</b>（内部继电器命令位，不直接控制 Y 输出）。</para>
///
/// <para>管理员可增删条目、改名称、改地址、启停；改动写回 <c>config/param-tables.json</c>。</para>
/// </summary>
public interface 参数表格服务接口
{
    /// <summary>延时表条目。</summary>
    IReadOnlyList<参数条目定义> 延迟集合 { get; }

    /// <summary>按钮表条目。</summary>
    IReadOnlyList<参数条目定义> 按钮集合 { get; }

    /// <summary>任一表格变化。</summary>
    event EventHandler? 条目已变更;

    /// <summary>初始化：从文件加载，不存在时写入默认配置。</summary>
    Task 异步初始化(CancellationToken 取消令牌 = default);

    /// <summary>写回文件。</summary>
    Task 异步保存(CancellationToken 取消令牌 = default);

    /// <summary>取某张表的条目。</summary>
    IReadOnlyList<参数条目定义> 获取(参数表格类别 类别);

    /// <summary>校验条目（名称、地址区、取值范围）。</summary>
    /// <param name="kind">延时表 / 按钮表。</param>
    /// <param name="item">待校验的条目（编辑时传的是克隆副本）。</param>
    /// <param name="isUpdate">
    /// 是否为编辑已有条目。编辑传 <c>true</c>：同 Id 的条目就是正在编辑的那条自身，
    /// 不再报「已被占用」（克隆副本无法用引用比较排除自身）。
    /// </param>
    参数条目校验 校验(参数表格类别 类别, 参数条目定义 条目, bool 是否更新 = false);

    /// <summary>新增（仅内存）。</summary>
    void 添加(参数表格类别 类别, 参数条目定义 条目);

    /// <summary>更新（按 Id 匹配，仅内存）。</summary>
    void 更新(参数表格类别 类别, 参数条目定义 条目);

    /// <summary>删除（仅内存）。</summary>
    void 移除(参数表格类别 类别, string 标识);

    /// <summary>
    /// 把按钮点位写入 PLC（<paramref name="开"/> 为 <c>true</c> 置位、<c>false</c> 复位）。
    ///
    /// <para>与气缸同样的语义：<b>一个 M 点就是一个开关</b>，置位 = 开启、复位 = 关闭，
    /// 不需要两个地址。</para>
    /// </summary>
    Task 异步设置按钮(参数条目定义 条目, bool on, CancellationToken 取消令牌 = default);

    /// <summary>
    /// 把延时条目的当前值写入 PLC（实时生效，不需要再按「下发到 PLC」）。
    ///
    /// <para>换算规则：界面与配置按秒，PLC 的 D 区存 <b>32 位整数毫秒（DINT，占 2 字）</b>（×1000）。
    /// 写完后同步保存配置文件，保证「PLC 里的值 = 配置文件里的值」。</para>
    /// </summary>
    Task 异步写入延迟(参数条目定义 条目, CancellationToken 取消令牌 = default);

    /// <summary>
    /// 回读：把延时表与按钮表在 PLC 上的<b>当前真实值</b>读回来。
    ///
    /// <para><b>2026-10-17 现场要求：</b>延时与按钮不在 <c>tags.json</c> 里（地址由管理员在界面上配），
    /// 因此不走轮询引擎；而它们的值 PLC 侧同样可能改写（切换机种、HMI 旁的操作面板、断电前状态），
    /// 界面只显示本地配置值会与实际不符。故由页面按周期触发一次回读。</para>
    ///
    /// <para>逐项容错：某一项读失败只跳过它，不影响其它项，也不抛异常
    /// （回读是"锦上添花"，不该把页面操作打断）。</para>
    /// </summary>
    Task 异步回读(CancellationToken 取消令牌 = default);

    /// <summary>回读完成后触发（参数为变化的数据项标识）。</summary>
    event EventHandler<string>? 回读已变更;

    /// <summary>某条延时的 PLC 当前值（秒）；从未回读过或读不到时为 <c>null</c>。</summary>
    double? 回读延时值(string 标识);

    /// <summary>某个按钮的 PLC 当前状态；从未回读过或读不到时为 <c>null</c>。</summary>
    bool? 回读按钮状态(string 标识);

    /// <summary>生成一个新的唯一 Id。</summary>
    string 新标识(参数表格类别 类别);
}

/// <summary>
/// 用户与权限服务。
///
/// <para><b>登录策略</b>（按需求方确认）：密码登录，<b>不超时回落</b>——
/// 适合无人看守的触摸屏，操作员不必反复登录。</para>
///
/// <para><b>存储：</b>账号存本地 <c>config/users.json</c>，只存 PBKDF2 哈希与盐，绝不存明文。</para>
/// </summary>
public interface 用户服务接口
{
    /// <summary>当前角色。未登录时为最低权限的 <see cref="用户角色.操作员"/>。</summary>
    用户角色 当前角色 { get; }

    /// <summary>当前登录名。未登录时为空。</summary>
    string 当前用户名称 { get; }

    /// <summary>当前显示名。</summary>
    string 当前显示名称 { get; }

    /// <summary>是否已登录（未登录时按操作员权限处理）。</summary>
    bool 是否已登录 { get; }

    /// <summary>当前权限集合。</summary>
    权限 当前权限 { get; }

    /// <summary>当前账号是否仍在使用初始密码。</summary>
    bool 是否使用默认密码 { get; }

    /// <summary>全部账号（不含密码哈希）。</summary>
    IReadOnlyList<用户账号> 账号集合 { get; }

    /// <summary>身份变化（登录 / 登出）。</summary>
    event EventHandler? 用户已变更;

    /// <summary>判断当前身份是否具备某项权限。</summary>
    bool 有(权限 权限项);

    /// <summary>初始化：加载账号文件，不存在时创建默认账号。</summary>
    Task 异步初始化(CancellationToken 取消令牌 = default);

    /// <summary>登录。成功返回 <c>true</c>。</summary>
    Task<bool> 异步登录(string 用户名称, string 密码, CancellationToken 取消令牌 = default);

    /// <summary>登出，回落到操作员身份。</summary>
    void 退出登录();

    /// <summary>修改密码。成功返回 <c>true</c>。</summary>
    Task<bool> 异步变更密码(
        string 用户名称, string 当前密码, string 新密码, CancellationToken 取消令牌 = default);

    /// <summary>初始密码（用于首次登录提示）。</summary>
    static string 默认密码 => "123456";
}

/// <summary>
/// 轴地址管理。集中维护四类"随轴切换"的地址：定位速度寄存器、当前位置寄存器、
/// 正反转点动线圈、点动速度档位寄存器。
///
/// <para><b>唯一真相</b>：定位速度与点动档位落在 <c>config/recipe-config.json</c>，
/// 当前位置与正反转点动落在 <c>config/tags.json</c>。</para>
///
/// <para><b>校验一律在服务层</b>（界面只负责隐藏入口，不能作为唯一防线）：</para>
/// <list type="bullet">
///   <item>轴参数类（定位速度 / 档位）必须 <b>D 区</b> 且落在轴参数区间 <b>D1200–D1799</b>
///   （PLC 地址分配表的轴 1–轴 6 参数区，每轴 100 字）；</item>
///   <item>当前位置必须 <b>D 区</b>（Float32 占 2 字）；</item>
///   <item>正反转点动必须 <b>M 区线圈</b> 或 <b>D/R 区字内位</b>（如 D1210.2，汇川位打包）——
///   上位机只置位内部继电器/命令位，不直接驱动 Y；</item>
///   <item>四类地址<b>互不重叠</b>（同字的两个位不算重叠），且<b>不得占用延时表专属区 D1000–D1099</b>。</item>
/// </list>
///
/// <para><b>保存后立即生效</b>：原子落盘 → 重载点位定义 → 重建采集计划，无需重启程序。</para>
/// </summary>
public interface 轴地址服务接口
{
    /// <summary>当前四轴地址快照。界面编辑前应先 <c>Table.Clone()</c> 取副本。</summary>
    轴地址表格 表格 { get; }

    /// <summary>地址表已被保存并生效（界面据此刷新）。</summary>
    event EventHandler? 地址已变更;

    /// <summary>校验一组待保存的地址，返回全部错误（不修改任何状态）。</summary>
    轴地址校验 校验(轴地址表格 候选);

    /// <summary>
    /// 保存地址改动：原子落盘 → 重载点位定义 → 重建采集计划。
    /// <para>校验不通过时抛 <see cref="InvalidOperationException"/>，不会写入半个状态。</para>
    /// </summary>
    Task 异步保存(轴地址表格 候选, CancellationToken 取消令牌 = default);
}

/// <summary>
/// 汇川变量表服务。管理 <c>config/variables.json</c>（由变量表 CSV 导入生成）。
///
/// <para><b>用途：</b>轴参数区 / 延时区 / 气缸区的元件配置可按变量名绑定——
/// Modbus 模式按变量表中的软元件地址映射通讯；OPC UA 模式直接以变量名作 NodeId 符号寻址。</para>
/// </summary>
public interface 变量表服务接口
{
    /// <summary>全部变量条目（按导入顺序）。</summary>
    IReadOnlyList<变量表条目> 条目集合 { get; }

    /// <summary>变量表已重新导入（NodeId 缓存等下游应刷新）。</summary>
    event EventHandler? 已重新导入;

    /// <summary>按变量名查找（大小写不敏感）。未命中返回 <c>null</c>。</summary>
    变量表条目? 查找(string? 变量名);

    /// <summary>
    /// 按变量首地址查找（不限定数量）。命中时经 <paramref name="寄存器数量值"/> 返回该变量的占用字数。
    /// </summary>
    变量表条目? 按首地址查找(Modbus区域 区域值, int 起始地址值, out int 寄存器数量值);

    /// <summary>
    /// 按软元件寻址信息反查变量：起始地址与寄存器数量都必须精确匹配（如 REAL 变量占 2 字）。
    /// 未命中返回 <c>null</c>。
    /// </summary>
    变量表条目? 按地址查找(Modbus区域 区域值, int 起始地址值, int 寄存器数量值);

    /// <summary>
    /// 从汇川变量表 CSV 导入：整表解析校验后原子落盘并替换内存表。
    /// <para>导入失败（文件不可读 / 全部行无效）时保持旧表不变并抛 <see cref="InvalidOperationException"/>。</para>
    /// </summary>
    /// <returns>导入结果（有效条数 / 跳过行说明）。</returns>
    Task<变量表导入结果> 异步导入(string CSV文件路径, CancellationToken 取消令牌 = default);
}

/// <summary>变量表导入结果。</summary>
public sealed class 变量表导入结果
{
    /// <summary>成功导入的条目数。</summary>
    public int 有效条数 { get; init; }

    /// <summary>被跳过的行数（结构体头行 / 不支持的类型 / 地址无法寻址）。</summary>
    public int 跳过行数 { get; init; }

    /// <summary>给管理员看的说明（跳过原因汇总）。</summary>
    public string 摘要 => $"已导入 {有效条数} 个变量，跳过 {跳过行数} 行。";
}

// 运行监控页元件地址（模型 / 校验结果 / 服务接口）见 IAdminServices.RunMonitor.cs。
