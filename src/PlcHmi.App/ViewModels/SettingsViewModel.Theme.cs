using CommunityToolkit.Mvvm.ComponentModel;
using PlcHmi.App.Services;
using PlcHmi.Business.Configuration;

namespace PlcHmi.App.ViewModels;

/// <summary>
/// <see cref="SettingsViewModel"/> 的「外观（主题）」部分（partial）。
///
/// <para>从主文件拆出：主文件已到 400 行规模门禁，主题切换与"连接参数 / 变量表"是互不相干的职责，
/// 放一起只会让两边的改动互相挤行数。</para>
/// </summary>
public sealed partial class SettingsViewModel
{
    // ── 外观（所有身份可见）：深浅主题切换，立即生效并落盘 ─────────────
    /// <summary>外观主题选项（显示文本即配置值，直接写入 appsettings.json）。</summary>
    public string[] ThemeOptions { get; } = [UiOptions.themeDarkColor, UiOptions.themeLightColor];

    /// <summary>当前选中的界面主题。</summary>
    [ObservableProperty] private string _selectedTheme = ThemeManager.CurrentTheme;

    /// <summary>主题变更即切换 + 落盘；初始化或与当前主题相同（含非法值回退）时不动作。</summary>
    partial void OnSelectedThemeChanged(string value)
    {
        if (string.IsNullOrEmpty(value) || value == ThemeManager.CurrentTheme || _uiOptions is null)
        {
            return;
        }

        SafeInvokeAsync.FireAndForget(
            () => ThemeManager.ApplyAndSaveAsync(value, _uiOptions),
            _log, "切换界面主题");
    }
}
