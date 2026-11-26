using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace 上位机.应用.视图模型;

/// <summary>
/// 侧栏收起模式（P3 响应式）。
///
/// <para>窄屏或需要专注内容区时，把 196px 导航栏收为 64px 纯图标栏；
/// 名称经 ToolTip 呈现（仅收起态启用，展开态不打扰）。
/// 状态不持久化——它是会话级的视图偏好，不属于设备配置。</para>
///
/// <para>拆为 partial 的原因：主文件已贴近 400 行规模门禁基线。</para>
/// </summary>
public sealed partial class 主视图模型
{
    /// <summary>侧栏是否已收起（true = 64px 纯图标栏）。</summary>
    [ObservableProperty] private bool _侧栏已收起;

    /// <summary>切换侧栏展开 / 收起。</summary>
    [RelayCommand]
    private void 切换侧栏() => 侧栏已收起 = !侧栏已收起;
}
