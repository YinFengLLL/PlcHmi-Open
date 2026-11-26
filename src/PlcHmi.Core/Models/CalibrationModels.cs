using 上位机.核心.枚举;

namespace 上位机.核心.模型;

/// <summary>标定/辅助点里的一个数值字段定义（一个坐标或偏移量）。</summary>
public sealed class 标定字段定义
{
    /// <summary>键，如 <c>X</c> / <c>Y</c> / <c>Z</c> / <c>偏移量</c>。</summary>
    public string 键 { get; set; } = string.Empty;

    /// <summary>界面显示名称。</summary>
    public string 名称 { get; set; } = string.Empty;

    /// <summary>D 区地址（序号）。</summary>
    public int 基址 { get; set; }

    /// <summary>数据类型（默认 Float32）。</summary>
    public 标签数据类型 数据类型 { get; set; } = 标签数据类型.Float32;

    /// <summary>工程单位（可空）。</summary>
    public string? 单元 { get; set; }

    /// <summary>本字段占用的寄存器数量。</summary>
    public int 寄存器数量 => 数据类型 switch
    {
        标签数据类型.Float32 or 标签数据类型.Int32 or 标签数据类型.UInt32 => 2,
        标签数据类型.Float64 => 4,
        _ => 1,
    };

    /// <summary>显示名称（为空时回退到键）。</summary>
    public string 显示名称 => string.IsNullOrWhiteSpace(名称) ? 键 : 名称;
}

/// <summary>
/// 标定示教配置。对应 <c>recipe-config.json</c> 的 <c>calibration</c> 节。
///
/// <para><b>两套相机（2026-10-17 现场要求）：</b>上相机与下相机各一组「中心点 / 偏移量 / 标定启动」。
/// 上相机沿用原有字段名（<see cref="中心点"/> / <see cref="每点偏移"/> / <see cref="启动地址"/>）
/// 以便老配置直接兼容；下相机是新增的一组，字段名带「下相机」前缀。</para>
///
/// <para><b>下相机没有 Y：</b>下相机只做 X/Z 平面标定（Y 不参与），因此
/// <see cref="下相机中心点"/> 只有 X / Z 两个字段，界面也不给 Y 输入框——
/// 这与上相机"填 X/Y/Z 三个地址"的处理模式刻意不同。</para>
/// </summary>
public sealed class 标定配置
{
    /// <summary>上相机标定中心点坐标字段（X / Y / Z）。</summary>
    public List<标定字段定义> 中心点 { get; set; } = new();

    /// <summary>上相机每点偏移量字段。</summary>
    public 标定字段定义 每点偏移 { get; set; } = new() { 键 = "偏移量", 名称 = "上相机标定偏移量", 基址 = 3606 };

    /// <summary>「上相机标定启动」按钮地址（M 区）。</summary>
    public string 启动地址 { get; set; } = "M420";

    /// <summary>
    /// 下相机标定起点字段（<b>只有 X / Z</b>）。
    /// <para>键为 <c>X</c> / <c>Z</c>；不提供 Y——下相机的地址处理模式与上相机相同，
    /// 唯独不需要 Y 地址。</para>
    /// <para><b>老配置缺这两个字段时</b>由 <see cref="补齐下相机"/> 按
    /// <see cref="下相机缺省中心点"/> 补上：否则「编辑地址」里根本看不到下相机 X/Z，
    /// 下发时会报"缺少该字段"（2026-10-21 现场报障）。</para>
    /// </summary>
    public List<标定字段定义> 下相机中心点 { get; set; } = new();

    /// <summary>下相机每点偏移量字段。</summary>
    public 标定字段定义 下相机每点偏移 { get; set; } =
        new() { 键 = "偏移量", 名称 = "下相机标定偏移量", 基址 = 下相机偏移缺省基址 };

    /// <summary>「下相机标定启动」按钮地址（M 区）。</summary>
    public string 下相机启动地址 { get; set; } = 下相机启动缺省地址;

    /// <summary>下相机起点 X 的出厂基址（D 区，落在点胶结构体数组 3500–3739 之后）。</summary>
    public const int 下相机X缺省基址 = 3740;

    /// <summary>下相机起点 Z 的出厂基址（D 区）。</summary>
    public const int 下相机Z缺省基址 = 3742;

    /// <summary>下相机每点偏移的出厂基址（D 区）：紧跟起点 X/Z（3740–3743）之后。</summary>
    public const int 下相机偏移缺省基址 = 3744;

    /// <summary>下相机标定启动的出厂线圈。</summary>
    public const string 下相机启动缺省地址 = "M83";

    /// <summary>
    /// 标定不能占用的保留区（D3500–D3743）：3500–3739 是点胶单点参数结构体数组，
    /// 3740–3743 是下相机起点 X/Z。落在这里的地址会与它们互相覆盖。
    /// </summary>
    private const int 保留区起始 = 3500;

    private const int 保留区结束 = 3743;

    /// <summary>按键查找上相机中心点字段。</summary>
    public 标定字段定义? 查找中心点(string 键) =>
        中心点.FirstOrDefault(字段项 => string.Equals(字段项.键, 键, StringComparison.OrdinalIgnoreCase));

    /// <summary>按键查找下相机字段（X / Z）。</summary>
    public 标定字段定义? 查找下相机(string 键) =>
        下相机中心点.FirstOrDefault(字段项 => string.Equals(字段项.键, 键, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 下相机标定起点的出厂字段。<b>每次返回新实例</b>——共享同一个对象会被某台机器的编辑改掉。
    /// </summary>
    public static IReadOnlyList<标定字段定义> 下相机缺省中心点() =>
    [
        new()
        {
            键 = "X", 名称 = "下相机标定起点X", 基址 = 下相机X缺省基址,
            数据类型 = 标签数据类型.Float32, 单元 = "MM",
        },
        new()
        {
            键 = "Z", 名称 = "下相机标定起点Z", 基址 = 下相机Z缺省基址,
            数据类型 = 标签数据类型.Float32, 单元 = "MM",
        },
    ];

    /// <summary>
    /// 给老现场配置补上下相机标定：缺 X / Z 字段就补字段；偏移 / 启动地址缺失或落在保留区就归到出厂值。
    ///
    /// <para><b>为什么必须补（2026-10-21 现场报障）：</b>覆盖升级不会覆盖现场的
    /// <c>recipe-config.json</c>，而"下相机"是较新版本才新增的一节。老文件反序列化后
    /// <see cref="下相机中心点"/> 为空 → 「编辑地址」里没有下相机 X/Z 可改 →
    /// 下发时报"标定配置缺少该字段"，现场完全没有入口把它补出来。</para>
    /// </summary>
    /// <returns>本次补齐的项数（0 = 无需补齐）。</returns>
    public int 补齐下相机()
    {
        var 补齐 = 0;

        foreach (var 缺省 in 下相机缺省中心点())
        {
            if (查找下相机(缺省.键) is not null)
            {
                continue;
            }

            下相机中心点.Add(缺省);
            补齐++;
        }

        // 旧口径 3612 落在点胶结构体数组里（会与单点工艺参数互相覆盖），一并纠正
        if (下相机每点偏移.基址 <= 0
            || 下相机每点偏移.基址 is >= 保留区起始 and <= 保留区结束)
        {
            下相机每点偏移.基址 = 下相机偏移缺省基址;
            补齐++;
        }

        if (string.IsNullOrWhiteSpace(下相机启动地址))
        {
            下相机启动地址 = 下相机启动缺省地址;
            补齐++;
        }

        return 补齐;
    }
}

