using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using 上位机.核心.模型;

namespace 上位机.应用.视图模型;

/// <summary>
/// IO 点位监控页的行视图模型（单点 / 分组）。
///
/// <para><b>为什么拆出来：</b>IoViewModel.cs 已超过 400 行规模门禁，
/// 行模型是独立的可复用单元（标定页的辅助点行也是同款结构）。</para>
/// </summary>
public sealed partial class IO点位视图模型 : ObservableObject
{
    [ObservableProperty] private string _含义;
    [ObservableProperty] private bool _是否开启;
    [ObservableProperty] private bool _是否编辑中;

    public IO点位视图模型(IO点位 模型对象)
    {
        模型 = 模型对象;
        代码 = 模型对象.代码;
        _含义 = 模型对象.含义;
    }

    /// <summary>底层配置模型。</summary>
    public IO点位 模型 { get; }

    /// <summary>点位编号，如 X0 / Y10。<b>X / Y 为八进制编号。</b></summary>
    public string 代码 { get; }

    /// <summary>是否启用（未启用时界面置灰占位）。</summary>
    public bool 启用 => 模型.启用;

    /// <summary>是否反相（常闭信号）。</summary>
    public bool 取反 => 模型.取反;

    /// <summary>是否已定义含义。</summary>
    public bool 是否已定义 => !string.IsNullOrWhiteSpace(含义);

    /// <summary>状态文字。</summary>
    public string 状态文本 => 是否开启 ? "ON" : "OFF";

    partial void On含义Changed(string value)
    {
        模型.含义 = value;
        OnPropertyChanged(nameof(是否已定义));
    }

    partial void On是否开启Changed(bool value) => OnPropertyChanged(nameof(状态文本));
}

/// <summary>IO 点分组（按八进制，每 8 点一组）。</summary>
public sealed class IO分组视图模型
{
    public IO分组视图模型(string 标题文本, IEnumerable<IO点位视图模型> 点位列表)
    {
        标题 = 标题文本;
        点位集合 = new ObservableCollection<IO点位视图模型>(点位列表);
    }

    /// <summary>组标题，如 “X0 — X7”。</summary>
    public string 标题 { get; }

    /// <summary>组内点位。</summary>
    public ObservableCollection<IO点位视图模型> 点位集合 { get; }

    /// <summary>已定义含义的点数描述。</summary>
    public string 已定义摘要
    {
        get
        {
            var 已定义 = 0;

            // 单遍统计：曾经的两次枚举（Count(谓词) + Count）每个绑定刷新都遍历两轮
            for (var 序号 = 0; 序号 < 点位集合.Count; 序号++)
            {
                if (点位集合[序号].是否已定义)
                {
                    已定义++;
                }
            }

            return $"{已定义} / {点位集合.Count} 已定义";
        }
    }
}
