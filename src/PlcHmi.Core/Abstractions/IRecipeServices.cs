using 上位机.核心.模型;

namespace 上位机.核心.抽象;

/// <summary>
/// 机种（配方）服务。
///
/// <para><b>数据边界（2026-10-12 现场口径）：</b></para>
/// <list type="bullet">
/// <item><b>机种区段 = D2500–D3999（含两端）</b>：落在这个区间的一切数据
/// （锁附点表 X/Y/浮高、锁附辅助地址值、点胶点表 指令码/X/Y/Z …）都<b>跟随机种切换</b>，
/// 每个机种在 <c>config/recipes.json</c> 里存一份该区段的寄存器快照。</item>
/// <item><b>区段之外 = 全局记忆</b>：轴速度参数（D1200 段）、标定、其它点、动作指令等
/// 一律不随机种变动，参数值单独存在 <c>config/parameters.json</c>。</item>
/// </list>
///
/// <para><b>硬约束（务必遵守）：</b>写 PLC 只允许发生在——
/// ① 操作员按下「下发到 PLC」/「下发机种」；② 切换机种后自动下发一次区段快照。
/// 绝不允许输入框一改就写。</para>
/// </summary>
public interface 配方服务接口
{
    /// <summary>机种总数上限。</summary>
    int 配方数量 { get; }

    /// <summary>当前机种号（1 基）。</summary>
    int 当前配方号 { get; }

    /// <summary>当前机种名。</summary>
    string 当前配方名称 { get; }

    /// <summary>机种列表（含示教进度等统计）。</summary>
    IReadOnlyList<配方信息> 配方集合 { get; }

    /// <summary>参数项定义（来自 <c>recipe-config.json</c>）。</summary>
    IReadOnlyList<配方参数规格> 参数规格 { get; }

    /// <summary>当前机种的参数值（键 → 值）。</summary>
    IReadOnlyDictionary<string, double> 当前参数 { get; }

    /// <summary>最近一次启动同步的结果（尚未同步时为 <c>null</c>）。</summary>
    一致性校验报告? 最后一次一致性报告 { get; }

    /// <summary>机种切换完成后触发。</summary>
    event EventHandler? 配方已变更;

    /// <summary>参数值（内存）变化后触发。</summary>
    event EventHandler? 参数已变更;

    /// <summary>
    /// 初始化：读 PLC 机种号 → 载入该机种快照 → 把<b>全局参数从 PLC 读回刷新</b>。
    ///
    /// <para>区段之外的 D 区以 PLC 为准（"开机读一次刷新"）；M 区由轮询引擎持续刷新。</para>
    /// </summary>
    /// <returns>启动同步结果，供界面提示。</returns>
    Task<一致性校验报告> 异步初始化(CancellationToken 取消令牌 = default);

    /// <summary>
    /// 切换机种：写机种号 → 取该机种的 <b>D2500–D3999 区段快照</b>并<b>自动下发</b>到 PLC。
    /// <para>机种区段之外的数据（轴速度参数等）全局记忆，切换时<b>不</b>触碰。</para>
    /// </summary>
    Task 异步切换(int 配方号, CancellationToken 取消令牌 = default);

    /// <summary>
    /// 「保存机种」：把 PLC 当前 D2500–D3999 区段的实际值读回来，存成指定机种的快照并落盘。
    /// <para>要求工程师及以上（会覆盖该机种已保存的数据）。</para>
    /// </summary>
    Task 异步保存机种(int 配方号, CancellationToken 取消令牌 = default);

    /// <summary>
    /// 把指定机种的快照写入 PLC 的 D2500–D3999（不切换机种号）。
    /// <para>用于「现场调好后重新下发」，要求工程师及以上。</para>
    /// </summary>
    Task 异步下发机种(int 配方号, CancellationToken 取消令牌 = default);

    /// <summary>指定机种已保存的区段快照（相对 D2500 的偏移 → 值）；无数据返回空字典。</summary>
    IReadOnlyDictionary<int, int> 区段快照(int 配方号);

    /// <summary>
    /// 用给定的区段快照覆盖指定机种的数据并落盘（CSV 导入用）。
    /// <para>只改本机数据，不写 PLC。要求工程师及以上。</para>
    /// </summary>
    Task 异步写入机种快照(
        int 配方号, IReadOnlyDictionary<int, int> 快照, CancellationToken 取消令牌 = default);

    /// <summary>「下发到 PLC」：把<b>全局参数</b>写入参数区，并同步留底到 <c>parameters.json</c>。</summary>
    Task 异步写入参数(CancellationToken 取消令牌 = default);

    /// <summary>
    /// 「从 PLC 读取」：读参数区当前值（按参数声明类型解码），全部通过范围校验后
    /// 覆盖内存参数并留底到本地当前机种。
    ///
    /// <para>与「下发到 PLC」方向相反——用于现场直接在 PLC 侧改了参数后同步回上位机。
    /// 任一项超出允许范围则<b>整体拒绝</b>（抛 <see cref="InvalidOperationException"/>，不写半个状态）；
    /// 成功返回与本地不同的项的描述清单（形如"X 轴定位速度：200 → 300"），无变化返回空。</para>
    /// </summary>
    Task<IReadOnlyList<string>> 异步读取PLC参数(CancellationToken 取消令牌 = default);

    /// <summary>仅把<b>全局参数</b>保存到本地 <c>parameters.json</c>（不触碰 PLC）。</summary>
    Task 异步保存本地(CancellationToken 取消令牌 = default);

    /// <summary>修改参数值（<b>仅内存</b>，不写 PLC；由下发动作统一写出）。</summary>
    void 设置参数(string 键值, double 值);

    /// <summary>重命名机种（仅本地）。</summary>
    Task 异步重命名(int 配方号, string 名称值, CancellationToken 取消令牌 = default);

    /// <summary>
    /// 把当前机种的参数恢复为配置中的默认值。
    /// <para><b>仅改内存</b>，需要再执行一次下发才会写入 PLC。</para>
    /// </summary>
    void 恢复默认值();

    // ── CSV 导入导出 ────────────────────────────────────────────────────
    // CSV 是交换格式（给 Excel 编辑、机种间拷贝用），recipes.json 仍是主存。
    // 导入只更新本机数据，不会自动写 PLC —— 写 PLC 仍须按「下发」或「切换机种」。

    /// <summary>把指定机种导出为 CSV 文本。</summary>
    string 导出CSV(int 配方号);

    /// <summary>把指定机种导出为 CSV 文件（UTF-8 带 BOM，Excel 双击可开）。</summary>
    Task 异步导出CSV到文件(int 配方号, string 文件路径, CancellationToken 取消令牌 = default);

    /// <summary>批量导出全部机种到一个目录，每个机种一个 CSV 文件。</summary>
    /// <returns>实际写出的文件路径列表。</returns>
    Task<IReadOnlyList<string>> 异步导出全部CSV到目录(
        string 目录, CancellationToken 取消令牌 = default);

    /// <summary>解析 CSV 文本（只解析，不写入任何数据）。</summary>
    /// <param name="csvText">CSV 内容。</param>
    /// <param name="fallbackRecipeNo">文件里没写机种号时使用的号。</param>
    CSV导入结果 解析CSV(string CSV文本, int 回退配方号);

    /// <summary>
    /// 从 CSV 文件导入。
    /// <para>导入后只更新本机数据与界面，<b>不自动写 PLC</b>。</para>
    /// </summary>
    /// <param name="path">CSV 文件路径。</param>
    /// <param name="targetRecipeNo">目标机种号。传 <c>null</c> 用文件里写的号；
    /// 传其他值即可实现「把机种 A 拷到机种 B」。</param>
    Task<CSV导入结果> 异步从文件导入CSV(
        string 文件路径, int? 目标配方号 = null, CancellationToken 取消令牌 = default);
}
