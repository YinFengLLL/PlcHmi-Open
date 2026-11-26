using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace 上位机.应用.服务;

/// <summary>
/// 支持整体替换的 <see cref="ObservableCollection{T}"/>。
///
/// <para><b>动机：</b>整表装载（历史翻页、报警刷新）此前是"Clear + 逐条 Add"，
/// 每个元素各发一次 <c>CollectionChanged</c>；非虚拟化面板下每次通知都要
/// 走一遍布局，几千行就是几千次。现在只发一次 Reset。</para>
///
/// <para><b>为什么用 Reset：</b>消费方无需处理 Add/Remove 的增量语义，
/// 最终状态与 Clear + Add 完全一致；WPF 对 Reset 的处理是整体重新枚举，
/// 这正是"换了一批数据"场景要的语义。</para>
/// </summary>
public sealed class 批量可观察集合<T> : ObservableCollection<T>
{
    /// <summary>创建空集合。</summary>
    public 批量可观察集合()
    {
    }

    /// <summary>用初始内容创建集合。</summary>
    public 批量可观察集合(IEnumerable<T> 初始项)
        : base(初始项)
    {
    }

    /// <summary>清空并一次性填入新内容（只发一次 Reset 通知）。</summary>
    public void 替换全部(IEnumerable<T> 新内容)
    {
        ArgumentNullException.ThrowIfNull(新内容);

        Items.Clear();

        foreach (var 项 in 新内容)
        {
            Items.Add(项);
        }

        // 与 Clear + Add 的最终通知面保持一致：Count / 索引器 / Reset 三件事
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
