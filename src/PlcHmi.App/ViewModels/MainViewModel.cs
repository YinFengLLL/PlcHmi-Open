using System.Reflection;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using 上位机.应用.服务;
using 上位机.业务.服务;
using 上位机.业务.配置;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.应用.视图模型;

/// <summary>导航项。</summary>
public sealed class 导航条目
{
    public 导航条目(string 标题文本, string 图标键值, 视图模型基类 页面项)
    {
        标题 = 标题文本;
        图标键 = 图标键值;
        页面 = 页面项;
    }

    /// <summary>导航标题。</summary>
    public string 标题 { get; }

    /// <summary>导航图标资源键（Images/PlcHmi.Icons.xaml 中的 Geometry）。</summary>
    public string 图标键 { get; }

    /// <summary>目标页面。</summary>
    public 视图模型基类 页面 { get; }
}

/// <summary>
/// 主窗口 ViewModel。负责导航、连接状态、时间与底部报警条。
/// </summary>
public sealed partial class 主视图模型 : ObservableObject
{
    /// <summary>
    /// 导航项 → 访问所需权限。
    /// <para>操作员只有运行监控 + IO/报警（只读）；工程师与管理员可见全部。</para>
    /// </summary>
    private static readonly Dictionary<string, 权限> 导航权限 = new()
    {
        ["运行监控"] = 权限.视图运行页面,
        ["参数设置"] = 权限.视图全部页面,
        ["锁附示教"] = 权限.视图全部页面,
        ["点胶示教"] = 权限.视图全部页面,
        ["标定 · 其它点"] = 权限.视图全部页面,
        ["机种管理"] = 权限.视图全部页面,
        ["手动调试"] = 权限.视图全部页面,
        ["IO 点位"] = 权限.视图监控页面,
        ["报警记录"] = 权限.视图监控页面,
        ["历史记录"] = 权限.视图监控页面,
        ["系统设置"] = 权限.视图全部页面,
    };

    /// <summary>
    /// 页面号映射（写给 PLC 的 D10）：<b>主监控 = 1</b>，按侧栏固定顺序递增。
    ///
    /// <para><b>为什么用固定表而不是集合下标：</b>导航集合会按身份过滤（操作员只看得到 4 项），
    /// 用下标会让"参数设置"在管理员眼里是 2、在操作员眼里是 1 —— 同一页面两个号码，
    /// PLC 侧的程序分支必然错乱。所以页码只认标题。</para>
    /// </summary>
    private static readonly Dictionary<string, int> 页面号映射 = new()
    {
        ["运行监控"] = 1,
        ["参数设置"] = 2,
        ["锁附示教"] = 3,
        ["点胶示教"] = 4,
        ["标定 · 其它点"] = 5,
        ["机种管理"] = 6,
        ["手动调试"] = 7,
        ["IO 点位"] = 8,
        ["报警记录"] = 9,
        ["历史记录"] = 10,
        ["系统设置"] = 11,
    };

    private readonly IServiceProvider _服务;
    private readonly 连接管理器接口 _连接;
    private readonly 报警服务接口 _报警;
    private readonly 用户服务接口 _用户项;
    private readonly 配置健康接口 _配置健康;
    private readonly 运行期错误接收器接口 _运行期错误列表;
    private readonly 对话框服务接口 _对话框;
    private readonly PLC连接选项 _PLC选项;
    private readonly 运行模式管理器接口? _运行模式;
    private readonly 页面上下文服务接口 _页面上下文;
    private readonly ILogger<主视图模型> _日志;
    private readonly DispatcherTimer _时钟;

    private 连接状态 _上次连接状态 = 连接状态.已断开;  // 只在「已连接 → 非已连接」时触发失效全部()

    /// <summary>弹窗宿主（M 触发线圈 → 主窗居中弹窗，定义见 config/popups.json）。</summary>
    public 弹窗宿主视图模型 弹窗宿主 { get; }

    [ObservableProperty] private 导航条目? _选中导航条目;
    [ObservableProperty] private 视图模型基类? _当前页面;
    [ObservableProperty] private string _连接状态文本 = "未连接";
    [ObservableProperty] private string _连接状态颜色键 = "MutedBrush";
    [ObservableProperty] private string _当前时间文本 = string.Empty;
    [ObservableProperty] private string _报警栏文本 = "当前无报警";
    [ObservableProperty] private string _报警栏颜色键 = "SuccessBrush";
    [ObservableProperty] private bool _有活动报警;

    /// <summary>顶栏副标题（连接目标 + 运行模式），随运行模式切换刷新。</summary>
    [ObservableProperty] private string _工位副标题 = string.Empty;

    /// <summary>是否处于模拟 PLC 模式（顶栏常驻横幅依据）。</summary>
    [ObservableProperty] private bool _是模拟模式;

    /// <summary>模拟模式横幅文案。</summary>
    [ObservableProperty] private string _模拟模式文本 = string.Empty;

    [ObservableProperty] private string _用户显示文本 = "操作员（未登录）";
    [ObservableProperty] private string _用户角色文本 = "操作员";

    /// <summary>是否存在配置降级（横幅是否显示）。</summary>
    [ObservableProperty] private bool _有配置问题;

    /// <summary>配置降级横幅文案。</summary>
    [ObservableProperty] private string _配置问题文本 = string.Empty;

    /// <summary>横幅配色资源键：数据不可用=红，已恢复=橙。</summary>
    [ObservableProperty] private string _配置问题颜色键 = "DangerBrush";

    /// <summary>是否存在运行期异常（第二条横幅）。</summary>
    [ObservableProperty] private bool _有运行期错误;

    /// <summary>运行期异常横幅文案。</summary>
    [ObservableProperty] private string _运行期错误文本 = string.Empty;

    /// <summary>运行期异常横幅配色资源键：真缺陷=红，PLC 未连接=橙（现场常态，不该喊红）。</summary>
    [ObservableProperty] private string _运行期错误颜色键 = "DangerBrush";

    /// <summary>运行期异常横幅底色资源键。</summary>
    [ObservableProperty] private string _运行期错误底色键 = "DangerSoftBrush";

    public 主视图模型(
        IServiceProvider 服务集合,
        连接管理器接口 连接,
        报警服务接口 报警列表,
        用户服务接口 用户,
        配置健康接口 配置健康,
        运行期错误接收器接口 运行期错误列表,
        对话框服务接口 对话框集合,
        界面选项 界面选项,
        PLC连接选项 PLC选项,
        ILogger<主视图模型> 日志,
        运行模式管理器接口? 运行模式 = null)
    {
        _服务 = 服务集合;
        _连接 = 连接;
        _报警 = 报警列表;
        _用户项 = 用户;
        _配置健康 = 配置健康;
        _运行期错误列表 = 运行期错误列表;
        _对话框 = 对话框集合;
        _PLC选项 = PLC选项;
        _运行模式 = 运行模式;
        _日志 = 日志;

        工位名称 = string.IsNullOrWhiteSpace(界面选项.工位名称) ? "工控上位机" : 界面选项.工位名称;

        构建导航();
        刷新用户显示();
        刷新运行模式();

        _连接.状态已变更 += (_, 状态值) => 界面派发.投递(() => 应用连接状态(状态值));

        // 运行模式切换（真实 ↔ 模拟）由系统设置页发起：顶栏副标题与模拟横幅随之刷新
        if (_运行模式 is not null)
        {
            _运行模式.模式已变更 += (_, _) => 界面派发.投递(刷新运行模式);
        }

        _报警.报警已触发 += (_, _) => 界面派发.投递(刷新报警栏);
        _报警.报警已清除 += (_, _) => 界面派发.投递(刷新报警栏);

        // 身份变化 → 重算可见导航并刷新顶栏
        _用户项.用户已变更 += (_, _) => 界面派发.投递(() =>
        {
            构建导航();
            刷新用户显示();
        });

        // 配置健康变化 → 刷新降级横幅（加载器可能在后台线程登记）
        _配置健康.已变更 += (_, _) => 界面派发.投递(刷新配置健康);

        // 运行期异常 → 刷新错误横幅（异常可能来自任意线程）
        _运行期错误列表.已变更 += (_, _) => 界面派发.投递(刷新运行期错误);

        // 连接状态变化 → 设备类提示（"PLC 未连接，数据未刷新"）该出现/该消失都由它定
        _连接.状态已变更 += (_, _) => 界面派发.投递(刷新运行期错误);

        // 可配置弹窗（M 触发线圈 → 主窗居中弹窗，定义见 config/popups.json）
        弹窗宿主 = _服务.GetRequiredService<弹窗宿主视图模型>();

        // 页面号/点码同步（D10 / D651·D652）：PLC 侧据此做程序分支
        _页面上下文 = _服务.GetRequiredService<页面上下文服务接口>();

        _时钟 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _时钟.Tick += (_, _) =>
        {
            当前时间文本 = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            // 顺带把当前页需要周期回读的 PLC 状态跟上（动作按钮 / 回原状态灯，见 MainViewModel.Buttons.cs）
            异步回读当前页状态();
        };
        _时钟.Start();

        当前时间文本 = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        应用连接状态(_连接.状态);
        刷新报警栏();
        刷新配置健康();
        刷新运行期错误();

        if (导航条目集合.Count > 0)
        {
            选中导航条目 = 导航条目集合[0];
        }
    }

    /// <summary>站点/项目名称，显示在顶栏。</summary>
    public string 工位名称 { get; }

    /// <summary>侧栏版本号（取程序集版本，避免与 csproj 里写死的版本号各说各话）。</summary>
    public string 版本号 { get; } = 格式化版本号(Assembly.GetExecutingAssembly().GetName().Version);

    private static string 格式化版本号(Version? 版本) =>
        $"v{版本?.Major ?? 0}.{版本?.Minor ?? 0}.{版本?.Build ?? 0} · 点胶锁付机";

    /// <summary>导航项集合。</summary>
    public 批量可观察集合<导航条目> 导航条目集合 { get; } = new();

    partial void On选中导航条目Changed(导航条目? value)
    {
        if (value is null)
        {
            return;
        }

        当前页面 = value.页面;

        // 页面号同步给 PLC（D0）：主监控 = 1，按侧栏固定顺序递增。
        // 与页面初始化并行发出——页码只是上下文提示，不该等页面加载完才写。
        if (页面号映射.TryGetValue(value.标题, out var 页码))
        {
            异步安全调用.即发即忘(
                () => _页面上下文.异步写入页码(页码),
                _日志,
                $"写入页面号（{value.标题} = {页码}）");
        }

        异步安全调用.即发即忘(
            () => value.页面.异步初始化(),
            _日志,
            $"初始化页面「{value.标题}」");
    }

    /// <summary>
    /// 按当前身份重建导航。
    ///
    /// <para>页面 ViewModel 仍然全部构造（都是单例，构造一次即可），只是不显示在导航栏上——
    /// 这样切换身份时不需要重新解析 DI，也不会丢状态。</para>
    /// </summary>
    private void 构建导航()
    {
        var 上一个标题 = 选中导航条目?.标题;

        导航条目[] 全部 =
        [
            new("运行监控", "Icon.Nav.Run", _服务.GetRequiredService<运行视图模型>()),
            new("参数设置", "Icon.Nav.Param", _服务.GetRequiredService<参数视图模型>()),
            new("锁附示教", "Icon.Nav.Teach", _服务.GetRequiredService<锁附示教视图模型>()),
            new("点胶示教", "Icon.Nav.Recipe", _服务.GetRequiredService<点胶示教视图模型>()),
            new("标定 · 其它点", "Icon.Nav.Io", _服务.GetRequiredService<标定视图模型>()),
            new("机种管理", "Icon.Nav.Recipe", _服务.GetRequiredService<配方视图模型>()),
            new("手动调试", "Icon.Nav.Manual", _服务.GetRequiredService<手动视图模型>()),
            new("IO 点位", "Icon.Nav.Io", _服务.GetRequiredService<IO视图模型>()),
            new("报警记录", "Icon.Nav.Alarm", _服务.GetRequiredService<报警视图模型>()),
            new("历史记录", "Icon.Nav.History", _服务.GetRequiredService<历史记录视图模型>()),
            new("系统设置", "Icon.Nav.Settings", _服务.GetRequiredService<设置视图模型>()),
        ];

        导航条目集合.替换全部(全部.Where(条目 =>
            !导航权限.TryGetValue(条目.标题, out var required) || _用户项.有(required)));

        // 尽量停留在原来的页面；该页已不可见时回到第一页
        选中导航条目 = 导航条目集合.FirstOrDefault(导航项 => 导航项.标题 == 上一个标题)
                          ?? 导航条目集合.FirstOrDefault();
    }

    // ── 身份与权限 ──────────────────────────────────────────────────────

    /// <summary>弹出登录框切换身份。</summary>
    [RelayCommand]
    private void 切换用户()
    {
        if (!_对话框.显示登录())
        {
            return;
        }

        _日志.LogInformation("身份已切换：{User}", _用户项.当前显示名称);

        if (_用户项.是否使用默认密码)
        {
            // 不在提示里复述初始密码本身——密码值不应出现在任何界面上
            _对话框.提示("安全提示",
                "当前账号仍在使用初始密码。\n\n"
                + "请在「系统设置」页尽快修改，避免无关人员以管理员身份操作系统。");
        }
    }

    /// <summary>
    /// 打开报警记录页。底栏报警条可点击直达——报警出现后操作员的下一跳应该是一键可达的。
    /// </summary>
    [RelayCommand]
    private void 打开报警()
    {
        var 报警列表 = 导航条目集合.FirstOrDefault(导航项 => 导航项.标题 == "报警记录");

        if (报警列表 is not null)
        {
            选中导航条目 = 报警列表;
        }
    }

    /// <summary>退出登录，回落到操作员身份。</summary>
    [RelayCommand]
    private void 退出登录用户()
    {
        if (!_用户项.是否已登录)
        {
            return;
        }

        _用户项.退出登录();
        _日志.LogInformation("已退出登录，回落到操作员身份");
    }

    private void 刷新用户显示()
    {
        用户角色文本 = 角色权限.显示名称(_用户项.当前角色);

        用户显示文本 = _用户项.是否已登录
            ? $"{_用户项.当前显示名称}（{用户角色文本}）"
            : "操作员（未登录）";
    }

    /// <summary>
    /// 刷新顶栏副标题与模拟模式横幅。
    /// <para>模拟模式必须在界面上"一眼可见"——否则操作员会以为设备在跑，
    /// 实际却在跟一台虚拟 PLC 说话。</para>
    /// </summary>
    private void 刷新运行模式()
    {
        var 模式 = _运行模式?.当前模式
                   ?? (_PLC选项.使用模拟器 ? 运行模式.模拟PLC : 运行模式.真实PLC);

        工位副标题 = $"{_PLC选项.主机}:{_PLC选项.端口} · " +
                     (模式 == 运行模式.模拟PLC ? "模拟模式（虚拟 PLC）" : "真实 PLC");

        是模拟模式 = 模式 == 运行模式.模拟PLC;

        模拟模式文本 = 是模拟模式
            ? "当前为模拟 PLC 模式：未连接真实设备，仅用于离线调试；下方数据均来自内置虚拟 PLC。"
              + (_运行模式 is not null && _运行模式.模拟从站是否运行
                  ? $"　Modbus 调试从站：{_运行模式.模拟从站端点文本}。"
                  : string.Empty)
            : string.Empty;
    }

    private void 应用连接状态(连接状态 状态值)
    {
        // 断线后界面不该继续显示旧值；本方法已在 UI 线程（界面派发.投递），事件订阅者也在 UI 线程。
        if (_上次连接状态 is 连接状态.已连接 && 状态值 is not 连接状态.已连接)
        {
            _服务.GetRequiredService<标签值服务>().失效全部();
        }

        _上次连接状态 = 状态值;

        (连接状态文本, 连接状态颜色键) = 状态值 switch
        {
            连接状态.已连接 => ("已连接", "SuccessBrush"),
            连接状态.连接中 => ("连接中", "WarningBrush"),
            连接状态.重连中 => ("重连中", "WarningBrush"),
            连接状态.故障 => ("连接故障", "DangerBrush"),
            _ => ("未连接", "MutedBrush"),
        };
    }

    // 三条横幅（配置降级 / 运行期异常 / 活动报警）的刷新见 MainViewModel.Banners.cs
}
