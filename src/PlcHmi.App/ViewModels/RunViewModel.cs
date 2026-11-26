using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using 上位机.应用.服务;
using 上位机.应用.视图;
using 上位机.业务.服务;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.应用.视图模型;

/// <summary>主运行页 ViewModel。设备状态、节拍、产量与启停控制。</summary>
public sealed partial class 运行视图模型 : 视图模型基类
{
    private readonly 设备服务接口 _设备;
    private readonly 报警服务接口 _报警;
    private readonly 对话框服务接口 _对话框;
    private readonly 运行监控地址服务接口 _运行地址;
    private readonly 用户服务接口 _用户项;
    private readonly 配方服务接口 _配方;
    private readonly ILogger<运行视图模型> _日志;

    private bool _图表待刷新;  // 4 个事件源同批只算一次
    private readonly DispatcherTimer _图表合批 = new() { Interval = TimeSpan.FromMilliseconds(150) };

    [ObservableProperty] private string _设备状态文本 = "未知";
    [ObservableProperty] private string _设备状态颜色键 = "MutedBrush";
    [ObservableProperty] private string _周期时间文本 = "--.---";
    [ObservableProperty] private string _输出数量文本 = "0";
    [ObservableProperty] private string _目标数量文本 = "0";
    [ObservableProperty] private string _良品数量文本 = "0";
    [ObservableProperty] private string _不良品数量文本 = "0";
    [ObservableProperty] private string _达成文本 = "0.0%";
    [ObservableProperty] private bool _可编辑地址;

    /// <summary>
    /// 是否显示"数据清除"按钮（工程师与管理员）。
    ///
    /// <para>清零累计数据是调试 / 交接班动作，操作员没有理由碰它；
    /// 沿用"改参数值"这一权限点：恰好工程师与管理员具备、操作员不具备，
    /// 不必为单个按钮新增权限位。</para>
    /// </summary>
    [ObservableProperty] private bool _可清除数据;

    // ── 数据分析组合视图（产量进度卡 + 四张图表翻页） ──────────────────
    private readonly 产量统计接口 _统计;

    [ObservableProperty] private int _当前图表索引;

    [ObservableProperty] private string _图表标题 = 图表标题列表[0];
    [ObservableProperty] private string _页码文本 = "1 / 4";
    [ObservableProperty] private bool _显示占比图 = true;
    [ObservableProperty] private bool _显示小时图;
    [ObservableProperty] private bool _显示周图;
    [ObservableProperty] private bool _显示对比图;

    [ObservableProperty] private double _占比良品值;
    [ObservableProperty] private double _占比不良品值;
    [ObservableProperty] private int _当前小时索引;

    [ObservableProperty] private IReadOnlyList<double> _今日小时集合 = 全零序列(24);
    [ObservableProperty] private IReadOnlyList<double> _周产量集合 = 全零序列(7);
    [ObservableProperty] private IReadOnlyList<double> _对比今日集合 = 全零序列(24);
    [ObservableProperty] private IReadOnlyList<double> _对比昨日集合 = 全零序列(24);
    [ObservableProperty] private IReadOnlyList<string> _周标签集合 = 空标签(7);
    [ObservableProperty] private IReadOnlyList<折线系列> _周产量系列 = [];
    [ObservableProperty] private IReadOnlyList<折线系列> _对比系列 = [];

    /// <summary>横轴小时标签（0–23），三张图共用。</summary>
    public IReadOnlyList<string> 小时标签集合 { get; } =
        Enumerable.Range(0, 24).Select(小时 => $"{小时}").ToList();

    private static readonly string[] 图表标题列表 =
    [
        "良品 / 不良品占比",
        "今日每小时产量",
        "近 7 天每日产量",
        "今日与昨日同小时对比",
    ];

    /// <summary>达成率（0–100），仅供进度条使用；精确读数看 <see cref="达成文本"/>。</summary>
    [ObservableProperty] private double _达成率;

    /// <summary>
    /// 当前机种（显示为「机种 03 · 锁附 A」）。
    /// <para>现场最怕"跑错机种"——机种号显示在运行页第一眼能看到的位置。</para>
    /// </summary>
    [ObservableProperty] private string _当前机种文本 = "机种 --";

    public 运行视图模型(
        设备服务接口 设备,
        报警服务接口 报警列表,
        对话框服务接口 对话框集合,
        运行监控地址服务接口 运行地址,
        用户服务接口 用户项,
        产量统计接口 产量统计,
        配方服务接口 配方项,
        扫码枪服务接口 扫码枪,
        ILogger<运行视图模型> 日志)
    {
        _设备 = 设备;
        _报警 = 报警列表;
        _对话框 = 对话框集合;
        _运行地址 = 运行地址;
        _用户项 = 用户项;
        _统计 = 产量统计;
        _配方 = 配方项;
        _扫码枪 = 扫码枪;
        _日志 = 日志;

        _图表合批.Tick += (_, _) =>
        {
            _图表合批.Stop();
            _图表待刷新 = false;
            刷新图表();
        };

        标题 = "运行监控";
        副标题 = "设备实时状态与生产节拍";

        _设备.状态已变更 += (_, 状态值) => 在界面线程执行(() => 应用状态(状态值));
        _设备.周期时间已变更 += (_, 秒) => 在界面线程执行(() => 应用周期时间(秒));
        _设备.输出数量已变更 += (_, 数量值) => 在界面线程执行(() =>
        {
            应用输出数量(数量值);
            标记图表待刷新();
        });
        _设备.目标数量已变更 += (_, 数量值) => 在界面线程执行(() => 应用输出数量(_设备.输出数量));
        _设备.良品数量已变更 += (_, 数量值) => 在界面线程执行(() =>
        {
            应用良品数量(数量值);
            标记图表待刷新();
        });
        _设备.不良品数量已变更 += (_, 数量值) => 在界面线程执行(() =>
        {
            应用不良品数量(数量值);
            标记图表待刷新();
        });
        _报警.报警已触发 += (_, 报警项) => 在界面线程执行(刷新报警摘要);
        _报警.报警已清除 += (_, _) => 在界面线程执行(刷新报警摘要);
        _用户项.用户已变更 += (_, _) => 在界面线程执行(刷新权限);
        _统计.统计已变更 += (_, _) => 在界面线程执行(标记图表待刷新);

        // 工位状态条（锁附/点胶/加工完成 + 已锁附颗数）见 RunViewModel.Process.cs
        订阅加工状态();

        // 当前工单号（扫码结果）见 RunViewModel.Scanner.cs
        订阅扫码工单号();
        _配方.配方已变更 += (_, _) => 在界面线程执行(刷新机种文本);

        目标数量文本 = _设备.目标数量.ToString("N0");
        应用状态(_设备.状态);
        刷新报警摘要();
        刷新权限();
        刷新机种文本();
        刷新图表();
    }

    /// <inheritdoc />
    public override Task 异步初始化()
    {
        目标数量文本 = _设备.目标数量.ToString("N0");
        应用输出数量(_设备.输出数量);
        应用良品数量(_设备.良品数量);
        应用不良品数量(_设备.不良品数量);

        // 主动拉一次节拍快照。<b>不能只依赖事件</b>：设备服务仅在值发生变化时
        // 才触发 周期时间已变更，若启动时机器正以恒定节拍生产，事件永远不来，
        // 「当前节拍」会一直停在 --.---（审查标准 R2：订阅事件流的服务必须在初始拉一次快照）。
        应用周期时间(_设备.周期时间秒);
        应用加工初始快照();
        应用工单号(_扫码枪.工单号);
        刷新图表();
        return Task.CompletedTask;
    }

    // ── 数据分析组合视图 ────────────────────────────────────────────────

    /// <summary>上一张图（循环翻页）。</summary>
    [RelayCommand]
    private void 上一图表() => 当前图表索引 = (当前图表索引 + 3) % 4;

    /// <summary>下一张图（循环翻页）。</summary>
    [RelayCommand]
    private void 下一图表() => 当前图表索引 = (当前图表索引 + 1) % 4;

    partial void On当前图表索引Changed(int value)
    {
        页码文本 = $"{value + 1} / 4";
        图表标题 = 图表标题列表[value];
        显示占比图 = value == 0;
        显示小时图 = value == 1;
        显示周图 = value == 2;
        显示对比图 = value == 3;
    }

    /// <summary>4 个事件源合批：脏标记已在则不重复排队，计时器到期只重建一次图表。</summary>
    private void 标记图表待刷新()
    {
        if (_图表待刷新)
        {
            return;
        }

        _图表待刷新 = true;
        _图表合批.Stop();
        _图表合批.Start();
    }

    /// <summary>
    /// 从统计服务与设备服务拉取最新快照刷新全部图表。
    ///
    /// <para>每次都替换集合实例——图表控件的依赖属性靠引用变化触发重绘。</para>
    /// </summary>
    private void 刷新图表()
    {
        占比良品值 = _设备.良品数量;
        占比不良品值 = _设备.不良品数量;

        var 今日 = _统计.今日小时产量;
        今日小时集合 = 今日;
        对比今日集合 = 今日;   // 与昨日共用同一份今日数据
        对比昨日集合 = _统计.昨日小时产量;
        当前小时索引 = DateTime.Now.Hour;

        周产量集合 = _统计.周每日产量;
        周标签集合 = _统计.周日期标签;

        周产量系列 =
        [
            new 折线系列 { 名称 = "每日产量", 颜色键 = "PrimaryBrush", 数值 = 周产量集合 },
        ];

        对比系列 =
        [
            new 折线系列 { 名称 = "今日", 颜色键 = "PrimaryBrush", 数值 = 对比今日集合 },
            new 折线系列 { 名称 = "昨日", 颜色键 = "WarningBrush", 数值 = 对比昨日集合 },
        ];
    }

    private static double[] 全零序列(int 数量) => new double[数量];

    private static string[] 空标签(int 数量) =>
        Enumerable.Range(0, 数量).Select(_ => "--").ToArray();

    // ── 命令 ───────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task 异步启动()
    {
        if (!_对话框.确认("启动确认", "确认启动设备？请确认人员已撤离危险区域。"))
        {
            return;
        }

        await 异步执行命令("启动", () => _设备.异步启动());
    }

    [RelayCommand]
    private async Task 异步停止() => await 异步执行命令("停止", () => _设备.异步停止());

    [RelayCommand]
    private async Task 异步重置() => await 异步执行命令("复位", () => _设备.异步重置());

    /// <summary>
    /// 报警清除。
    ///
    /// <para>上位机<b>不提供急停按钮</b>——急停必须由物理按钮实现：软件卡死或断线时
    /// 按钮就失效了，不能作为安全回路的一环。现场按下急停后 PLC 会把状态字置为
    /// <c>4</c>，界面上的设备状态会显示为「报警中」。</para>
    /// </summary>
    [RelayCommand]
    private async Task 异步清空报警()
    {
        var 清除地址 = _运行地址.查找地址("CmdAlarmClear");

        if (!_对话框.确认("报警清除",
                $"确认向 PLC 下发报警清除命令（{清除地址}）？",
                视图.确认等级.警告, "下发清除",
                "清除后报警锁存位被复位，请先排除报警原因再清除。"))
        {
            return;
        }

        await 异步执行命令("报警清除", () => _设备.异步清空报警());
    }

    /// <summary>数据清除（复归型脉冲命令，由 PLC 清零产量等累计数据）。</summary>
    [RelayCommand]
    private async Task 异步清除数据()
    {
        var 清除地址 = _运行地址.查找地址("CmdDataClear");

        if (!_对话框.确认("数据清除",
                $"确认向 PLC 下发数据清除命令（{清除地址}）？",
                视图.确认等级.警告, "下发清除",
                "产量、良品 / 不良品等累计数据将被 PLC 清零，该操作不可撤销。"))
        {
            return;
        }

        await 异步执行命令("数据清除", () => _设备.异步清除数据());
    }

    /// <summary>编辑运行监控页元件地址（仅管理员）：对话框就地修改候选副本，服务层校验并保存。</summary>
    [RelayCommand]
    private async Task 异步编辑地址()
    {
        // 编辑走克隆副本，确认前不影响页面运行；取消则直接丢弃
        var 候选列表 = _运行地址.元件集合.Select(条目项 => 条目项.克隆()).ToList();

        if (!_对话框.编辑运行监控地址(候选列表))
        {
            return;
        }

        var 成功 = await 界面命令.异步执行(_日志, _对话框, "保存运行监控页地址",
            () => _运行地址.异步保存(候选列表)).ConfigureAwait(true);

        if (成功)
        {
            _对话框.提示("保存成功", "运行监控页元件地址已保存并立即生效。");
        }
    }

    private void 刷新权限()
    {
        可编辑地址 = _用户项.有(权限.编辑绑定地址);
        可清除数据 = _用户项.有(权限.编辑参数值列表);
    }

    /// <summary>刷新机种显示文本（切换机种、重命名后由 配方已变更 事件触发）。</summary>
    private void 刷新机种文本()
    {
        var 号 = _配方.当前配方号;
        var 名称 = _配方.当前配方名称;

        当前机种文本 = string.IsNullOrWhiteSpace(名称)
            ? $"机种 {号:00}"
            : $"机种 {号:00} · {名称}";
    }

    private async Task 异步执行命令(string 动作, Func<Task> 命令)
    {
        是否忙碌 = true;

        try
        {
            if (await 界面命令.异步执行(_日志, _对话框, 动作, 命令).ConfigureAwait(true))
            {
                _日志.LogInformation("已下发{Action}命令", 动作);
            }
        }
        finally
        {
            是否忙碌 = false;
        }
    }

    // ── 状态映射 ───────────────────────────────────────────────────────

    /// <summary>
    /// 把设备状态应用到主监控页 Hero 卡的显示文本与语义色键。
    /// 状态字约定见 <see cref="设备服务.映射状态字"/>：0=空闲中 1=运行中 2=待机中 3=复位中 4=报警中；
    /// 文案与色键的对应关系见 <see cref="设备服务.状态显示"/>（回归测试钉住）。
    /// </summary>
    private void 应用状态(设备状态 状态值)
    {
        (设备状态文本, 设备状态颜色键) = 设备服务.状态显示(状态值);
    }

    private void 应用良品数量(long 数量值) => 良品数量文本 = 数量值.ToString("N0");

    private void 应用不良品数量(long 数量值) => 不良品数量文本 = 数量值.ToString("N0");

    private void 应用周期时间(double 秒)
    {
        周期时间文本 = 秒 <= 0 ? "--.---" : 秒.ToString("0.000");
    }

    private void 应用输出数量(long 数量值)
    {
        输出数量文本 = 数量值.ToString("N0");
        目标数量文本 = _设备.目标数量.ToString("N0");

        var 速率 = _设备.目标数量 > 0 ? (double)数量值 / _设备.目标数量 * 100 : 0;
        达成文本 = $"{速率:0.0}%";

        // 进度条只认 0–100：产量被人工改到超过目标时，条不该冲出卡片。
        // 文字照实显示真实百分比 —— 数字要诚实，条只负责"进度感"。
        达成率 = Math.Clamp(速率, 0, 100);
    }

    // 报警展示（横幅 / 胶囊 / 自动消失）见 RunViewModel.Alarm.cs（partial）。
}
