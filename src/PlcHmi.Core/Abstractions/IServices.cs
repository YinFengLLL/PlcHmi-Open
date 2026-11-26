using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.核心.抽象;

/// <summary>点位定义仓库。负责从 <c>config/tags.json</c> 加载业务点位。</summary>
public interface 标签仓储接口
{
    /// <summary>全部点位定义。</summary>
    IReadOnlyList<标签定义> 全部 { get; }

    /// <summary>按名称查找点位。</summary>
    标签定义? 查找(string 名称值);

    /// <summary>按轮询分组筛选点位。</summary>
    IReadOnlyList<标签定义> 获取分组(标签分组 group);

    /// <summary>重新加载配置。</summary>
    Task 异步重载(CancellationToken 取消令牌 = default);

    /// <summary>
    /// 修改指定点位的地址（<b>仅内存</b>）。
    /// <para>找不到该点位时返回 <c>false</c>，由调用方决定是否视为错误。</para>
    /// </summary>
    bool 尝试更新地址(string 名称值, string 地址值);

    /// <summary>
    /// 新增一个点位定义（<b>仅内存</b>）；同名点位已存在时返回 <c>false</c>。
    ///
    /// <para><b>为什么必须有：</b>覆盖升级时现场 <c>tags.json</c> 属于现场文件、不会被覆盖，
    /// 新版自带的新点位（如 2026-10-16 的工位状态三灯与已锁附颗数）在现场表里是缺的。
    /// 缺了就"能显示但不能配地址"——管理员在「编辑地址」里改完保存会直接失败，
    /// 只能手工编辑 JSON。运行时按出厂定义补建，配合随后的整表保存即可落盘。</para>
    /// </summary>
    bool 尝试新增(标签定义 定义项);

    /// <summary>
    /// 把当前点位表原子写回 <c>config/tags.json</c>。
    /// <para>改完地址<b>必须</b>接着调 <c>IPollingEngine.RebuildPlansAsync</c>，
    /// 否则新地址不参与采集——读取计划是启动时编译好的。</para>
    /// </summary>
    Task 异步保存(CancellationToken 取消令牌 = default);
}

/// <summary>点位实时值服务。值变化时推送事件。</summary>
public interface 标签值服务接口
{
    /// <summary>某个点位的值发生变化（已做变化检测，值不变不推送）。</summary>
    event EventHandler<标签新值>? 新值已变更;

    /// <summary>获取点位当前值。</summary>
    标签新值 获取新值(string 名称值);

    /// <summary>尝试以 double 取得点位值。</summary>
    bool 尝试获取双精度(string 名称值, out double 值);

    /// <summary>尝试以 bool 取得点位值。</summary>
    bool 尝试获取布尔(string 名称值, out bool 值);
}

/// <summary>IO 点位服务。管理 X / Y 点位表及其实时状态。</summary>
public interface IO点位服务接口
{
    /// <summary>IO 点位表。</summary>
    IO点位表格 表格 { get; }

    /// <summary>IO 点状态变化事件（仅变化时推送）。</summary>
    event EventHandler<IO点位状态>? 状态已变更;

    /// <summary>获取某个 IO 点的当前状态。</summary>
    IO点位状态 获取状态(string 代码值);

    /// <summary>
    /// 由采集层更新某个 IO 点的原始状态。
    ///
    /// <para>内部会应用该点的 <c>inverted</c>（常闭）设置，并且<b>仅在逻辑状态变化时</b>
    /// 推送 <see cref="状态值已变更"/>。</para>
    ///
    /// <para><b>为什么必须声明在接口上：</b>采集层持有的是 <see cref="IO点位服务接口"/>。
    /// 若改写成"先判断具体类型再向下转型"，一旦实现被替换（测试替身、换 PLC 品牌的适配实现），
    /// 状态更新会被<b>静默跳过</b>——不报错、不记日志，界面只是永远停在初始值，
    /// 正是本项目踩过的那类"编译通过、自检通过、就是不工作"的坑。</para>
    /// </summary>
    void 更新状态(string 代码值, bool 原始值);

    /// <summary>修改某个 IO 点的含义。</summary>
    void 更新含义(string 代码值, string 含义);

    /// <summary>把当前点位表写回 <c>io-points.json</c>。</summary>
    Task 异步保存(CancellationToken 取消令牌 = default);

    /// <summary>重新加载点位表。</summary>
    Task 异步重载(CancellationToken 取消令牌 = default);

    /// <summary>
    /// 预览某一侧下一栏的编号范围，如 <c>"X30 — X37"</c>。
    /// <para>仅供界面确认框与按钮提示使用，<b>不修改任何数据</b>。</para>
    /// </summary>
    string 预览下一栏(string 前缀);

    /// <summary>
    /// 在某一侧末尾追加一栏点位（<see cref="IO点位表格元数据.分组大小"/> 个），
    /// 编号按八进制自动递增，随即原子写回 <c>io-points.json</c>。
    ///
    /// <para><b>调用方必须接着重建采集计划</b>（<c>轮询引擎接口.异步重建方案列表</c>），
    /// 否则新点不参与轮询 —— 与改轴地址后的要求相同。</para>
    ///
    /// <para>编号越出 PLC 编址范围、或与既有点位重号时抛异常，<b>不做部分写入</b>。</para>
    /// </summary>
    Task<IReadOnlyList<IO点位>> 异步新增一栏(string 前缀, CancellationToken 取消令牌 = default);
}

/// <summary>连接管理器。负责连接生命周期、心跳保活与断线重连。</summary>
public interface 连接管理器接口
{
    /// <summary>当前连接状态。</summary>
    连接状态 状态 { get; }

    /// <summary>连接状态变化事件。</summary>
    event EventHandler<连接状态>? 状态已变更;

    /// <summary>启动连接（含自动重连循环）。</summary>
    Task 异步启动(CancellationToken 取消令牌 = default);

    /// <summary>停止连接。</summary>
    Task 异步停止(CancellationToken 取消令牌 = default);
}

/// <summary>轮询引擎。按分组周期采集点位，做变化检测后分发。</summary>
public interface 轮询引擎接口
{
    /// <summary>启动轮询。</summary>
    Task 异步启动(CancellationToken 取消令牌 = default);

    /// <summary>停止轮询。</summary>
    Task 异步停止(CancellationToken 取消令牌 = default);

    /// <summary>
    /// 重新编译读取计划并重启采集循环。
    /// <para><b>地址配置被修改后必须调用</b>：读取计划只在启动时编译一次，
    /// 不重建就会出现"地址改了却不生效，直到重启程序"这种最难查的问题。</para>
    /// </summary>
    Task 异步重建方案列表(CancellationToken 取消令牌 = default);

    /// <summary>
    /// 各分组最近一轮采集的耗时（毫秒），键为分组，供诊断使用。
    ///
    /// <para><b>为什么是"按分组"而不是一个总数：</b>快 / 慢 / 事件三组是各自独立的循环、
    /// 并发运行。若三组共用一个字段，它记录的实际是"最后完成的那一组"的耗时——
    /// 既不代表最快组也不代表最慢组。拿它当"采集耗时"看，会把排查方向带偏。</para>
    /// </summary>
    IReadOnlyDictionary<标签分组, double> 周期毫秒分组 { get; }
}

// 设备服务接口见 IDeviceServices.cs（含启停命令、节拍产量与加工状态）。

/// <summary>
/// 产量统计服务。按小时采样累计产量计数器，为运行页数据分析图表提供
/// 今日每小时 / 昨日每小时 / 近 7 天每日的产量序列。
///
/// <para><b>为什么由上位机自己统计：</b>PLC 只暴露一个累计产量寄存器，
/// 没有按小时 / 按天的历史。上位机在整点记录计数器快照，
/// 相邻两点的差值就是该小时的产量；历史落盘 <c>data/production-history.json</c>，
/// 跨重启保留近 8 天。</para>
///
/// <para><b>计数器回退（数据清除）</b>：相邻快照差值为负说明计数器被清零，
/// 该小时的产量取后一个快照值（清零后已生产的件数），绝不出负数。</para>
/// </summary>
public interface 产量统计接口
{
    /// <summary>统计快照更新（整点采样或实时值变化）。界面据此刷新图表。</summary>
    event EventHandler? 统计已变更;

    /// <summary>今日每小时产量（24 项，索引 = 小时；当前小时为实时值）。</summary>
    IReadOnlyList<double> 今日小时产量 { get; }

    /// <summary>昨日每小时产量（24 项，索引 = 小时）。</summary>
    IReadOnlyList<double> 昨日小时产量 { get; }

    /// <summary>最近 7 天每日产量（索引 0 = 6 天前 … 6 = 今天，今天含实时值）。</summary>
    IReadOnlyList<double> 周每日产量 { get; }

    /// <summary>最近 7 天的日期标签（如 "09-18 周四"），与 <see cref="周每日产量"/> 一一对应。</summary>
    IReadOnlyList<string> 周日期标签 { get; }
}

/// <summary>报警服务。</summary>
public interface 报警服务接口
{
    /// <summary>活动报警列表。</summary>
    IReadOnlyList<报警条目> 活动报警 { get; }

    /// <summary>历史报警列表。</summary>
    IReadOnlyList<报警条目> 历史 { get; }

    /// <summary>新报警产生。</summary>
    event EventHandler<报警条目>? 报警已触发;

    /// <summary>报警消除。</summary>
    event EventHandler<报警条目>? 报警已清除;

    /// <summary>
    /// 产生一条报警。
    /// <para>由报警扫描服务在检测到 PLC 报警位为 1 时调用。
    /// 相同编号已在活动中时忽略，不会重复入列。</para>
    /// </summary>
    void 触发(string 代码值, string 消息, 报警等级 等级);

    /// <summary>
    /// 消除一条报警。
    /// <para>由报警扫描服务在检测到 PLC 报警位恢复为 0 时调用。</para>
    /// </summary>
    void 清空(string 代码值);

    /// <summary>确认单条报警。</summary>
    void 确认(string 代码值);

    /// <summary>确认全部活动报警。</summary>
    void 确认全部();

    /// <summary>报警复位（向 PLC 下发复位命令）。</summary>
    Task 异步重置(CancellationToken 取消令牌 = default);

    /// <summary>清除历史报警。</summary>
    void 清空历史();
}

/// <summary>
/// 手动控制服务。手动页元件动作与四轴 JOG，含手动使能联锁。
///
/// <para><b>元件地址硬约束：只接受 M 区（辅助继电器）地址。</b>
/// 上位机只置位内部继电器，由 PLC 程序决定何时驱动 Y 输出——
/// 这样上位机不会绕过 PLC 的联锁逻辑直接动作执行机构。</para>
/// </summary>
public interface 手动控件服务接口
{
    /// <summary>手动使能是否开启。未开启时禁止一切手动动作。</summary>
    bool 是否启用 { get; }

    /// <summary>
    /// 手动使能状态发生变化。
    /// <para>手动调试页与示教页共用同一个使能开关，必须靠这个事件保持一致——
    /// 否则在 A 页开了使能、切到 B 页却显示“未使能”，操作员会以为按钮坏了。</para>
    /// </summary>
    event EventHandler<bool>? 启用已变更;

    /// <summary>设置手动使能。</summary>
    void 设置启用(bool 启用);

    /// <summary>把手动使能状态同步到 PLC。</summary>
    Task 异步同步启用(CancellationToken 取消令牌 = default);

    /// <summary>把一个 M 区线圈写到指定值。</summary>
    /// <param name="address">软元件地址，如 <c>M220</c>。必须是 M 区。</param>
    /// <param name="value">目标值。</param>
    Task 异步写入线圈(string 地址值, bool 值, CancellationToken 取消令牌 = default);

    /// <summary>把一个 M 区线圈置位一段时间后复位（按钮类元件用）。</summary>
    /// <param name="address">软元件地址，如 <c>M220</c>。必须是 M 区。</param>
    /// <param name="width">脉冲宽度，传 <see cref="TimeSpan.Zero"/> 用默认值。</param>
    Task 异步脉冲线圈(string 地址值, TimeSpan 宽度, CancellationToken 取消令牌 = default);

    /// <summary>
    /// 读一个 M 区线圈的当前值。
    /// <para>给<b>切换型按钮</b>回读真实状态用：断电重启、或在别处（PLC 程序 / 手动页）改过线圈后，
    /// 按钮不该还显示成"未激活"。</para>
    /// </summary>
    /// <param name="address">软元件地址，如 <c>M220</c>。必须是 M 区。</param>
    Task<bool> 异步读取线圈(string 地址值, CancellationToken 取消令牌 = default);

    /// <summary>
    /// 批量读取多个 M 区线圈（2026-11-19 性能改造）：把连续/邻近地址合并成一次读位请求，
    /// 返回与输入顺序对齐的结果，读不到的项返回 <c>null</c>。
    /// <para>用于动作按钮状态回读：一页十几个按钮原逐个读，连发会独占串口闸锁。</para>
    /// </summary>
    Task<IReadOnlyList<bool?>> 异步批量读取线圈(
        IReadOnlyList<string> 地址列表, CancellationToken 取消令牌 = default);

    /// <summary>
    /// 轴 JOG 点动。<paramref name="方向"/> 为 1 正向、-1 负向。
    ///
    /// <para><b>方向命令不携带速度</b>：具体速度由该轴的点动速度档位寄存器决定
    /// （见 <see cref="异步设置点动速度等级"/>，写 1/2/3），这里只置位方向命令。
    /// 把"选速度"与"执行动作"分开，就不会出现两处速度配置打架。</para>
    /// </summary>
    Task 异步点动(string 轴号, int 方向, CancellationToken 取消令牌 = default);

    /// <summary>
    /// 停止 JOG（松开按钮）。
    /// <para><b>必须无条件生效</b>：不检查手动使能——若使能意外断开就拒绝停止，轴会一直动下去。</para>
    /// </summary>
    Task 异步停止点动(string 轴号, CancellationToken 取消令牌 = default);

    /// <summary>
    /// 写入点动速度档位到该轴对应的档位寄存器：1 低速 / 2 中速 / 3 高速。
    ///
    /// <para><b>不要求手动使能</b>：这是"参数设定"而不是动作机构，与参数页的延时/按钮表一致；
    /// 真正的动作仍受使能联锁保护。</para>
    ///
    /// <para>地址取自 <c>recipe-config.json</c> 的 <c>jogSpeedLevels</c>，
    /// 可在参数页的「轴地址」区由管理员修改；写入强制 D 区并校验值域。</para>
    /// </summary>
    Task 异步设置点动速度等级(string 轴号, int 等级, CancellationToken 取消令牌 = default);

    /// <summary>
    /// 轴回零（回原点）。
    ///
    /// <para><b>只置位 M 区线圈</b>：由 PLC 程序执行实际回零动作。地址取自
    /// <c>recipe-config.json</c> 的 <c>homeButtons</c>，按轴查找；找不到配置时抛异常。</para>
    ///
    /// <para><b>要求手动使能</b>：回零是真实的机械动作，未使能时拒绝——与点动同一道联锁。
    /// 界面侧还须做二次确认（危险动作）。</para>
    /// </summary>
    Task 异步回零(string 轴号, CancellationToken 取消令牌 = default);

    /// <summary>
    /// 按<b>配置驱动的轴</b>点动（锁附 X/Y/Z/ZZ、点胶 X/Z）。地址取自
    /// <c>recipe-config.json</c> 的 <c>jogButtons</c>，正/反转各一个 M 区线圈。
    /// <para>先复位相反方向再置位目标方向（与四轴点动同一约定）。要求手动使能。</para>
    /// </summary>
    Task 异步点动轴(string 轴键, int 方向, CancellationToken 取消令牌 = default);

    /// <summary>
    /// 停止配置驱动的轴点动。
    /// <para><b>无条件生效</b>：刻意不检查手动使能——松开即停是安全出口。</para>
    /// </summary>
    Task 异步停止点动轴(string 轴键, CancellationToken 取消令牌 = default);
}


