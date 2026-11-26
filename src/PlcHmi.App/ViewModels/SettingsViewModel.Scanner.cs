namespace PlcHmi.App.ViewModels;

/// <summary>
/// <see cref="SettingsViewModel"/> 的「扫码枪设置」部分（partial，2026-11-12）。
///
/// <para>扫码枪的编辑状态与保存命令都放在独立的
/// <see cref="ScannerSettingsViewModel"/> 里——主设置页只负责把它挂到界面上，
/// 这样"连接参数 / 变量表 / 主题 / 扫码枪"四块各改各的，互不挤行数。</para>
/// </summary>
public sealed partial class SettingsViewModel
{
    /// <summary>扫码枪设置板块（未注册时为 <c>null</c>，界面整块隐藏）。</summary>
    private readonly ScannerSettingsViewModel? ScannerSettingsSection;

    /// <summary>扫码枪设置板块（供界面绑定）。</summary>
    public ScannerSettingsViewModel? ScannerSettings => ScannerSettingsSection;

    /// <summary>是否显示扫码枪设置卡（未注册该板块时整块隐藏）。</summary>
    public bool ShowScannerSet => ScannerSettingsSection is not null;
}
