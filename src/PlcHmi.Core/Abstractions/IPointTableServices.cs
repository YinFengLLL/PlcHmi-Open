using PlcHmi.Core.Models;

namespace PlcHmi.Core.Abstractions;

/// <summary>
/// 通用点表服务。锁附示教与点胶示教共用同一套读写逻辑，仅配置不同。
///
/// <para><b>布局：</b>每张点表按列分区存放（列基址 + 序号 × 字每点位）。
/// 读一整列是<b>一次块读</b>（30 点 × 2 字 = 60 字，远低于单次上限）。</para>
///
/// <para><b>写入时机硬约束：</b>点值只允许在「保存坐标点」或「参数写入」时写入 PLC，
/// 输入框改动只改内存。未示教的点（全 0）在下发前逐点做范围校验，
/// 防止把轴驱动到原点。</para>
/// </summary>
public interface IPointTableService
{
    /// <summary>某张点表的数据变化（参数为表键）。</summary>
    event EventHandler<string>? pointTableChanged;

    /// <summary>全部点表定义。</summary>
    IReadOnlyList<PointTableDefinition> AllPointTable { get; }

    /// <summary>按表键取定义。</summary>
    PointTableDefinition Table(string Key);

    /// <summary>某张点表的点数。</summary>
    int PointCount(string Key);

    /// <summary>某张点表的当前行（本地缓存，供界面绑定）。</summary>
    IReadOnlyList<PointValueRow> CurrentPointTable(string Key);

    /// <summary>初始化：按<b>当前机种快照</b>装载全部点表（离线也可用）。</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 按<b>当前机种快照</b>重新装载一张点表并设为当前缓存。
    /// <para>切换机种后回到本页即会刷新到新机种的数据。</para>
    /// </summary>
    Task LoadPointTableAsync(string Key, CancellationToken cancellationToken = default);

    /// <summary>修改某点某列的值（<b>仅内存</b>，不写 PLC）。</summary>
    void SetPointValue(string Key, int Sequence, string ColumnKey, double Value);

    /// <summary>清空某点（<b>仅内存</b>）。</summary>
    void ClearEmptyPoint(string Key, int Sequence);

    /// <summary>按各列的位置标签读取当前 PLC 位置，构造一行（用于「坐标示教」）。</summary>
    PointValueRow ReadCurrentPosition(string Key);

    /// <summary>把当前位置记录到指定点：写 PLC 并落盘。要求工程师权限（编辑示教）。</summary>
    Task TeachCurrentPositionAsync(string Key, int Sequence, CancellationToken cancellationToken = default);

    /// <summary>把整张点表（只含本表各列）写入 PLC（逐点范围校验）。要求工程师权限（编辑示教）。</summary>
    Task WritePointTableAsync(string Key, CancellationToken cancellationToken = default);

    /// <summary>
    /// 读「当前浮高」（REAL，占 2 字，地址见 <see cref="PointTableDefinition.CurrentFloatHeightAddress"/>）。
    ///
    /// <para><b>2026-10-24 现场要求（锁附页）：</b>手动区显示它，「保存坐标点」把它写进选中点。
    /// 未配置地址 / PLC 未连接 / 读不全时返回 <c>null</c>——绝不返回 0 冒充读数。</para>
    /// </summary>
    Task<double?> ReadCurrentFloatHeightAsync(string Key, CancellationToken cancellationToken = default);

    /// <summary>
    /// <b>参数写入</b>（原示教页的「下发点表」，2026-10-21 现场要求）：把上位机当前的
    /// <b>机种区段 D2500–D3999 整段</b>写入 PLC，并同时触发机种保存。
    ///
    /// <para><b>三步：</b>① 把本页点表的最新编辑并入当前机种快照（否则"刚改的坐标"不在快照里，
    /// 整段写下去会把旧值推回 PLC）；② 整段下发（先清零、再写快照、写机种号）；
    /// ③ 把 PLC 里刚写下的整段读回并存成当前机种（等同机种页「保存机种」）。</para>
    ///
    /// <para>与 <see cref="WritePointTableAsync"/> 的区别：那个只写点表各列；本方法写整段，
    /// 单点工艺参数 / 标定等也一并随快照落地。要求工程师权限（编辑示教 / 切换配方）。</para>
    /// </summary>
    Task ParameterWriteAsync(string Key, CancellationToken cancellationToken = default);

    /// <summary>
    /// 写入某点的<b>指令码</b>（点胶示教页的下拉框）：立即写 PLC 的单字，同步内存点表并并入机种快照。
    /// 要求工程师权限（编辑示教）。
    /// <para><b>为什么单独一个方法：</b>指令码是"选一个码就立即生效"的整型单字，
    /// 既不该走整表下发（会连带未示教的坐标），也不该只改内存（界面显示与 PLC 会不一致）。</para>
    /// </summary>
    /// <param name="Key">表键（<c>点胶</c>）。</param>
    /// <param name="Sequence">点序号（0 基）。</param>
    /// <param name="code">指令码（0…9，见 <see cref="InstructionCodeDefinition"/>）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task WriteInstructionCodeAsync(string Key, int Sequence, int code, CancellationToken cancellationToken = default);

    /// <summary>
    /// 从 PLC <b>按需</b>读取整张点表的当前值（2026-10-21 现场要求）。
    ///
    /// <para><b>为什么是按需：</b>点表落在机种区段（D2500–D3999），该区段不参与周期采集，
    /// 界面上的点表默认显示"上次装载 / 下发"的值。要看 PLC 现状，由操作员按「读取点表」触发一次。</para>
    ///
    /// <para><b>只写内存，不写机种快照：</b>机种数据以「保存机种」为准——
    /// 只是看一眼 PLC 就把机种改掉，会让现场分不清"保存过的"和"刚读到的"。</para>
    /// </summary>
    Task ReadPointTableAsync(string Key, CancellationToken cancellationToken = default);


}

/// <summary>辅助点（其它点）服务：点位定义的增删改查、坐标读写与 CSV 导入导出。</summary>
public interface IAuxPointService
{
    /// <summary>点位定义或数据变化。</summary>
    event EventHandler? changed;

    /// <summary>全部点位定义。</summary>
    IReadOnlyList<AuxPointDefinition> PointCollection { get; }

    /// <summary>配置（范围、允许区间）。</summary>
    OtherPointConfig Config { get; }

    /// <summary>初始化：加载落盘的点位定义。</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>读取一个点位的当前坐标（从 PLC）。</summary>
    Task<(double X, double Y, double Z)> ReadCoordinateAsync(AuxPointDefinition PointItem, CancellationToken cancellationToken = default);

    /// <summary>写入一个点位的坐标（到 PLC）。要求检查范围。</summary>
    Task WriteCoordinateAsync(AuxPointDefinition PointItem, double X, double Y, double Z, CancellationToken cancellationToken = default);

    /// <summary>新增点位定义并落盘。</summary>
    Task AddAsync(AuxPointDefinition PointItem, CancellationToken cancellationToken = default);

    /// <summary>
    /// 编辑点位定义并落盘；<b>允许改名</b>（按 <paramref name="originalName"/> 定位后整体替换）。
    /// <para>改名后与其它点重名会被拒绝：名称是"按名查找 / 删除 / CSV 导入"的依据。</para>
    /// </summary>
    Task EditAsync(string originalName, AuxPointDefinition PointItem, CancellationToken cancellationToken = default);

    /// <summary>删除点位定义并落盘。</summary>
    Task DeleteAsync(string nameValue, CancellationToken cancellationToken = default);

    /// <summary>按关键字查询（名称模糊匹配，空串返回全部）。</summary>
    IReadOnlyList<AuxPointDefinition> Query(string keyword);

    /// <summary>导出为 CSV 文本。</summary>
    string ExportCsv();

    /// <summary>导出为 CSV 文件。</summary>
    Task ExportCsvToFileAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>从 CSV 文件导入并落盘。</summary>
    Task<int> FromFileImportCsvAsync(string filePath, CancellationToken cancellationToken = default);
}

/// <summary>标定示教服务：标定中心点坐标、每点偏移量的读写与标定启动。</summary>
public interface ICalibrationService
{
    /// <summary>标定配置。</summary>
    CalibrationConfig Config { get; }

    /// <summary>读取中心点坐标（X / Y / Z）。</summary>
    Task<(double X, double Y, double Z)> ReadCenterAsync(CancellationToken cancellationToken = default);

    /// <summary>写入中心点坐标。</summary>
    Task WriteCenterAsync(double X, double Y, double Z, CancellationToken cancellationToken = default);

    /// <summary>读取每点偏移量。</summary>
    Task<double> ReadOffsetAsync(CancellationToken cancellationToken = default);

    /// <summary>写入每点偏移量。</summary>
    Task WriteOffsetAsync(double Value, CancellationToken cancellationToken = default);

    /// <summary>触发「上相机标定启动」按钮（脉冲 M 区线圈）。</summary>
    Task CalibrationStartAsync(CancellationToken cancellationToken = default);

    // ── 下相机（2026-10-17 新增；地址处理模式与上相机一致，但没有 Y）──────

    /// <summary>读取下相机标定起点坐标（<b>只有 X / Z</b>）。</summary>
    Task<(double X, double Z)> ReadBottomCameraCenterAsync(CancellationToken cancellationToken = default);

    /// <summary>写入下相机标定起点坐标（<b>只有 X / Z</b>）。</summary>
    Task WriteBottomCameraCenterAsync(double X, double Z, CancellationToken cancellationToken = default);

    /// <summary>读取下相机每点偏移量。</summary>
    Task<double> ReadBottomCameraOffsetAsync(CancellationToken cancellationToken = default);

    /// <summary>写入下相机每点偏移量。</summary>
    Task WriteBottomCameraOffsetAsync(double Value, CancellationToken cancellationToken = default);

    /// <summary>触发「下相机标定启动」按钮（脉冲 M 区线圈）。</summary>
    Task BottomCameraCalibrationStartAsync(CancellationToken cancellationToken = default);
}
