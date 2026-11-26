using Microsoft.Extensions.DependencyInjection;
using PlcHmi.App.ViewModels;

namespace PlcHmi.App.Services;

/// <summary>
/// 界面层的依赖注入装配。页面 ViewModel 全部注册为单例——
/// 上位机切页频繁，单例可以避免每次进页都重新建表、丢状态。
/// </summary>
public static class AppServiceCollectionExtensions
{
    /// <summary>注册导航、对话框、全部页面 ViewModel 与主窗口。</summary>
    public static IServiceCollection AddPlcHmiUi(this IServiceCollection serviceCollection)
    {
        ArgumentNullException.ThrowIfNull(serviceCollection);

        // 通用界面服务
        serviceCollection.AddSingleton<INavigationService, NavigationService>();
        serviceCollection.AddSingleton<IDialogService, DialogService>();
        serviceCollection.AddSingleton<IFileDialogService, FileDialogService>();
        serviceCollection.AddSingleton<IRuntimeErrorReceiver, RuntimeErrorService>();

        // 页面 ViewModel
        serviceCollection.AddSingleton<RunViewModel>();
        serviceCollection.AddSingleton<ParameterViewModel>();
        serviceCollection.AddSingleton<ScrewTeachViewModel>();
        serviceCollection.AddSingleton<DispenseTeachViewModel>();
        serviceCollection.AddSingleton<CalibrationViewModel>();
        serviceCollection.AddSingleton<RecipeViewModel>();
        serviceCollection.AddSingleton<ManualViewModel>();
        serviceCollection.AddSingleton<IoViewModel>();
        serviceCollection.AddSingleton<AlarmViewModel>();
        serviceCollection.AddSingleton<HistoryRecordViewModel>();
        serviceCollection.AddSingleton<SettingsViewModel>();

        // 系统设置页内的「扫码枪设置」板块（2026-11-12）：独立 VM，随设置页一起注入
        serviceCollection.AddSingleton<ScannerSettingsViewModel>();

        // 可配置弹窗宿主（M 触发线圈 → 主窗居中弹窗，定义见 config/popups.json）
        serviceCollection.AddSingleton<PopupHostViewModel>();

        // 主窗口与主 ViewModel
        serviceCollection.AddSingleton<MasterViewModel>();
        serviceCollection.AddSingleton<MasterWindow>();

        return serviceCollection;
    }
}
