using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using PlcHmi.Core.Models;

namespace PlcHmi.App.Views;

/// <summary>
/// 页面地址编辑器（仅管理员入口）。
///
/// <para>把页面上所有与 PLC 交互的元件地址集中成一张表：点表列基址、辅助地址、
/// 动作按钮、点动与回零线圈。辅助地址可由管理员新增 / 删除，其余元件地址可改不可删。</para>
///
/// <para>只负责收集输入；权限守卫与地址格式校验在服务层。</para>
/// </summary>
public partial class AddressEditDialog : Window
{
    /// <summary>可选数据类型（辅助地址用）。</summary>
    private static readonly string[] allTypeOptions =
        ["Float32", "UInt16", "Int16", "Int32", "UInt32", "布尔"];

    /// <summary>一行的可编辑状态。</summary>
    public sealed partial class AddressRow : ObservableObject
    {
        private readonly AddressEditItem _item;

        [ObservableProperty] private string _name = string.Empty;
        [ObservableProperty] private string _addressText = string.Empty;
        [ObservableProperty] private string _dataTypeText = "Float32";
        [ObservableProperty] private bool _structMember;

        public AddressRow(AddressEditItem Item)
        {
            _item = Item;
            Name = Item.Name;
            AddressText = Item.AddressText;
            DataTypeText = string.IsNullOrWhiteSpace(Item.DataTypeText) ? "Float32" : Item.DataTypeText;
            StructMember = Item.StructMember;
        }

        /// <summary>类别（分组表头）。</summary>
        public string Category => _item.Category;

        /// <summary>底层条目（回写用）。</summary>
        public AddressEditItem Item => _item;

        /// <summary>是否允许删除。</summary>
        public bool Deletable => _item.Deletable;

        /// <summary>是否允许修改数据类型（只有辅助地址是任意 D 区数值；动作按钮是 M 区线圈）。</summary>
        public bool CanChangeType => string.Equals(_item.Category, "辅助地址", StringComparison.Ordinal);

        /// <summary>是否显示"结构体成员"勾选框（仅点胶页的辅助地址）。</summary>
        public bool SettableStructMember => _item.SettableStructMember;

        /// <summary>成员地址由结构体布局算出，编辑器里不能再手改。</summary>
        public bool AddressEditable => !StructMember;

        partial void OnStructMemberChanged(bool value) => OnPropertyChanged(nameof(AddressEditable));

        /// <summary>数据类型下拉项。</summary>
        public IReadOnlyList<string> TypeOptions => allTypeOptions;
    }

    private readonly AddressEditRequest _request;
    private readonly List<AddressRow> _deleted = new();

    private AddressEditDialog(AddressEditRequest Request)
    {
        InitializeComponent();

        _request = Request;
        TitleWidget.Text = $"地址编辑 · {Request.Title}";

        foreach (var Item in Request.ItemCollection)
        {
            RowCollection.Add(new AddressRow(Item));
        }

        DataContext = this;
    }

    /// <summary>可编辑行集合（供分组视图绑定）。</summary>
    public ObservableCollection<AddressRow> RowCollection { get; } = new();

    /// <summary>弹出编辑器。返回是否保存（保存时已就地写回请求对象）。</summary>
    public static bool Show(AddressEditRequest Request)
    {
        var Dialog = new AddressEditDialog(Request)
        {
            Owner = Application.Current.MainWindow,
        };

        return Dialog.ShowDialog() == true;
    }

    /// <summary>仅供自检使用：实例化但不显示。</summary>
    internal static Window CreateVerify() => new AddressEditDialog(new AddressEditRequest
    {
        Title = "自检",
        DefaultGroup = "自检",
        ItemCollection =
        [
            new AddressEditItem { Category = "点表列", Key = "X", Name = "X坐标基址", AddressText = "D2500" },
        ],
    });

    private void AddAuxAddress(object sender, RoutedEventArgs eventArgs)
    {
        // 上限按「本页分组」统计：分组匹配口径与服务层一致（空分组视为全局元件）。
        var thisPageGroup = _request.DefaultGroup;
        var thisPageCount = RowCollection.Count(Row =>
            Row.Category == "辅助地址" && GroupMatch(Row.Item.Group, thisPageGroup));

        if (thisPageCount >= AuxAddressItem.perGroupUpperLimit)
        {
            MessageText.Text = $"「{thisPageGroup}」分组的辅助地址已达 {AuxAddressItem.perGroupUpperLimit} 个上限，不能再新增。";
            return;
        }

        var Sequence = NextNotOccupiedKey("辅助地址");

        RowCollection.Add(new AddressRow(new AddressEditItem
        {
            Category = "辅助地址",
            Key = $"辅助地址{Sequence}",
            Name = $"新辅助地址{Sequence}",
            AddressText = "D0",
            DataTypeText = "Float32",
            Group = _request.DefaultGroup,
            Deletable = true,
            Insert = true,

            // 新条目跟随本页口径：点胶页的辅助地址才有"结构体成员"这个概念
            SettableStructMember = RowCollection.Any(Row => Row.SettableStructMember),
        }));
    }

    private void AddActionButton(object sender, RoutedEventArgs eventArgs)
    {
        var Sequence = NextNotOccupiedKey("动作");

        RowCollection.Add(new AddressRow(new AddressEditItem
        {
            Category = "动作按钮",
            Key = $"动作{Sequence}",
            Name = $"新动作按钮{Sequence}",

            // 新增按钮默认按"脉冲 M 区线圈"处理
            AddressText = "M0",
            DataTypeText = "布尔",
            Group = _request.DefaultGroup,
            Deletable = true,
            Insert = true,
        }));
    }

    /// <summary>
    /// 取下一个未占用的编号（从 1 起递增，返回第一个空位）。
    /// <para>旧逻辑用「行数 + 1」生成键，遇到配置里已有的<b>不连续编号键</b>
    /// （如 辅助地址7/8/9/11）会撞键；服务层一旦全局按键就地更新，撞键即覆盖旧项。
    /// 这里改为扫描全部已知键取第一个空位。</para>
    /// </summary>
    private int NextNotOccupiedKey(string prefix)
    {
        // 已知占用键 = 当前表格里的行 + 请求原始项 + 本次会话已删除的行。
        // 已删除项在保存前仍留在服务层配置里，不能复用它的键，否则又撞上残留项。
        var occupiedKey = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var Row in RowCollection)
        {
            occupiedKey.Add(Row.Item.Key);
        }

        foreach (var Item in _request.ItemCollection)
        {
            occupiedKey.Add(Item.Key);
        }

        foreach (var Row in _deleted)
        {
            occupiedKey.Add(Row.Item.Key);
        }

        // 占用键最多 Count 个，检查前 Count+1 个编号必有空位（鸽巢原理），循环必定终止。
        for (var Sequence = 1; Sequence <= occupiedKey.Count + 1; Sequence++)
        {
            if (!occupiedKey.Contains($"{prefix}{Sequence}"))
            {
                return Sequence;
            }
        }

        throw new InvalidOperationException($"前缀「{prefix}」的键已用尽。");
    }

    /// <summary>分组匹配（空分组视为全局元件，与页面地址服务同口径）。</summary>
    private static bool GroupMatch(string componentGroup, string PageGroup) =>
        string.IsNullOrWhiteSpace(componentGroup)
        || string.IsNullOrWhiteSpace(PageGroup)
        || string.Equals(componentGroup, PageGroup, StringComparison.OrdinalIgnoreCase);

    private void DeleteRow(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not FrameworkElement ElementItem || ElementItem.DataContext is not AddressRow Row)
        {
            return;
        }

        RowCollection.Remove(Row);

        if (!Row.Item.Insert)
        {
            _deleted.Add(Row);
        }
    }

    private void Confirm(object sender, RoutedEventArgs eventArgs)
    {
        foreach (var Row in RowCollection)
        {
            if (string.IsNullOrWhiteSpace(Row.AddressText))
            {
                MessageText.Text = $"「{Row.Name}」的地址不能为空。";
                return;
            }

            Row.Item.Name = Row.Name.Trim();
            Row.Item.AddressText = Row.AddressText.Trim();
            Row.Item.DataTypeText = Row.DataTypeText;
            Row.Item.StructMember = Row.StructMember;
        }

        _request.ItemCollection.Clear();
        _request.ItemCollection.AddRange(RowCollection.Select(Row => Row.Item));

        _request.DeletedItem.Clear();
        _request.DeletedItem.AddRange(_deleted.Select(Row => Row.Item));

        DialogResult = true;
    }

    private void Cancel(object sender, RoutedEventArgs eventArgs) => DialogResult = false;
}
