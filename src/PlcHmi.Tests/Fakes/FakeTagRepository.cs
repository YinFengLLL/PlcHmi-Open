using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.上位机测试.替身;

/// <summary>
/// 点位仓储的测试替身：内存字典承载点位定义，记录地址更新与保存次数。
///
/// <para><b>为什么需要它</b>：运行页地址保存链路是「更新点位 → 落盘 → 重建采集计划」，
/// 真仓储会读写 <c>config/tags.json</c>，测试不能碰真实配置文件；
/// 用替身即可断言"保存时确实更新了点位并要求落盘"。</para>
/// </summary>
public sealed class 替身标签仓储 : 标签仓储接口
{
    private readonly Dictionary<string, 标签定义> _标签 = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>异步保存被调用的次数。</summary>
    public int 保存次数;

    /// <summary>被补建的点位名称（用于断言"老现场缺点位时确实补了"）。</summary>
    public List<string> 补建记录 { get; } = [];

    public 替身标签仓储(params 标签定义[] 初始点位)
    {
        foreach (var 条目项 in 初始点位)
        {
            _标签[条目项.名称] = 条目项;
        }
    }

    /// <summary>按 tags.json 的默认结构构建一份替身（运行页 15 个点位）。</summary>
    public static 替身标签仓储 运行页默认点位() => new(
        new 标签定义 { 名称 = "RunStatus", 地址 = "D6", 数据类型 = 标签数据类型.UInt16, 访问 = 标签访问.读取, 分组 = 标签分组.快 },
        new 标签定义 { 名称 = "CycleTime", 地址 = "D1100", 数据类型 = 标签数据类型.UInt32, 访问 = 标签访问.读取, 分组 = 标签分组.快 },
        new 标签定义 { 名称 = "OutputCount", 地址 = "D1102", 数据类型 = 标签数据类型.UInt32, 访问 = 标签访问.读取, 分组 = 标签分组.慢 },
        new 标签定义 { 名称 = "TargetCount", 地址 = "D1104", 数据类型 = 标签数据类型.UInt32, 访问 = 标签访问.读取, 分组 = 标签分组.慢 },
        new 标签定义 { 名称 = "GoodCount", 地址 = "D1106", 数据类型 = 标签数据类型.UInt32, 访问 = 标签访问.读取, 分组 = 标签分组.慢 },
        new 标签定义 { 名称 = "BadCount", 地址 = "D1108", 数据类型 = 标签数据类型.UInt32, 访问 = 标签访问.读取, 分组 = 标签分组.慢 },
        new 标签定义 { 名称 = "ScrewDone", 地址 = "M490", 数据类型 = 标签数据类型.布尔, 访问 = 标签访问.读取, 分组 = 标签分组.快 },
        new 标签定义 { 名称 = "GlueDone", 地址 = "M491", 数据类型 = 标签数据类型.布尔, 访问 = 标签访问.读取, 分组 = 标签分组.快 },
        new 标签定义 { 名称 = "ProcessDone", 地址 = "M492", 数据类型 = 标签数据类型.布尔, 访问 = 标签访问.读取, 分组 = 标签分组.快 },
        new 标签定义 { 名称 = "ScrewCount", 地址 = "D1116", 数据类型 = 标签数据类型.Int16, 访问 = 标签访问.读取, 分组 = 标签分组.快 },
        new 标签定义 { 名称 = "CmdStart", 地址 = "M200", 数据类型 = 标签数据类型.布尔, 访问 = 标签访问.写入, 分组 = 标签分组.命令 },
        new 标签定义 { 名称 = "CmdStop", 地址 = "M201", 数据类型 = 标签数据类型.布尔, 访问 = 标签访问.写入, 分组 = 标签分组.命令 },
        new 标签定义 { 名称 = "CmdReset", 地址 = "M202", 数据类型 = 标签数据类型.布尔, 访问 = 标签访问.写入, 分组 = 标签分组.命令 },
        new 标签定义 { 名称 = "CmdDataClear", 地址 = "M1000", 数据类型 = 标签数据类型.布尔, 访问 = 标签访问.写入, 分组 = 标签分组.命令 },
        new 标签定义 { 名称 = "CmdAlarmClear", 地址 = "M203", 数据类型 = 标签数据类型.布尔, 访问 = 标签访问.写入, 分组 = 标签分组.命令 },
        // 干扰项：用于校验"与其它点位冲突"
        new 标签定义 { 名称 = "AxisXPosition", 地址 = "D1221", 数据类型 = 标签数据类型.Float32, 访问 = 标签访问.读取, 分组 = 标签分组.快 });

    /// <inheritdoc />
    public IReadOnlyList<标签定义> 全部 => _标签.Values.ToList();

    /// <inheritdoc />
    public 标签定义? 查找(string 名称值) =>
        _标签.TryGetValue(名称值, out var 条目项) ? 条目项 : null;

    /// <inheritdoc />
    public IReadOnlyList<标签定义> 获取分组(标签分组 group) =>
        _标签.Values.Where(标签条目 => 标签条目.分组 == group).ToList();

    /// <inheritdoc />
    public Task 异步重载(CancellationToken 取消令牌 = default) => Task.CompletedTask;

    /// <inheritdoc />
    public bool 尝试新增(标签定义 定义项)
    {
        if (_标签.ContainsKey(定义项.名称))
        {
            return false;
        }

        _标签[定义项.名称] = 定义项;
        补建记录.Add(定义项.名称);
        return true;
    }

    /// <inheritdoc />
    public bool 尝试更新地址(string 名称值, string 地址值)
    {
        if (!_标签.TryGetValue(名称值, out var 条目项))
        {
            return false;
        }

        条目项.地址 = 地址值;
        return true;
    }

    /// <inheritdoc />
    public Task 异步保存(CancellationToken 取消令牌 = default)
    {
        Interlocked.Increment(ref 保存次数);
        return Task.CompletedTask;
    }
}
