using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using 上位机.应用.服务;
using 上位机.应用.视图;
using 上位机.核心.抽象;
using 上位机.核心.模型;

namespace 上位机.应用.视图模型;

// 单点 / 分组的行视图模型见 IoRowViewModels.cs。

/// <summary>
/// IO 点位监控页 ViewModel。
///
/// <para><b>页面职责：</b>按八进制分组展示 X 输入与 Y 输出的实时状态；
/// 允许工程师直接编辑每个点的含义并保存回 <c>io-points.json</c>。</para>
///
/// <para>程序本身不需要知道 “X0 是什么”——这是本模板区别于普通上位机的关键设计，
/// 换设备零改代码。</para>
/// </summary>
public sealed partial class IO视图模型 : 视图模型基类
{
    /// <summary>每组的点位数（八进制自然分组）。</summary>
    private const int 分组大小 = 8;

    private readonly IO点位服务接口 _IO点位表;
    private readonly 用户服务接口 _用户项;
    private readonly 轮询引擎接口 _轮询;
    private readonly 对话框服务接口 _对话框;
    private readonly IO诊断服务接口 _诊断;
    private readonly ILogger<IO视图模型> _日志;

    private readonly Dictionary<string, IO点位视图模型> _序号 = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>摘要刷新脏标记：合并同一批状态事件（见 <see cref="请求刷新摘要"/>）。</summary>
    private bool _摘要待刷新;

    /// <summary>筛选文本防抖：输入过程中只重启计时器，停手 200ms 才重建分组。</summary>
    private readonly DispatcherTimer _筛选防抖 = new() { Interval = TimeSpan.FromMilliseconds(200) };

    [ObservableProperty] private bool _是否编辑模式;
    [ObservableProperty] private string _筛选文本 = string.Empty;
    [ObservableProperty] private string _输入摘要 = string.Empty;
    [ObservableProperty] private string _输出摘要 = string.Empty;
    [ObservableProperty] private bool _显示已定义;

    /// <summary>是否允许新增点位区（管理员专属：这会改变采集范围）。</summary>
    [ObservableProperty] private bool _可新增点位;

    /// <summary>是否显示「IO 通信诊断」入口（与改 IO 配置同权限：诊断结论要用来改配置）。</summary>
    [ObservableProperty] private bool _可诊断;

    // 文本变化只重启防抖计时器，停手后才重建分组（逐字符全量重建太重）。
    // _序号 始终保持全量点位（实时状态刷新不能丢）；复选框等条件仍即时重建。
    partial void On筛选文本Changed(string value) => 重启筛选防抖();

    partial void On显示已定义Changed(bool value) => 构建分组();

    public IO视图模型(
        IO点位服务接口 IO点位表,
        用户服务接口 用户项,
        轮询引擎接口 轮询,
        对话框服务接口 对话框集合,
        IO诊断服务接口 诊断,
        ILogger<IO视图模型> 日志)
    {
        _IO点位表 = IO点位表;
        _用户项 = 用户项;
        _轮询 = 轮询;
        _对话框 = 对话框集合;
        _诊断 = 诊断;
        _日志 = 日志;

        _筛选防抖.Tick += (_, _) =>
        {
            _筛选防抖.Stop();
            构建分组();
        };

        标题 = "IO 点位监控";
        副标题 = $"机型 {IO点位表.表格.元.PLC模型} · X/Y 按八进制编号";

        构建分组();

        _IO点位表.状态已变更 += 状态已变更;
        _用户项.用户已变更 += (_, _) => 在界面线程执行(刷新权限);

        刷新权限();
    }

    /// <summary>X 输入分组。</summary>
    public 批量可观察集合<IO分组视图模型> 输入分组 { get; } = new();

    /// <summary>Y 输出分组。</summary>
    public 批量可观察集合<IO分组视图模型> 输出分组 { get; } = new();

    /// <inheritdoc />
    public override Task 异步初始化()
    {
        刷新摘要();
        return Task.CompletedTask;
    }

    /// <summary>进入/退出编辑模式时，同步所有点位的编辑状态。</summary>
    partial void On是否编辑模式Changed(bool value)
    {
        foreach (var 视图模型 in _序号.Values)
        {
            视图模型.是否编辑中 = value;
        }
    }

    // ── 命令 ───────────────────────────────────────────────────────────

    [RelayCommand]
    private void 切换编辑模式() => 是否编辑模式 = !是否编辑模式;

    [RelayCommand]
    private async Task 异步保存()
    {
        try
        {
            是否忙碌 = true;
            await _IO点位表.异步保存().ConfigureAwait(false);

            在界面线程执行(() =>
            {
                是否编辑模式 = false;
                刷新摘要();
                _对话框.提示("保存成功", "IO 点位含义已保存到 io-points.json。");
            });
        }
        catch (Exception 异常)
        {
            _日志.LogError(异常, "保存 IO 点位表失败");
            _对话框.错误信息("保存失败", 异常.Message);
        }
        finally
        {
            是否忙碌 = false;
        }
    }

    [RelayCommand]
    private void 取消编辑()
    {
        // 放弃编辑：从配置文件重新加载，完成后再重建分组。
        // 顺序是硬约束——曾经的"即发即忘重载 + 立即构建分组"在重载未完成时
        // 就用了旧表/旧状态，界面停留在重载前的数据（异步重载 完成时不推事件，
        // 直到下次筛选或进出页才会纠正）。构建分组动的是绑定集合，必须回界面线程。
        是否编辑模式 = false;

        异步安全调用.即发即忘(async () =>
        {
            await _IO点位表.异步重载().ConfigureAwait(false);
            在界面线程执行(构建分组);
        }, _日志, "重新加载 IO 点位表");
    }

    [RelayCommand]
    private void 清空筛选() => 筛选文本 = string.Empty;

    /// <summary>在 X 输入区末尾追加一栏（8 点，八进制自动递增）。</summary>
    [RelayCommand]
    private Task 异步新增输入区() => 异步新增一栏("X");

    /// <summary>在 Y 输出区末尾追加一栏（8 点，八进制自动递增）。</summary>
    [RelayCommand]
    private Task 异步新增输出区() => 异步新增一栏("Y");

    /// <summary>
    /// 新增一栏 X / Y 点位。
    ///
    /// <para>顺序是硬约束：<b>先改表并落盘，再重建采集计划</b>。
    /// 反过来先重建，新点还没进表，重建等于白做；改了表却不重建，
    /// 新点会出现在界面上但永远读不到状态 —— 那比不显示更糟，
    /// 操作员会以为那个点只是没有信号。</para>
    ///
    /// <para>轮询引擎重建时会重新从 <see cref="IO点位服务接口.表格"/> 取点位，
    /// 所以运行中新增的点不需要重启程序即可参与采集。</para>
    /// </summary>
    private async Task 异步新增一栏(string 前缀)
    {
        if (!可新增点位)
        {
            _对话框.提示("权限不足", "只有管理员可以新增 IO 点位区。");
            return;
        }

        var 区名 = string.Equals(前缀, "X", StringComparison.OrdinalIgnoreCase) ? "X 输入区" : "Y 输出区";
        var 预览 = _IO点位表.预览下一栏(前缀);
        var 每组点数 = _IO点位表.表格.元.分组大小;

        if (!_对话框.确认($"新增 {区名}",
                $"将在 {区名}末尾追加一栏 {预览}（{每组点数} 点，编号按八进制自动递增），\n" +
                "写入 io-points.json 并立即重建采集计划。\n\n是否继续？"))
        {
            return;
        }

        try
        {
            是否忙碌 = true;

            var 成功 = await 界面命令.异步执行(_日志, _对话框, "新增一栏", async () =>
            {
                await _IO点位表.异步新增一栏(前缀).ConfigureAwait(false);
                await _轮询.异步重建方案列表().ConfigureAwait(false);
            }, 区名).ConfigureAwait(true);

            // 只有真的成功才报喜：失败时也弹"新增成功"，比不提示更危险
            if (成功)
            {
                在界面线程执行(() =>
                {
                    构建分组();
                    _对话框.提示("新增成功", $"{区名} 已新增一栏 {预览}，采集计划已重建。");
                });
            }
        }
        finally
        {
            是否忙碌 = false;
        }
    }

    /// <summary>新增点位区会改变采集范围，与改 PLC 连接同级，只给管理员。</summary>
    private void 刷新权限()
    {
        可新增点位 = _用户项.有(权限.编辑IO点位);
        可诊断 = _用户项.有(权限.编辑IO点位);
    }

    /// <summary>
    /// IO 通信诊断：X / Y 段 × FC01 / FC02 的原始读数并排，定位"显示与物理对不上"。
    /// <para>基址取 <c>io-points.json</c> 元里配置的值（与解析器同一套 X/Y 基址）。</para>
    /// </summary>
    [RelayCommand]
    private void 异步IO诊断()
    {
        var 元 = _IO点位表.表格.元;

        IO诊断对话框.显示(_诊断, 元.X基址地址, 元.Y基址地址);
    }

    // ── 构建分组 ───────────────────────────────────────────────────────

    private void 重启筛选防抖()
    {
        _筛选防抖.Stop();
        _筛选防抖.Start();
    }

    private void 构建分组()
    {
        _序号.Clear();

        // 先建全量索引（实时状态刷新与编辑模式同步都依赖它），再按筛选条件生成分组展示
        序号侧(_IO点位表.表格.输入);
        序号侧(_IO点位表.表格.输出);

        var 查询 = 筛选文本.Trim();
        构建侧(_IO点位表.表格.输入, 输入分组, "X", 查询);
        构建侧(_IO点位表.表格.输出, 输出分组, "Y", 查询);

        刷新摘要();
    }

    /// <summary>把一侧全部点位建入索引（不受筛选影响）。</summary>
    private void 序号侧(IReadOnlyList<IO点位> 点位列表)
    {
        foreach (var 点位项 in 点位列表)
        {
            var 视图模型 = new IO点位视图模型(点位项);
            视图模型.是否开启 = _IO点位表.获取状态(点位项.代码).是否开启;
            _序号[点位项.代码] = 视图模型;
        }
    }

    private void 构建侧(IReadOnlyList<IO点位> 点位列表, 批量可观察集合<IO分组视图模型> 目标, string 前缀, string 查询)
    {
        var 视图模型项 = 点位列表
            .Select(点位项 => _序号[点位项.代码])
            .Where(视图模型 => 匹配筛选(视图模型, 查询))
            .ToList();

        // 按八进制高位分组：X0-X7、X10-X17、X20-X27……
        var 分组列表 = 视图模型项.GroupBy(视图模型 => 获取分组键(视图模型.代码, 前缀));

        目标.替换全部(分组列表.Select(group =>
        {
            var 有序 = group.ToList();
            var 首个 = 有序.First().代码;
            var 最后 = 有序.Last().代码;
            return new IO分组视图模型($"{首个} — {最后}", 有序);
        }));
    }

    /// <summary>
    /// 筛选规则：「仅显示已定义」为硬条件；关键字按编号或含义模糊匹配（不区分大小写）。
    /// </summary>
    private bool 匹配筛选(IO点位视图模型 视图模型, string 查询)
    {
        if (显示已定义 && !视图模型.是否已定义)
        {
            return false;
        }

        if (查询.Length == 0)
        {
            return true;
        }

        return 视图模型.代码.Contains(查询, StringComparison.OrdinalIgnoreCase)
               || 视图模型.含义.Contains(查询, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 取八进制分组键：由低位数字之外的部分决定。
    /// <para>X0→"0"、X7→"0"、X10→"1"、X17→"1"、X20→"2"。</para>
    /// </summary>
    private static string 获取分组键(string 代码值, string 前缀)
    {
        var 数字列表 = 代码值.Substring(前缀.Length);
        return 数字列表.Length <= 1 ? "0" : 数字列表[..^1];
    }

    // ── 状态更新 ───────────────────────────────────────────────────────

    private void 状态已变更(object? 发送者, IO点位状态 状态值)
    {
        在界面线程执行(() =>
        {
            if (_序号.TryGetValue(状态值.代码, out var 视图模型))
            {
                视图模型.是否开启 = 状态值.是否开启;
            }

            请求刷新摘要();
        });
    }

    /// <summary>
    /// 摘要合并刷新：一个采集周期内可能几十个点同时翻转，
    /// 每点都全量重算摘要就是 O(事件数 × 点数)。
    /// 用脏标记把同一批事件合并成一次重算——入队顺序保证摘要回调
    /// 排在所有已排队的点位更新之后，界面最终状态与逐次刷新一致。
    /// </summary>
    private void 请求刷新摘要()
    {
        if (_摘要待刷新)
        {
            return;
        }

        _摘要待刷新 = true;

        在界面线程执行(() =>
        {
            _摘要待刷新 = false;
            刷新摘要();
        });
    }

    private void 刷新摘要()
    {
        var 输入 = _IO点位表.表格.输入;
        var 输出 = _IO点位表.表格.输出;

        var 输入为开 = 输入.Count(点位项 => _IO点位表.获取状态(点位项.代码).是否开启);
        var 输出为开 = 输出.Count(点位项 => _IO点位表.获取状态(点位项.代码).是否开启);

        输入摘要 = $"共 {输入.Count} 点 · {输入.Count(点位项 => 点位项.启用)} 点启用 · {输入为开} 点 ON";
        输出摘要 = $"共 {输出.Count} 点 · {输出.Count(点位项 => 点位项.启用)} 点启用 · {输出为开} 点 ON";
    }
}
