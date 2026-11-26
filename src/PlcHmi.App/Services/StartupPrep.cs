using PlcHmi.Business.Configuration;

namespace PlcHmi.App.Services;

/// <summary>
/// 启动期的目录与配置准备。
///
/// <para>从 <c>App.OnStartup</c> 拆出来的原因很简单：那里已经堆了异常处理、日志、单实例、
/// 配置加载、诊断分发……"建目录 + 补出厂配置 + 可写性预检"自成一段，放在一起反而难找。</para>
///
/// <para>三件事的顺序有讲究：<b>先建目录、再补配置、最后预检可写</b>——
/// 预检要探的就是补齐之后的状态（否则首次部署时 config/ 还是空的，预检通过是假象）。</para>
/// </summary>
public static class StartupPreparation
{
    /// <summary>
    /// 执行启动期准备，返回（本次从出厂模板补齐的文件，目录可写性问题，老配置升级说明）。
    /// <para>升级说明<b>不在这里记日志</b>：此刻 Serilog 还没初始化（日志在读完配置之后才建），
    /// 这个点写日志会直接丢进静默 logger。由调用方在日志就绪后统一打印。</para>
    /// </summary>
    public static (
        IReadOnlyList<string> fillConfig,
        IReadOnlyList<string> writabilityIssue,
        string? upgradeDescription) Execute()
    {
        ConfigPath.EnsureCreated();

        // 出厂配置补齐（只补缺失、绝不覆盖）：
        // 发布包里带的是「出厂配置/」模板而不是 config/。现场升级直接覆盖目录时，
        // 机种（recipes.json）、PLC 连接参数（appsettings.json）、账号等现场数据天然不会被冲掉；
        // 新版本新增的配置文件会在下次启动时自动补进 config/。
        var fillConfig = ConfigPath.FillMissingConfig();

        // 老现场配置的结构升级（2026-11）：点胶点表 30 点 → 50 点。
        // 必须放在"补齐"之后、各加载器之前：发布包不覆盖现场 config/，
        // 不在这里升一次，现场装了新版本界面上仍然是 30 个点。
        var upgradeDescription = RecipeConfigMigration.App(ConfigPath.RecipeConfig);

        // 目录可写性预检：程序以 asInvoker 运行、配置写在程序目录下。
        // 装到 C:\Program Files\ 这类普通用户不可写的路径时，一切都要等
        // "操作员按下保存"才失败 —— 那时人已经在设备前面等着了。
        // 提前查出来，启动横幅直接说明原因。
        var writabilityIssue = ConfigPath.VerifyWritable();

        return (fillConfig, writabilityIssue, upgradeDescription);
    }
}
