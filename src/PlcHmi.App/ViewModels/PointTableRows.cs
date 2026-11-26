using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.App.ViewModels;

/// <summary>
/// 页面分组过滤：配置里带 <c>分组</c> 的元件只在该分组的页面上出现。
/// <para>留空视为"全局元件"，任何页面都显示——这样公共按钮不必逐页复制。</para>
/// </summary>
internal static class PageGroup
{
    /// <summary>元件分组是否属于某个页面分组。</summary>
    public static bool Match(string componentGroup, string PageGroup) =>
        string.IsNullOrWhiteSpace(componentGroup)
        || string.IsNullOrWhiteSpace(PageGroup)
        || string.Equals(componentGroup, PageGroup, StringComparison.OrdinalIgnoreCase);
}

/// <summary>点表里的一个单元格（一点 × 一列）。编辑只改内存，写 PLC 由「坐标示教」或「参数写入」触发。</summary>
public sealed partial class PointValueCellViewModel : ObservableObject
{
    private readonly IPointTableService _pointTable;
    private readonly PointTableDefinition _table;
    private readonly PointTableColumnDefinition _column;
    private bool _load;

    [ObservableProperty] private string _text = "--";

    public PointValueCellViewModel(PointTableDefinition Table, PointTableColumnDefinition Column, IPointTableService PointTable)
    {
        _table = Table;
        _column = Column;
        _pointTable = PointTable;
    }

    /// <summary>列键。</summary>
    public string ColumnKey => _column.Key;

    /// <summary>列定义（供界面读取单位等）。</summary>
    public PointTableColumnDefinition Column => _column;

    /// <summary>当前行序号。</summary>
    public int Sequence { get; private set; }

    /// <summary>
    /// 是否为指令码列。
    /// <para><b>2026-10-21：</b>指令码改由辅助地址区的下拉框选择（选中即写 PLC），
    /// 本列只负责<b>显示文字</b>（如「直线」），不再手填数字。</para>
    /// </summary>
    public bool IsInstructionCode => string.Equals(_column.Key, PointTableDefinition.instructionCodeColumnKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>用点数据刷新显示。</summary>
    public void Load(int Sequence, PointValueRow Row)
    {
        _load = true;
        this.Sequence = Sequence;

        var empty = Row.IsEmpty(_table);
        var Value = Row.Take(_column.Key);
        Text = empty && !IsInstructionCode ? "--" : Format(Value);

        _load = false;
    }

    partial void OnTextChanged(string value) => Push();

    private void Push()
    {
        // 指令码列显示的是文字（「直线」），解析不成数字，也不该由本列回写：
        // 写入只走下拉框（点表服务.异步写入指令码）
        if (_load || IsInstructionCode)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Text) || Text == "--")
        {
            _pointTable.SetPointValue(_table.Key, Sequence, _column.Key, 0);
            return;
        }

        if (CoordinateAux.TryParse(Text, out var Value))
        {
            _pointTable.SetPointValue(_table.Key, Sequence, _column.Key, Value);
        }
    }

    private string Format(double Value) =>
        IsInstructionCode ? InstructionCodeDefinition.ShowText(Value) : CoordinateAux.Format(Value);
}

/// <summary>点表里的一行（一个点）。</summary>
public sealed partial class PointValueRowViewModel : ObservableObject
{
    private readonly PointTableDefinition _table;
    private readonly Func<int, Task>? _selectedBackAdjust;

    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _hasValue;

    public PointValueRowViewModel(PointTableDefinition Table, int indexValue, IPointTableService PointTable, Func<int, Task>? SelectedBackAdjust = null)
    {
        _table = Table;
        Sequence = indexValue;
        _selectedBackAdjust = SelectedBackAdjust;

        foreach (var columnItem in Table.Column)
        {
            CellCollection.Add(new PointValueCellViewModel(Table, columnItem, PointTable));
        }
    }

    /// <summary>绝对序号（0 基，固定不变）。</summary>
    public int Sequence { get; }

    /// <summary>界面显示的点号（1 基，两位）。</summary>
    public string Tag => (Sequence + 1).ToString("00");

    /// <summary>单元格集合（与表列一一对应）。</summary>
    public System.Collections.ObjectModel.ObservableCollection<PointValueCellViewModel> CellCollection { get; } = new();

    /// <summary>用点数据刷新整行。</summary>
    public void Load(PointValueRow Row)
    {
        foreach (var unitItem in CellCollection)
        {
            unitItem.Load(Sequence, Row);
        }

        HasValue = !Row.IsEmpty(_table);
    }

    /// <summary>点击该行空白处：把这一行设为当前点。</summary>
    [RelayCommand]
    private Task Selected() =>
        IsCurrent || _selectedBackAdjust is null ? Task.CompletedTask : _selectedBackAdjust(Sequence);
}

/// <summary>示教页的一个点动轴（配置驱动：正/反转线圈 + 位置标签）。</summary>
public sealed partial class TeachAxisViewModel : ObservableObject
{
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _positionText = "--.--";

    public TeachAxisViewModel(JogButtonItem Item)
    {
        AxisKey = Item.Axis;
        Name = Item.DisplayName;
        PositionTag = Item.PositionTag ?? string.Empty;
    }

    /// <summary>轴标识（配置键：X / Y / Z / ZZ / GlueX / GlueZ）。</summary>
    public string AxisKey { get; }

    /// <summary>界面显示名称。</summary>
    public string Name { get; }

    /// <summary>当前位置点位名（可空）。</summary>
    public string PositionTag { get; }

    /// <summary>回零按钮文案。</summary>
    public string HomeCaption => $"{Name} 回零";
}
