using CommunityToolkit.Mvvm.ComponentModel;
using 上位机.核心.模型;

namespace 上位机.应用.视图模型;

/// <summary>
/// 手动调试页的轴选择项（一点动轴一行）。
///
/// <para>从 <c>ManualViewModel.cs</c> 拆出：手动页主文件已在规模基线边缘（529 行），
/// 轴项是与"元件 / 使能 / 点动"并列的独立小对象，单独一处更易审查。</para>
/// </summary>
public sealed partial class 轴视图模型 : ObservableObject
{
    [ObservableProperty] private bool _是否选中;
    [ObservableProperty] private string _位置文本 = "--.--";

    public 轴视图模型(点动按钮项 定义项)
    {
        轴键 = 定义项.轴;
        名称 = 定义项.显示名称;
        位置标签 = 定义项.位置标签 ?? string.Empty;
    }

    /// <summary>轴标识（X / Y / Z / ZZ / GlueX / GlueZ）。</summary>
    public string 轴键 { get; }

    /// <summary>界面显示名称。</summary>
    public string 名称 { get; }

    /// <summary>当前位置对应的点位名（与 tags.json 一致）；留空表示该轴没有位置回读。</summary>
    public string 位置标签 { get; }
}
