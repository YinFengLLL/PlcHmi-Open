using PlcHmi.Core.Models;

namespace PlcHmi.Core.Abstractions;

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
public interface IRecipeService
{
    /// <summary>机种总数上限。</summary>
    int RecipeCount { get; }

    /// <summary>当前机种号（1 基）。</summary>
    int CurrentRecipeNumber { get; }

    /// <summary>当前机种名。</summary>
    string CurrentRecipeName { get; }

    /// <summary>机种列表（含示教进度等统计）。</summary>
    IReadOnlyList<RecipeInfo> RecipeCollection { get; }

    /// <summary>参数项定义（来自 <c>recipe-config.json</c>）。</summary>
    IReadOnlyList<RecipeParameterSpec> ParameterSpec { get; }

    /// <summary>当前机种的参数值（键 → 值）。</summary>
    IReadOnlyDictionary<string, double> CurrentParameter { get; }

    /// <summary>最近一次启动同步的结果（尚未同步时为 <c>null</c>）。</summary>
    ConsistencyValidateReport? LastOnceConsistencyReport { get; }

    /// <summary>机种切换完成后触发。</summary>
    event EventHandler? recipeChanged;

    /// <summary>参数值（内存）变化后触发。</summary>
    event EventHandler? parameterChanged;

    /// <summary>
    /// 初始化：读 PLC 机种号 → 载入该机种快照 → 把<b>全局参数从 PLC 读回刷新</b>。
    ///
    /// <para>区段之外的 D 区以 PLC 为准（"开机读一次刷新"）；M 区由轮询引擎持续刷新。</para>
    /// </summary>
    /// <returns>启动同步结果，供界面提示。</returns>
    Task<ConsistencyValidateReport> InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 切换机种：写机种号 → 取该机种的 <b>D2500–D3999 区段快照</b>并<b>自动下发</b>到 PLC。
    /// <para>机种区段之外的数据（轴速度参数等）全局记忆，切换时<b>不</b>触碰。</para>
    /// </summary>
    Task SwitchAsync(int RecipeNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// 「保存机种」：把 PLC 当前 D2500–D3999 区段的实际值读回来，存成指定机种的快照并落盘。
    /// <para>要求工程师及以上（会覆盖该机种已保存的数据）。</para>
    /// </summary>
    Task SaveMachineModelAsync(int RecipeNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// 把指定机种的快照写入 PLC 的 D2500–D3999（不切换机种号）。
    /// <para>用于「现场调好后重新下发」，要求工程师及以上。</para>
    /// </summary>
    Task SendMachineModelAsync(int RecipeNumber, CancellationToken cancellationToken = default);

    /// <summary>指定机种已保存的区段快照（相对 D2500 的偏移 → 值）；无数据返回空字典。</summary>
    IReadOnlyDictionary<int, int> SectionSnapshot(int RecipeNumber);

    /// <summary>
    /// 用给定的区段快照覆盖指定机种的数据并落盘（CSV 导入用）。
    /// <para>只改本机数据，不写 PLC。要求工程师及以上。</para>
    /// </summary>
    Task WriteMachineModelSnapshotAsync(
        int RecipeNumber, IReadOnlyDictionary<int, int> Snapshot, CancellationToken cancellationToken = default);

    /// <summary>「下发到 PLC」：把<b>全局参数</b>写入参数区，并同步留底到 <c>parameters.json</c>。</summary>
    Task WriteParameterAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 「从 PLC 读取」：读参数区当前值（按参数声明类型解码），全部通过范围校验后
    /// 覆盖内存参数并留底到本地当前机种。
    ///
    /// <para>与「下发到 PLC」方向相反——用于现场直接在 PLC 侧改了参数后同步回上位机。
    /// 任一项超出允许范围则<b>整体拒绝</b>（抛 <see cref="InvalidOperationException"/>，不写半个状态）；
    /// 成功返回与本地不同的项的描述清单（形如"X 轴定位速度：200 → 300"），无变化返回空。</para>
    /// </summary>
    Task<IReadOnlyList<string>> ReadPlcParameterAsync(CancellationToken cancellationToken = default);

    /// <summary>仅把<b>全局参数</b>保存到本地 <c>parameters.json</c>（不触碰 PLC）。</summary>
    Task SaveThisGroundAsync(CancellationToken cancellationToken = default);

    /// <summary>修改参数值（<b>仅内存</b>，不写 PLC；由下发动作统一写出）。</summary>
    void SetParameter(string keyValue, double Value);

    /// <summary>重命名机种（仅本地）。</summary>
    Task RenameAsync(int RecipeNumber, string nameValue, CancellationToken cancellationToken = default);

    /// <summary>
    /// 把当前机种的参数恢复为配置中的默认值。
    /// <para><b>仅改内存</b>，需要再执行一次下发才会写入 PLC。</para>
    /// </summary>
    void RestoreDefaultValue();

    // ── CSV 导入导出 ────────────────────────────────────────────────────
    // CSV 是交换格式（给 Excel 编辑、机种间拷贝用），recipes.json 仍是主存。
    // 导入只更新本机数据，不会自动写 PLC —— 写 PLC 仍须按「下发」或「切换机种」。

    /// <summary>把指定机种导出为 CSV 文本。</summary>
    string ExportCsv(int RecipeNumber);

    /// <summary>把指定机种导出为 CSV 文件（UTF-8 带 BOM，Excel 双击可开）。</summary>
    Task ExportCsvToFileAsync(int RecipeNumber, string filePath, CancellationToken cancellationToken = default);

    /// <summary>批量导出全部机种到一个目录，每个机种一个 CSV 文件。</summary>
    /// <returns>实际写出的文件路径列表。</returns>
    Task<IReadOnlyList<string>> ExportAllCsvToDirectoryAsync(
        string dir, CancellationToken cancellationToken = default);

    /// <summary>解析 CSV 文本（只解析，不写入任何数据）。</summary>
    /// <param name="csvText">CSV 内容。</param>
    /// <param name="fallbackRecipeNo">文件里没写机种号时使用的号。</param>
    CsvImportResult ParseCsv(string csvText, int fallbackRecipeNumber);

    /// <summary>
    /// 从 CSV 文件导入。
    /// <para>导入后只更新本机数据与界面，<b>不自动写 PLC</b>。</para>
    /// </summary>
    /// <param name="path">CSV 文件路径。</param>
    /// <param name="targetRecipeNo">目标机种号。传 <c>null</c> 用文件里写的号；
    /// 传其他值即可实现「把机种 A 拷到机种 B」。</param>
    Task<CsvImportResult> FromFileImportCsvAsync(
        string filePath, int? targetRecipeNumber = null, CancellationToken cancellationToken = default);
}
