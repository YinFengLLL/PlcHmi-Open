using System.IO;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using 上位机.应用.服务;
using 上位机.应用.视图;
using 上位机.应用.视图模型;
using 上位机.业务;
using 上位机.业务.配置;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;
using Serilog;
using Serilog.Events;

namespace 上位机.应用;

/// <summary>
/// 应用程序入口。
/// <para>启动流程：加载 config → 初始化 Serilog → 构建泛型主机（DI）→ 启动通信与采集 →
/// 从容器取出 <see cref="主窗口"/> 显示。</para>
/// </summary>
public partial class 应用 : Application
{
    private IHost? _主机;

    /// <summary>单实例互斥体。为 <c>null</c> 表示本次是诊断模式，未参与互斥。</summary>
    private Mutex? _单实例锁;

    /// <summary>运行期错误收集处（与异常处理器共用同一实例）。</summary>
    private 运行期错误服务? _运行期错误列表;

    /// <summary>诊断模式：不弹登录、不进单实例互斥，跑完自检即退出。</summary>
    private static bool 是否诊断模式(string[] 参数) =>
        有参数(参数, "--smoke") || 有参数(参数, "--shots") || 有参数(参数, "--export-csv");

    /// <summary>
    /// 尝试取得单实例所有权。返回 <c>false</c> 表示已有实例在运行。
    /// </summary>
    private bool 尝试获取单个实例()
    {
        // Local\ 前缀：按登录会话划分，允许多用户/远程桌面各跑一个
        _单实例锁 = new Mutex(initiallyOwned: true, @"Local\PlcHmi.App.SingleInstance", out var 已创建);

        if (已创建)
        {
            return true;
        }

        _单实例锁.Dispose();
        _单实例锁 = null;
        return false;
    }

    /// <inheritdoc />
    protected override async void OnStartup(StartupEventArgs 事件参数)
    {
        base.OnStartup(事件参数);

        // 全局异常处理器（界面线程 / 应用程序域 / 后台任务）：记日志 + 顶栏常驻横幅，
        // 不弹模态框（无人值守机台上模态框会阻断界面，变成"报警在响、界面不动"）。见 异常处理器安装器。
        var 错误列表 = new 运行期错误服务();
        _运行期错误列表 = 错误列表;
        异常处理器安装器.安装(错误列表);

        // ── 0.4 目录准备 + 出厂配置补齐 + 可写性预检（顺序与理由见 启动准备.执行） ──
        var (补齐的配置, 写入问题, 升级说明) = 启动准备.执行();

        // ── 1. 读取配置（失败也能启动，回退默认值） ──
        var 设置 = 应用设置加载器.加载(out var 配置错误);
        var 配方配置 = 配方配置加载器.加载(out var 配方错误);
        var 变量表 = 变量表加载器.加载(out var 变量错误);

        // 变量绑定回填：recipe-config.json 中绑定了变量的参数项 / 点动档位 / 控制地址，从变量表取
        // 软元件地址回填（下游按地址工作；OPC UA 模式再按变量名符号寻址）。失败保留原地址并记健康警告（R3）。
        var 变量绑定警告 = 变量绑定回填.应用配方配置(
            配方配置, 变量表, new 上位机.通信.汇川.汇川地址解析器(设置.PLC连接.系列));

        // ── 2. 初始化日志 ──
        Directory.CreateDirectory(配置路径.日志目录);
        var 等级 = Enum.TryParse<LogEventLevel>(设置.日志.最小等级, true, out var 已解析)
            ? 已解析
            : LogEventLevel.Information;

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(等级)
            .WriteTo.File(
                Path.Combine(配置路径.日志目录, "plchmi-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("════ 工控上位机启动 ════");

        if (补齐的配置.Count > 0)
        {
            Log.Information("已从出厂配置补齐 {Count} 个缺失的配置文件：{Names}（已有文件一律不覆盖）",
                补齐的配置.Count, string.Join("、", 补齐的配置));
        }
        if (升级说明 is not null) Log.Information("配置升级：{Summary}", 升级说明);   // 老配置升级已在启动准备里落盘

        // ── 2.5 单实例保护（放在日志初始化之后，拒绝才有据可查）──
        //    两个实例会各自持有一份 RecipeStore 内存副本，同写 config/recipes.json ——
        //    正是本项目为了消掉"进程内双写"才抽出 RecipeStore 单例的那个陷阱，
        //    在进程级还开着。触发场景很现实：操作员双击两次桌面快捷方式。
        //    诊断模式（--smoke / --shots / --export-csv）刻意跳过：它们要能在
        //    开发机开着界面的同时跑门禁。
        if (!是否诊断模式(事件参数.Args) && !尝试获取单个实例())
        {
            Log.Warning("已有实例在运行，本次启动被拒绝（避免两个实例同写配置文件）");

            MessageBox.Show(
                "工控上位机已在运行中。\n\n"
                + "同时开两个实例会各自写入同一份配置（机种配方、报警历史），可能互相覆盖。\n"
                + "请切换到已打开的窗口；若确认没有窗口，请在任务管理器中结束「点胶锁付机」程序后重试。",
                "已在运行", MessageBoxButton.OK, MessageBoxImage.Warning);

            Log.CloseAndFlush();
            Shutdown(2);
            return;
        }

        if (配置错误 is not null)
        {
            Log.Warning("{ConfigError}", 配置错误);
        }

        if (配方错误 is not null)
        {
            Log.Warning("{RecipeConfigError}", 配方错误);
        }

        // ── 3. 构建主机 ──
        var 构建器 = Host.CreateApplicationBuilder();

        构建器.Services.AddSerilog(Log.Logger, dispose: true);
        构建器.Services.AddSingleton(设置.界面);
        构建器.Services.AddSingleton(设置.日志);

        // 四轴定义按接口注册，便于参数页直接注入
        IReadOnlyList<轴定义> 轴集合 = 设置.轴列表;
        构建器.Services.AddSingleton(轴集合);

        // 通信层 + 业务层
        构建器.Services.添加上位机服务(设置.PLC连接, 设置.轮询, 配方配置, 变量表);

        // 界面层
        构建器.Services.添加上位机界面();

        // 用异常处理器持有的同一个实例覆盖默认注册：
        // 否则横幅订阅的是 A 实例、异常写进 B 实例，界面永远不显示。
        构建器.Services.AddSingleton<运行期错误接收器接口>(错误列表);

        _主机 = 构建器.Build();

        // ── 4.5 启动期配置回退也要登记 ──
        //    AppSettingsLoader / RecipeConfigLoader 在主机建立之前就要出结果（DI 需要它们），
        //    因此拿不到 IConfigHealth。这里补登记，保证"回退默认值"同样出现在降级横幅上——
        //    静默用默认 IP / 默认地址规划连 PLC，比连不上更难排查。
        var 健康 = _主机.Services.GetRequiredService<配置健康接口>();

        if (配置错误 is not null)
        {
            健康.上报回退("连接与轮询配置", 配置错误);
        }

        if (配方错误 is not null)
        {
            健康.上报回退("机种配置（地址规划）", 配方错误);
        }

        if (变量错误 is not null)
        {
            健康.上报回退("汇川变量表", 变量错误);
        }

        // ── 4.6 Modbus 运行模式启动策略 ──
        //    在 Modbus 模式下，运行模式由管理员在运行期切换，且"不跨重启记忆"：这里把配置文件里的
        //    模拟开关强制归零，保证每次启动一律先按真实 PLC 装配。即使上次调试时切到了模拟模式、
        //    或配置里残留 使用模拟器=true，生产机台重启后也不会悄悄停在一个"看着在跑、其实没连设备"
        //    的状态。要进模拟模式，必须由管理员在「系统设置」页显式切换。
        if (运行模式策略.可运行期切换(设置.PLC连接.通讯协议) && 设置.PLC连接.使用模拟器)
        {
            Log.Warning("配置中的 使用模拟器=true 在 {Protocol} 模式下被忽略：每次启动一律进入真实 PLC 模式，"
                + "模拟模式请在「系统设置」页运行期切换", 设置.PLC连接.通讯协议);

            设置.PLC连接.使用模拟器 = false;
        }

        foreach (var 警告 in 变量绑定警告)
        {
            健康.上报回退("变量绑定回填", 警告);
        }

        foreach (var 问题 in 写入问题)
        {
            健康.上报回退("目录权限", 问题);
        }

        // ── 4. 先把运行时服务实例化并应用配置，再启动采集 ──
        //    DeviceService 等在构造时订阅事件，早于轮询启动可以第一时间拿到状态变化。
        _主机.Services.GetRequiredService<设备服务接口>().目标数量 = 设置.界面.目标数量;

        try
        {
            await _主机.StartAsync().ConfigureAwait(true);
        }
        catch (Exception 异常)
        {
            Log.Fatal(异常, "主机启动失败");
        }

        // ── 5. 显示主窗口 ──
        var 窗口 = _主机.Services.GetRequiredService<主窗口>();
        MainWindow = 窗口;
        窗口.Show();

        Log.Information("主窗口已显示");

        // ── 5.5 启动登录 ──
        //    诊断模式跳过登录，否则自动化会被模态对话框卡住。
        //    取消登录不阻断使用：以操作员身份进入（只能看运行 / IO / 报警）。
        var 是否诊断 = 有参数(事件参数.Args, "--smoke") || 有参数(事件参数.Args, "--shots")
                           || 有参数(事件参数.Args, "--export-csv");

        if (是否诊断)
        {
            // 诊断模式以管理员身份运行，否则操作员权限下只能看到 3 个页面，
            // --smoke 就失去了"遍历全部页面"的意义。
            try
            {
                var 用户 = _主机.Services.GetRequiredService<用户服务接口>();

                if (!await 用户.异步登录("admin", 用户服务接口.默认密码).ConfigureAwait(true))
                {
                    Log.Warning("诊断模式未能以管理员身份登录（密码可能已修改），将只遍历当前可见页面");
                }
            }
            catch (Exception 异常)
            {
                Log.Warning(异常, "诊断模式登录失败");
            }
        }
        else
        {
            try
            {
                var 对话框集合 = _主机.Services.GetRequiredService<对话框服务接口>();

                if (对话框集合.显示登录())
                {
                    Log.Information("已以 {User} 身份登录",
                        _主机.Services.GetRequiredService<用户服务接口>().当前显示名称);
                }
                else
                {
                    Log.Information("未登录，以操作员身份进入");
                }
            }
            catch (Exception 异常)
            {
                Log.Warning(异常, "登录流程异常，以操作员身份继续");
            }
        }

        // ── 6. 诊断模式 ──
        //    --smoke               ：遍历全部页面校验视图映射，输出 PASS/FAIL 后退出
        //    --shots <目录>        ：把每个页面渲染为 PNG，用于生成文档截图
        //    --export-csv <目录>   ：把全部机种导出为 CSV，便于 Excel 批量编辑
        //
        // 先把 Dispatcher 队列里的事做完再往下走：
        // UserService.LoginAsync 的 UserChanged 是在线程池线程上触发的（PBKDF2 用 Task.Run 移出 UI 线程），
        // 所以 MainViewModel 重建导航是经 BeginInvoke 异步排队的。这里若不先让队列跑完，
        // 下面读到的 NavItems 仍是"未登录"的 3 项——--smoke 会以 3 页"全过"、--shots 只出 3 张图。
        await Dispatcher.Yield(DispatcherPriority.Loaded);

        if (有参数(事件参数.Args, "--smoke"))
        {
            await 异步运行冒烟测试(窗口).ConfigureAwait(true);
        }
        else if (尝试获取参数新值(事件参数.Args, "--shots", out var 截图目录))
        {
            await 异步捕获页面(窗口, 截图目录).ConfigureAwait(true);
        }
        else if (尝试获取参数新值(事件参数.Args, "--export-csv", out var CSV目录))
        {
            await 异步导出配方CSV(CSV目录).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 把全部机种导出为 CSV。命令行：<c>PlcHmi.App.exe --export-csv &lt;目录&gt;</c>
    /// </summary>
    private async Task 异步导出配方CSV(string 目录)
    {
        var 配方项 = _主机?.Services.GetRequiredService<配方服务接口>();

        if (配方项 is null)
        {
            Log.Error("导出失败：主机未就绪");
            Shutdown(1);
            return;
        }

        try
        {
            var 文件列表 = await 配方项.异步导出全部CSV到目录(目录).ConfigureAwait(true);

            Log.Information("已导出 {Count} 个机种的 CSV 到 {Dir}", 文件列表.Count, 目录);

            foreach (var file in 文件列表)
            {
                Log.Information("  {File}", file);
            }

            Shutdown(0);
        }
        catch (Exception 异常)
        {
            Log.Error(异常, "导出机种 CSV 失败");
            Shutdown(1);
        }
    }

    private static bool 有参数(string[] 参数, string 名称值) =>
        参数.Any(甲 => string.Equals(甲, 名称值, StringComparison.OrdinalIgnoreCase));

    private static bool 尝试获取参数新值(string[] 参数, string 名称值, out string 值)
    {
        for (var 条目 = 0; 条目 < 参数.Length - 1; 条目++)
        {
            if (string.Equals(参数[条目], 名称值, StringComparison.OrdinalIgnoreCase))
            {
                值 = 参数[条目 + 1];
                return true;
            }
        }

        值 = string.Empty;
        return false;
    }

    /// <summary>
    /// 把每个导航页渲染成 PNG。用 WPF 自身的 <see cref="RenderTargetBitmap"/> 离屏渲染，
    /// 不受窗口遮挡影响，也不需要人工截图。
    /// </summary>
    private async Task 异步捕获页面(Window 窗口, string 目录)
    {
        Directory.CreateDirectory(目录);

        if (窗口.DataContext is not 主视图模型 主)
        {
            Log.Error("截图失败：主窗口 DataContext 不是 MainViewModel");
            Shutdown(1);
            return;
        }

        if (窗口.Content is not FrameworkElement 根)
        {
            Log.Error("截图失败：主窗口内容不是 FrameworkElement");
            Shutdown(1);
            return;
        }

        var 序号 = 0;
        foreach (var 条目 in 主.导航条目集合.ToList())
        {
            主.选中导航条目 = 条目;

            await Dispatcher.Yield(DispatcherPriority.Loaded);
            窗口.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.Render);

            // 留一点时间让采集回调把实时值填进界面
            await Task.Delay(400);

            var 宽度 = (int)Math.Ceiling(根.ActualWidth);
            var 高度 = (int)Math.Ceiling(根.ActualHeight);

            if (宽度 <= 0 || 高度 <= 0)
            {
                Log.Warning("页面「{Title}」尺寸无效，跳过截图", 条目.标题);
                continue;
            }

            var 位图 = new RenderTargetBitmap(宽度, 高度, 96, 96, PixelFormats.Pbgra32);
            位图.Render(根);

            var 编码器 = new PngBitmapEncoder();
            编码器.Frames.Add(BitmapFrame.Create(位图));

            var file = Path.Combine(目录, $"{++序号:00}-{合法文件名(条目.标题)}.png");
            await using (var 流 = File.Create(file))
            {
                编码器.Save(流);
            }

            Log.Information("已保存截图：{File}（{Width}×{Height}）", file, 宽度, 高度);
        }

        Log.Information("截图完成，共 {Count} 张 → {Dir}", 序号, 目录);

        // 页数不足说明导航被权限或时序问题截断；退出码非 0，截图才具备门禁价值
        var 完成 = 序号 == 期望视图.Count;

        if (!完成)
        {
            Log.Error("截图数量异常：应有 {Expected} 张，实际 {Actual} 张", 期望视图.Count, 序号);
        }

        Shutdown(完成 ? 0 : 1);
    }

    /// <summary>
    /// 冒烟自检：依次切换每个导航页，强制布局，从视觉树里确认内容区真的实例化了对应视图。
    /// <para>命令：<c>PlcHmi.App.exe --smoke</c>。全部通过时退出码为 0，否则为失败页数。</para>
    /// </summary>
    private async Task 异步运行冒烟测试(Window 窗口)
    {
        Log.Information("═════ 页面自检开始 ═════");

        if (窗口.DataContext is not 主视图模型 主)
        {
            Log.Error("自检失败：主窗口 DataContext 不是 MainViewModel");
            Shutdown(1);
            return;
        }

        var 失败 = 0;
        var 条目列表 = 主.导航条目集合.ToList();
        Log.Information("导航项共 {Count} 个：{Titles}", 条目列表.Count,
            string.Join(" / ", 条目列表.Select(条目 => 条目.标题)));

        // 页数必须与期望一致：否则"逐页比对"会退化成"只遍历看得见的几页然后宣布全过"。
        // 这个盲区确实发生过——管理员登录的回调还没跑完时，3 页全部"通过"。
        if (条目列表.Count != 期望视图.Count)
        {
            失败++;
            Log.Error("自检失败：导航应有 {Expected} 项，实际只有 {Actual} 项",
                期望视图.Count, 条目列表.Count);
        }

        foreach (var 条目 in 条目列表)
        {
            主.选中导航条目 = 条目;

            // 让绑定与布局完成，再检查视觉树
            await Dispatcher.Yield(DispatcherPriority.Loaded);
            窗口.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.Render);

            var 实际 = 查找首个用户控件(((主窗口)窗口).内容宿主); // 从内容宿主起查：壳层里的 UserControl（辉光状态灯等）不再被误认为页面
            期望视图.TryGetValue(条目.标题, out var 期望);

            var 问题详情 = $"页面「{条目.标题}」→ 期望 {期望}，实际 {实际 ?? "(无)"}";
            if (实际 == 期望)
            {
                Log.Information("自检通过：{Detail}", 问题详情);
            }
            else
            {
                失败++;
                Log.Error("自检失败：{Detail}", 问题详情);
            }
        }

        Log.Information("═════ 页面自检结束：{Total} 页，失败 {Failed} 页 ═════", 条目列表.Count, 失败);

        失败 += 验证对话框();
        失败 += await 异步验证交互();

        Log.Information("═════ 自检总计：失败 {Failed} 项 ═════", 失败);
        Shutdown(失败);
    }

    /// <summary>在视觉树中寻找第一个 <see cref="UserControl"/>（即内容区实际挂载的页面视图）。</summary>
    private static string? 查找首个用户控件(DependencyObject 根)
    {
        var 数量值 = VisualTreeHelper.GetChildrenCount(根);

        for (var 条目 = 0; 条目 < 数量值; 条目++)
        {
            var 子 = VisualTreeHelper.GetChild(根, 条目);

            if (子 is UserControl 控件)
            {
                return 控件.GetType().Name;
            }

            var 嵌套 = 查找首个用户控件(子);
            if (嵌套 is not null)
            {
                return 嵌套;
            }
        }

        return null;
    }
}
