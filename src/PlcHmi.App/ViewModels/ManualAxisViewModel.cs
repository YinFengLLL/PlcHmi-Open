using CommunityToolkit.Mvvm.ComponentModel;
using PlcHmi.Core.Models;

namespace PlcHmi.App.ViewModels;

/// <summary>
/// 手动调试页的轴选择项（一点动轴一行）。
///
/// <para>从 <c>ManualViewModel.cs</c> 拆出：手动页主文件已在规模基线边缘（529 行），
/// 轴项是与"元件 / 使能 / 点动"并列的独立小对象，单独一处更易审查。</para>
/// </summary>
public sealed partial class AxisViewModel : ObservableObject
{
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _positionText = "--.--";

    public AxisViewModel(JogButtonItem DefinitionItem)
    {
        AxisKey = DefinitionItem.Axis;
        Name = DefinitionItem.DisplayName;
        PositionTag = DefinitionItem.PositionTag ?? string.Empty;
    }

    /// <summary>轴标识（X / Y / Z / ZZ / GlueX / GlueZ）。</summary>
    public string AxisKey { get; }

    /// <summary>界面显示名称。</summary>
    public string Name { get; }

    /// <summary>当前位置对应的点位名（与 tags.json 一致）；留空表示该轴没有位置回读。</summary>
    public string PositionTag { get; }
}
