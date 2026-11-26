using CommunityToolkit.Mvvm.ComponentModel;
using 上位机.应用.服务;
using 上位机.业务.配置;

namespace 上位机.应用.视图模型;

/// <summary>
/// <see cref="设置视图模型"/> 的「外观（主题）」部分（partial）。
///
/// <para>从主文件拆出：主文件已到 400 行规模门禁，主题切换与"连接参数 / 变量表"是互不相干的职责，
/// 放一起只会让两边的改动互相挤行数。</para>
/// </summary>
public sealed partial class 设置视图模型
{
    // ── 外观（所有身份可见）：深浅主题切换，立即生效并落盘 ─────────────
    /// <summary>外观主题选项（显示文本即配置值，直接写入 appsettings.json）。</summary>
    public string[] 主题选项 { get; } = [界面选项.主题深色, 界面选项.主题浅色];

    /// <summary>当前选中的界面主题。</summary>
    [ObservableProperty] private string _选中主题 = 主题管理器.当前主题;

    /// <summary>主题变更即切换 + 落盘；初始化或与当前主题相同（含非法值回退）时不动作。</summary>
    partial void On选中主题Changed(string value)
    {
        if (string.IsNullOrEmpty(value) || value == 主题管理器.当前主题 || _界面选项 is null)
        {
            return;
        }

        异步安全调用.即发即忘(
            () => 主题管理器.异步应用并保存(value, _界面选项),
            _日志, "切换界面主题");
    }
}
