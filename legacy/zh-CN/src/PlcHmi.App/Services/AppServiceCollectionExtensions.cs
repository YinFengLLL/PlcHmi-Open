using Microsoft.Extensions.DependencyInjection;
using 上位机.应用.视图模型;

namespace 上位机.应用.服务;

/// <summary>
/// 界面层的依赖注入装配。页面 ViewModel 全部注册为单例——
/// 上位机切页频繁，单例可以避免每次进页都重新建表、丢状态。
/// </summary>
public static class 应用服务集合扩展
{
    /// <summary>注册导航、对话框、全部页面 ViewModel 与主窗口。</summary>
    public static IServiceCollection 添加上位机界面(this IServiceCollection 服务集合)
    {
        ArgumentNullException.ThrowIfNull(服务集合);

        // 通用界面服务
        服务集合.AddSingleton<导航服务接口, 导航服务>();
        服务集合.AddSingleton<对话框服务接口, 对话框服务>();
        服务集合.AddSingleton<文件对话框服务接口, 文件对话框服务>();
        服务集合.AddSingleton<运行期错误接收器接口, 运行期错误服务>();

        // 页面 ViewModel
        服务集合.AddSingleton<运行视图模型>();
        服务集合.AddSingleton<参数视图模型>();
        服务集合.AddSingleton<锁附示教视图模型>();
        服务集合.AddSingleton<点胶示教视图模型>();
        服务集合.AddSingleton<标定视图模型>();
        服务集合.AddSingleton<配方视图模型>();
        服务集合.AddSingleton<手动视图模型>();
        服务集合.AddSingleton<IO视图模型>();
        服务集合.AddSingleton<报警视图模型>();
        服务集合.AddSingleton<历史记录视图模型>();
        服务集合.AddSingleton<设置视图模型>();

        // 系统设置页内的「扫码枪设置」板块（2026-11-12）：独立 VM，随设置页一起注入
        服务集合.AddSingleton<扫码枪设置视图模型>();

        // 可配置弹窗宿主（M 触发线圈 → 主窗居中弹窗，定义见 config/popups.json）
        服务集合.AddSingleton<弹窗宿主视图模型>();

        // 主窗口与主 ViewModel
        服务集合.AddSingleton<主视图模型>();
        服务集合.AddSingleton<主窗口>();

        return 服务集合;
    }
}
