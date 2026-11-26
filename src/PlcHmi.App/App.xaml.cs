using System.IO;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PlcHmi.App.Services;
using PlcHmi.App.Views;
using PlcHmi.App.ViewModels;
using PlcHmi.Business;
using PlcHmi.Business.Configuration;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;
using Serilog;
using Serilog.Events;

namespace PlcHmi.App;

/// <summary>
/// 应用程序入口。
/// <para>启动流程：加载 config → 初始化 Serilog → 构建泛型主机（DI）→ 启动通信与采集 →
/// 从容器取出 <see cref="MasterWindow"/> 显示。</para>
/// </summary>
public partial class App : Application
{
    private IHost? _host;

    /// <summary>单实例互斥体。为 <c>null</c> 表示本次是诊断模式，未参与互斥。</summary>
    private Mutex? _singleInstanceLock;

    /// <summary>运行期错误收集处（与异常处理器共用同一实例）。</summary>
    private RuntimeErrorService? _runtimeErrorList;

    /// <summary>诊断模式：不弹登录、不进单实例互斥，跑完自检即退出。</summary>
    private static bool IsDiagnosticMode(string[] Parameter) =>
        HasParameter(Parameter, "--smoke") || HasParameter(Parameter, "--shots") || HasParameter(Parameter, "--export-csv");

    /// <summary>
    /// 尝试取得单实例所有权。返回 <c>false</c> 表示已有实例在运行。
    /// </summary>
    private bool TryGetSingleRealCase()
    {
        // Local\ 前缀：按登录会话划分，允许多用户/远程桌面各跑一个
        _singleInstanceLock = new Mutex(initiallyOwned: true, @"Local\PlcHmi.App.SingleInstance", out var created);

        if (created)
        {
            return true;
        }

        _singleInstanceLock.Dispose();
        _singleInstanceLock = null;
        return false;
    }

    /// <inheritdoc />
    protected override async void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);

        // 全局异常处理器（界面线程 / 应用程序域 / 后台任务）：记日志 + 顶栏常驻横幅，
        // 不弹模态框（无人值守机台上模态框会阻断界面，变成"报警在响、界面不动"）。见 异常处理器安装器。
        var errorList = new RuntimeErrorService();
        _runtimeErrorList = errorList;
        ExceptionHandlerInstaller.SafeInstall(errorList);

        // ── 0.4 目录准备 + 出厂配置补齐 + 可写性预检（顺序与理由见 启动准备.执行） ──
        var (fillConfig, writeIssue, upgradeDescription) = StartupPreparation.Execute();

        // ── 1. 读取配置（失败也能启动，回退默认值） ──
        var Settings = AppSettingsLoader.Load(out var configError);
        var RecipeConfig = RecipeConfigLoader.Load(out var recipeError);
        var VariableTable = VariableTableLoader.Load(out var variableError);

        // 变量绑定回填：recipe-config.json 中绑定了变量的参数项 / 点动档位 / 控制地址，从变量表取
        // 软元件地址回填（下游按地址工作；OPC UA 模式再按变量名符号寻址）。失败保留原地址并记健康警告（R3）。
        var variableBindingWarning = VariableBindingBackfill.ApplyRecipeConfig(
            RecipeConfig, VariableTable, new PlcHmi.Communication.Inovance.InovanceAddressParser(Settings.PlcConnection.Series));

        // ── 2. 初始化日志 ──
        Directory.CreateDirectory(ConfigPath.LogDirectory);
        var Level = Enum.TryParse<LogEventLevel>(Settings.Log.MinLevel, true, out var resolved)
            ? resolved
            : LogEventLevel.Information;

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(Level)
            .WriteTo.File(
                Path.Combine(ConfigPath.LogDirectory, "plchmi-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("════ 工控上位机启动 ════");

        if (fillConfig.Count > 0)
        {
            Log.Information("已从出厂配置补齐 {Count} 个缺失的配置文件：{Names}（已有文件一律不覆盖）",
                fillConfig.Count, string.Join("、", fillConfig));
        }
        if (upgradeDescription is not null) Log.Information("配置升级：{Summary}", upgradeDescription);   // 老配置升级已在启动准备里落盘

        // ── 2.5 单实例保护（放在日志初始化之后，拒绝才有据可查）──
        //    两个实例会各自持有一份 RecipeStore 内存副本，同写 config/recipes.json ——
        //    正是本项目为了消掉"进程内双写"才抽出 RecipeStore 单例的那个陷阱，
        //    在进程级还开着。触发场景很现实：操作员双击两次桌面快捷方式。
        //    诊断模式（--smoke / --shots / --export-csv）刻意跳过：它们要能在
        //    开发机开着界面的同时跑门禁。
        if (!IsDiagnosticMode(eventArgs.Args) && !TryGetSingleRealCase())
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

        if (configError is not null)
        {
            Log.Warning("{ConfigError}", configError);
        }

        if (recipeError is not null)
        {
            Log.Warning("{RecipeConfigError}", recipeError);
        }

        // ── 3. 构建主机 ──
        var builder = Host.CreateApplicationBuilder();

        builder.Services.AddSerilog(Log.Logger, dispose: true);
        builder.Services.AddSingleton(Settings.Ui);
        builder.Services.AddSingleton(Settings.Log);

        // 四轴定义按接口注册，便于参数页直接注入
        IReadOnlyList<AxisDefinition> axisCollection = Settings.AxisList;
        builder.Services.AddSingleton(axisCollection);

        // 通信层 + 业务层
        builder.Services.AddPlcHmiService(Settings.PlcConnection, Settings.Polling, RecipeConfig, VariableTable);

        // 界面层
        builder.Services.AddPlcHmiUi();

        // 用异常处理器持有的同一个实例覆盖默认注册：
        // 否则横幅订阅的是 A 实例、异常写进 B 实例，界面永远不显示。
        builder.Services.AddSingleton<IRuntimeErrorReceiver>(errorList);

        _host = builder.Build();

        // ── 4.5 启动期配置回退也要登记 ──
        //    AppSettingsLoader / RecipeConfigLoader 在主机建立之前就要出结果（DI 需要它们），
        //    因此拿不到 IConfigHealth。这里补登记，保证"回退默认值"同样出现在降级横幅上——
        //    静默用默认 IP / 默认地址规划连 PLC，比连不上更难排查。
        var Health = _host.Services.GetRequiredService<IConfigHealth>();

        if (configError is not null)
        {
            Health.ReportFallback("连接与轮询配置", configError);
        }

        if (recipeError is not null)
        {
            Health.ReportFallback("机种配置（地址规划）", recipeError);
        }

        if (variableError is not null)
        {
            Health.ReportFallback("汇川变量表", variableError);
        }

        // ── 4.6 Modbus 运行模式启动策略 ──
        //    在 Modbus 模式下，运行模式由管理员在运行期切换，且"不跨重启记忆"：这里把配置文件里的
        //    模拟开关强制归零，保证每次启动一律先按真实 PLC 装配。即使上次调试时切到了模拟模式、
        //    或配置里残留 使用模拟器=true，生产机台重启后也不会悄悄停在一个"看着在跑、其实没连设备"
        //    的状态。要进模拟模式，必须由管理员在「系统设置」页显式切换。
        if (RunModeStrategy.RuntimeSwitchable(Settings.PlcConnection.CommunicationProtocol) && Settings.PlcConnection.UseSimulator)
        {
            Log.Warning("配置中的 使用模拟器=true 在 {Protocol} 模式下被忽略：每次启动一律进入真实 PLC 模式，"
                + "模拟模式请在「系统设置」页运行期切换", Settings.PlcConnection.CommunicationProtocol);

            Settings.PlcConnection.UseSimulator = false;
        }

        foreach (var Warning in variableBindingWarning)
        {
            Health.ReportFallback("变量绑定回填", Warning);
        }

        foreach (var Issue in writeIssue)
        {
            Health.ReportFallback("目录权限", Issue);
        }

        // ── 4. 先把运行时服务实例化并应用配置，再启动采集 ──
        //    DeviceService 等在构造时订阅事件，早于轮询启动可以第一时间拿到状态变化。
        _host.Services.GetRequiredService<IDeviceService>().TargetCount = Settings.Ui.TargetCount;

        try
        {
            await _host.StartAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Log.Fatal(exception, "主机启动失败");
        }

        // ── 5. 显示主窗口 ──
        var Window = _host.Services.GetRequiredService<MasterWindow>();
        MainWindow = Window;
        Window.Show();

        Log.Information("主窗口已显示");

        // ── 5.5 启动登录 ──
        //    诊断模式跳过登录，否则自动化会被模态对话框卡住。
        //    取消登录不阻断使用：以操作员身份进入（只能看运行 / IO / 报警）。
        var isDiagnostic = HasParameter(eventArgs.Args, "--smoke") || HasParameter(eventArgs.Args, "--shots")
                           || HasParameter(eventArgs.Args, "--export-csv");

        if (isDiagnostic)
        {
            // 诊断模式以管理员身份运行，否则操作员权限下只能看到 3 个页面，
            // --smoke 就失去了"遍历全部页面"的意义。
            try
            {
                var User = _host.Services.GetRequiredService<IUserService>();

                if (!await User.LoginAsync("admin", IUserService.DefaultPassword).ConfigureAwait(true))
                {
                    Log.Warning("诊断模式未能以管理员身份登录（密码可能已修改），将只遍历当前可见页面");
                }
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "诊断模式登录失败");
            }
        }
        else
        {
            try
            {
                var dialogCollection = _host.Services.GetRequiredService<IDialogService>();

                if (dialogCollection.ShowLogin())
                {
                    Log.Information("已以 {User} 身份登录",
                        _host.Services.GetRequiredService<IUserService>().CurrentDisplayName);
                }
                else
                {
                    Log.Information("未登录，以操作员身份进入");
                }
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "登录流程异常，以操作员身份继续");
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

        if (HasParameter(eventArgs.Args, "--smoke"))
        {
            await RunSmokeTestAsync(Window).ConfigureAwait(true);
        }
        else if (TryGetParameterNewValue(eventArgs.Args, "--shots", out var truncateChartDirectory))
        {
            await CapturePageAsync(Window, truncateChartDirectory).ConfigureAwait(true);
        }
        else if (TryGetParameterNewValue(eventArgs.Args, "--export-csv", out var csvDirectory))
        {
            await ExportRecipeCsvAsync(csvDirectory).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 把全部机种导出为 CSV。命令行：<c>PlcHmi.App.exe --export-csv &lt;目录&gt;</c>
    /// </summary>
    private async Task ExportRecipeCsvAsync(string dir)
    {
        var recipeItem = _host?.Services.GetRequiredService<IRecipeService>();

        if (recipeItem is null)
        {
            Log.Error("导出失败：主机未就绪");
            Shutdown(1);
            return;
        }

        try
        {
            var fileList = await recipeItem.ExportAllCsvToDirectoryAsync(dir).ConfigureAwait(true);

            Log.Information("已导出 {Count} 个机种的 CSV 到 {Dir}", fileList.Count, dir);

            foreach (var file in fileList)
            {
                Log.Information("  {File}", file);
            }

            Shutdown(0);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "导出机种 CSV 失败");
            Shutdown(1);
        }
    }

    private static bool HasParameter(string[] Parameter, string nameValue) =>
        Parameter.Any(item => string.Equals(item, nameValue, StringComparison.OrdinalIgnoreCase));

    private static bool TryGetParameterNewValue(string[] Parameter, string nameValue, out string Value)
    {
        for (var Entry = 0; Entry < Parameter.Length - 1; Entry++)
        {
            if (string.Equals(Parameter[Entry], nameValue, StringComparison.OrdinalIgnoreCase))
            {
                Value = Parameter[Entry + 1];
                return true;
            }
        }

        Value = string.Empty;
        return false;
    }

    /// <summary>
    /// 把每个导航页渲染成 PNG。用 WPF 自身的 <see cref="RenderTargetBitmap"/> 离屏渲染，
    /// 不受窗口遮挡影响，也不需要人工截图。
    /// </summary>
    private async Task CapturePageAsync(Window Window, string dir)
    {
        Directory.CreateDirectory(dir);

        if (Window.DataContext is not MasterViewModel master)
        {
            Log.Error("截图失败：主窗口 DataContext 不是 MainViewModel");
            Shutdown(1);
            return;
        }

        if (Window.Content is not FrameworkElement root)
        {
            Log.Error("截图失败：主窗口内容不是 FrameworkElement");
            Shutdown(1);
            return;
        }

        var Sequence = 0;
        foreach (var Entry in master.NavigationEntryCollection.ToList())
        {
            master.SelectedNavigationEntry = Entry;

            await Dispatcher.Yield(DispatcherPriority.Loaded);
            Window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.Render);

            // 留一点时间让采集回调把实时值填进界面
            await Task.Delay(400);

            var width = (int)Math.Ceiling(root.ActualWidth);
            var Height = (int)Math.Ceiling(root.ActualHeight);

            if (width <= 0 || Height <= 0)
            {
                Log.Warning("页面「{Title}」尺寸无效，跳过截图", Entry.Title);
                continue;
            }

            var bitChart = new RenderTargetBitmap(width, Height, 96, 96, PixelFormats.Pbgra32);
            bitChart.Render(root);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitChart));

            var file = Path.Combine(dir, $"{++Sequence:00}-{ValidFileName(Entry.Title)}.png");
            await using (var stream = File.Create(file))
            {
                encoder.Save(stream);
            }

            Log.Information("已保存截图：{File}（{Width}×{Height}）", file, width, Height);
        }

        Log.Information("截图完成，共 {Count} 张 → {Dir}", Sequence, dir);

        // 页数不足说明导航被权限或时序问题截断；退出码非 0，截图才具备门禁价值
        var complete = Sequence == expectedView.Count;

        if (!complete)
        {
            Log.Error("截图数量异常：应有 {Expected} 张，实际 {Actual} 张", expectedView.Count, Sequence);
        }

        Shutdown(complete ? 0 : 1);
    }

    /// <summary>
    /// 冒烟自检：依次切换每个导航页，强制布局，从视觉树里确认内容区真的实例化了对应视图。
    /// <para>命令：<c>PlcHmi.App.exe --smoke</c>。全部通过时退出码为 0，否则为失败页数。</para>
    /// </summary>
    private async Task RunSmokeTestAsync(Window Window)
    {
        Log.Information("═════ 页面自检开始 ═════");

        if (Window.DataContext is not MasterViewModel master)
        {
            Log.Error("自检失败：主窗口 DataContext 不是 MainViewModel");
            Shutdown(1);
            return;
        }

        var Failed = 0;
        var entryList = master.NavigationEntryCollection.ToList();
        Log.Information("导航项共 {Count} 个：{Titles}", entryList.Count,
            string.Join(" / ", entryList.Select(Entry => Entry.Title)));

        // 页数必须与期望一致：否则"逐页比对"会退化成"只遍历看得见的几页然后宣布全过"。
        // 这个盲区确实发生过——管理员登录的回调还没跑完时，3 页全部"通过"。
        if (entryList.Count != expectedView.Count)
        {
            Failed++;
            Log.Error("自检失败：导航应有 {Expected} 项，实际只有 {Actual} 项",
                expectedView.Count, entryList.Count);
        }

        foreach (var Entry in entryList)
        {
            master.SelectedNavigationEntry = Entry;

            // 让绑定与布局完成，再检查视觉树
            await Dispatcher.Yield(DispatcherPriority.Loaded);
            Window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.Render);

            var actual = FindFirstUserWidget(((MasterWindow)Window).ContentHost); // 从内容宿主起查：壳层里的 UserControl（辉光状态灯等）不再被误认为页面
            expectedView.TryGetValue(Entry.Title, out var expected);

            var issueDetail = $"页面「{Entry.Title}」→ 期望 {expected}，实际 {actual ?? "(无)"}";
            if (actual == expected)
            {
                Log.Information("自检通过：{Detail}", issueDetail);
            }
            else
            {
                Failed++;
                Log.Error("自检失败：{Detail}", issueDetail);
            }
        }

        Log.Information("═════ 页面自检结束：{Total} 页，失败 {Failed} 页 ═════", entryList.Count, Failed);

        Failed += VerifyDialog();
        Failed += await VerifyCommitMutualAsync();

        Log.Information("═════ 自检总计：失败 {Failed} 项 ═════", Failed);
        Shutdown(Failed);
    }

    /// <summary>在视觉树中寻找第一个 <see cref="UserControl"/>（即内容区实际挂载的页面视图）。</summary>
    private static string? FindFirstUserWidget(DependencyObject root)
    {
        var countValue = VisualTreeHelper.GetChildrenCount(root);

        for (var Entry = 0; Entry < countValue; Entry++)
        {
            var child = VisualTreeHelper.GetChild(root, Entry);

            if (child is UserControl Widget)
            {
                return Widget.GetType().Name;
            }

            var embedSet = FindFirstUserWidget(child);
            if (embedSet is not null)
            {
                return embedSet;
            }
        }

        return null;
    }
}
