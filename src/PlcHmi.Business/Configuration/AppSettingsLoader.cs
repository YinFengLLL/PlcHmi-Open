using System.Text.Json;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Configuration;

/// <summary>
/// <c>config/appsettings.json</c> 的整体结构。
/// </summary>
public sealed class AppSettingsDocument
{
    /// <summary>PLC 连接参数。</summary>
    public PlcConnectionOptions PlcConnection { get; set; } = new();

    /// <summary>采集参数。</summary>
    public PollingOptions Polling { get; set; } = new();

    /// <summary>界面参数。</summary>
    public UiOptions Ui { get; set; } = new();

    /// <summary>四轴定义（参数页用）。</summary>
    public List<AxisDefinition> AxisList { get; set; } = new();

    /// <summary>日志参数。</summary>
    public LogOptions Log { get; set; } = new();

    /// <summary>
    /// 扫码枪参数（2026-11-12 新增）：通讯方式 / 串口或网口 / 两个信号寄存器 / 工作模式。
    /// <para>老配置文件没有这一节时按默认值（未启用）运行，不影响启动。</para>
    /// </summary>
    public ScannerConfig Scanner { get; set; } = new();
}

/// <summary>界面相关参数。</summary>
public sealed class UiOptions
{
    public const string sectionName = "Ui";

    /// <summary>深色主题名（深空蓝，默认）。</summary>
    public const string themeDarkColor = "深色";

    /// <summary>浅色主题名。</summary>
    public const string themeLightColor = "浅色";

    /// <summary>站点名称，显示在顶栏。</summary>
    public string StationName { get; set; } = "装配工位 1";

    /// <summary>目标产量。</summary>
    public long TargetCount { get; set; } = 5000;

    /// <summary>界面主题：<see cref="themeDarkColor"/>（默认）或 <see cref="themeLightColor"/>，
    /// 设置页可运行期切换并立即生效；未知值回退深色。</summary>
    public string Theme { get; set; } = themeDarkColor;
}

/// <summary>日志相关参数。</summary>
public sealed class LogOptions
{
    public const string sectionName = "Serilog";

    /// <summary>最低日志级别（Verbose/Debug/Information/Warning/Error/Fatal）。</summary>
    public string MinLevel { get; set; } = "Information";
}

/// <summary>
/// 应用配置加载器。读取失败时回退到默认值，保证程序仍能启动（现场配置写错不至于打不开界面）。
///
/// <para><b>2026-11-19 安全加固：</b>此前手写 <c>File.OpenRead</c> + <c>Deserialize</c>，
/// 解析失败一律返回出厂默认文档，而保存方又是"加载整份 → 改一节 → 整体写回"——
/// 于是 <c>appsettings.json</c> 一旦在保存那一刻不可读（升级中断留半截、杀软锁文件、磁盘满），
/// 整份 PLC 连接 + 扫码枪 + 轮询参数就会被出厂默认值覆盖，现场表现为"第二天开机连不上 PLC"。
/// 现在统一走 <see cref="ConfigFileLoader"/>（带 <c>.bak</c> 回退），并把"可不可信"通过
/// <see cref="LoadWithResult"/> 交给保存方判断：主文件与备份都坏时**拒绝落盘**，绝不拿默认值覆盖。</para>
/// </summary>
public static class AppSettingsLoader
{
    /// <summary>
    /// 带结果分类的加载（供保存方判断"这份数据能不能作为写回基础"）。
    /// </summary>
    /// <param name="filePath">配置文件路径，为空时使用 <see cref="ConfigPath.AppSettings"/>。</param>
    public static ConfigLoadResult<AppSettingsDocument> LoadWithResult(string? filePath = null) =>
        ConfigFileLoader.LoadJson<AppSettingsDocument>(
            filePath ?? ConfigPath.AppSettings, JsonConfig.Read);

    /// <summary>
    /// 加载配置。
    /// </summary>
    /// <param name="error">加载失败时的错误信息（成功为 <c>null</c>）。</param>
    /// <param name="path">配置文件路径，为空时使用 <see cref="ConfigPath.AppSettings"/>。</param>
    public static AppSettingsDocument Load(out string? error, string? filePath = null)
    {
        var result = LoadWithResult(filePath);

        switch (result.loadResult)
        {
            case LoadResultSplitCategory.Loaded:
                error = null;
                return result.Value!;

            case LoadResultSplitCategory.RestoredFromBackup:
                // 值可用，但必须让调用方看见"最近一次改动可能已丢失"
                error = $"主配置文件损坏，已从备份恢复：{result.path}（{result.ErrorInfo}）";
                return result.Value!;

            case LoadResultSplitCategory.Missing:
                error = $"未找到配置文件：{result.path}，将使用默认参数运行。";
                return new AppSettingsDocument();

            default:
                error = $"配置文件与备份均不可用：{result.path}（{result.ErrorInfo}），将使用默认参数运行。";
                return new AppSettingsDocument();
        }
    }
}

/// <summary>
/// 机种配置加载器。读取 <c>config/recipe-config.json</c>。
///
/// <para><b>这里的每个地址都是可配置的</b>——现场若换了 PLC 内存规划，
/// 只改 JSON 即可，不需要动代码。</para>
/// </summary>
public static class RecipeConfigLoader
{
    /// <summary>
    /// 加载机种配置。
    /// </summary>
    /// <param name="error">加载失败时的错误信息（成功为 <c>null</c>）。</param>
    /// <param name="path">配置文件路径，为空时使用 <see cref="ConfigPath.RecipeConfig"/>。</param>
    public static RecipeConfig Load(out string? error, string? filePath = null)
    {
        error = null;
        filePath ??= ConfigPath.RecipeConfig;

        if (!File.Exists(filePath))
        {
            error = $"未找到机种配置文件：{filePath}，将使用内置默认基址（点表 D2500、参数 D3300、控制 D3330）。";
            return new RecipeConfig();
        }

        try
        {
            using var stream = File.OpenRead(filePath);
            var configPairObject = JsonSerializer.Deserialize<RecipeConfig>(stream, JsonConfig.Read);

            if (configPairObject is null)
            {
                error = $"机种配置文件内容为空：{filePath}，将使用内置默认基址。";
                return new RecipeConfig();
            }

            // 老现场文件没有"结构体数组"参数（2026-10-21 新增）：按出厂定义补齐，
            // 否则点胶单点参数会静默退回固定地址、新功能看起来"没生效"。
            // 只补零、不覆盖现场值，详见 <see cref="AuxAddressStructDefault"/>。
            AuxAddressStructDefault.App(configPairObject);

            // 点胶页固定按钮「参数模式」（M5006，2026-10-21 新增）：老配置里没有这一条，
            // 不补就"装了新版本但按钮不出现"，详见 <see cref="ParameterModeButtonDefault"/>。
            ParameterModeButtonDefault.App(configPairObject);

            // 下相机标定（2026-10-17 新增）：老配置没有 下相机 X/Z 字段 → 「编辑地址」里
            // 看不到地址、下发报"缺少字段"（2026-10-21 现场报障）。缺就按出厂定义补齐。
            configPairObject.Calibration.FillBottomCamera();

            // 锁附页「当前浮高」（2026-10-24 新增）：老配置没有该地址 → 手动区不显示浮高、
            // 保存坐标点也不写浮高列。只补零，不改现场已配好的地址。
            CurrentFloatHeightDefault.App(configPairObject);

            return configPairObject;
        }
        catch (Exception exception)
        {
            error = $"解析机种配置文件失败：{filePath} —— {exception.Message}";
            return new RecipeConfig();
        }
    }
}
