using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.App.Views;

/// <summary>
/// 数据体「数据项」的编辑行（表格里的一列）。
/// <para>类型与小数位用下拉而不是输入框：都是小范围枚举值，
/// 手打必然出现"INT "、"２"（全角）这类看不见的输入错误，
/// 而地址错一位、类型错一项都会让历史数据整体失真。</para>
/// </summary>
public sealed partial class HistoryDataItemEditRow : ObservableObject
{
    // 2026-10-24：新增 REAL（32 位浮点）——PLC 直接给工程量时不必再放大成整数
    private static readonly string[] TypeOptions = ["INT（16 位）", "DINT（32 位）", "REAL（32 位浮点）"];

    private static readonly string[] bitArrayOptions = ["0", "1", "2", "3", "4"];

    private static readonly string[] colorOptions = ["无", "红", "黄", "绿"];

    [ObservableProperty] private string _name = string.Empty;

    [ObservableProperty] private string _address = string.Empty;

    /// <summary>0 = INT，1 = DINT，2 = REAL（32 位浮点）。</summary>
    [ObservableProperty] private int _typeIndex;

    /// <summary>
    /// 小数位（0–4）。
    /// <para>INT / DINT：显示值 = PLC 原始值 ÷ 10^小数位（<c>600</c> → <c>6.00</c>）。</para>
    /// <para>REAL：只表示显示保留几位（<c>12.3456</c> → <c>12.35</c>），不缩放。</para>
    /// </summary>
    [ObservableProperty] private int _decimalBitIndex;

    [ObservableProperty] private string _unit = string.Empty;

    /// <summary>是否按 ASCII 字符显示（仅 DINT 可勾选）。</summary>
    [ObservableProperty] private bool _showAsAscii;

    /// <summary>0 = 无，1 = 红，2 = 黄，3 = 绿。</summary>
    [ObservableProperty] private int _colorIndex;

    /// <summary>类型下拉数据源。</summary>
    public string[] DataTypeOptions => TypeOptions;

    /// <summary>小数位下拉数据源。</summary>
    public string[] DecimalBitOptions => bitArrayOptions;

    /// <summary>颜色下拉数据源。</summary>
    public string[] ColorOptionsTable => colorOptions;

    /// <summary>ASCII 显示是否可勾选（仅 DINT：32 位 = 4 字节 = 4 个字符）。</summary>
    public bool SettableAscii => TypeIndex == 1;

    /// <summary>小数位是否可改（ASCII 显示时字符没有小数点，改了解释不清）。</summary>
    public bool DecimalBitAvailable => !ShowAsAscii;

    partial void OnTypeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SettableAscii));

        if (value != 1 && ShowAsAscii)
        {
            // 切回 INT 时必须清掉 ASCII：否则"能存但显示不对"，比存不进去难查得多
            ShowAsAscii = false;
        }
    }

    partial void OnShowAsAsciiChanged(bool value) => OnPropertyChanged(nameof(DecimalBitAvailable));

    /// <summary>由配置项生成编辑行。</summary>
    public static HistoryDataItemEditRow FromModel(HistoryDataItem Item) => new()
    {
        Name = Item.Name,
        Address = Item.Address,
        TypeIndex = Item.DataType switch
        {
            TagDataType.Int32 => 1,
            TagDataType.Float32 => 2,
            _ => 0,
        },
        DecimalBitIndex = Math.Clamp(Item.DecimalBit, 0, HistoryRecordConfig.maxDecimalBit),
        Unit = Item.Unit ?? string.Empty,
        ShowAsAscii = Item.ShowAsAscii && Item.SettableAscii,
        ColorIndex = (int)Item.Color,
    };

    /// <summary>转回配置模型。</summary>
    public HistoryDataItem ConvertModel() => new()
    {
        Name = Name.Trim(),
        Address = Address.Trim(),
        DataType = TypeIndex switch
        {
            1 => TagDataType.Int32,
            2 => TagDataType.Float32,
            _ => TagDataType.Int16,
        },
        DecimalBit = Math.Clamp(DecimalBitIndex, 0, HistoryRecordConfig.maxDecimalBit),
        Unit = string.IsNullOrWhiteSpace(Unit) ? null : Unit.Trim(),
        // 双保险：界面已禁用非 DINT 的勾选，这里再挡一次（手改文件走的是同一条校验）
        ShowAsAscii = ShowAsAscii && TypeIndex == 1,
        Color = (HistoryColumnColor)Math.Clamp(ColorIndex, 0, 3),
    };
}

/// <summary>
/// 历史记录配置编辑器：改触发位、数据体名称、保留天数，并增删 / 修改数据项。
///
/// <para>只负责收集界面输入；<b>地址区域、类型与重叠校验在服务层</b>
/// （同一套规则必须同时作用于"界面改的"和"手改文件的"配置）。</para>
/// </summary>
public partial class HistoryConfigEditorDialog : Window
{
    private readonly IReadOnlyList<VariableTableEntry>? _variableList;
    private readonly ObservableCollection<HistoryDataItemEditRow> _rowCollection = new();

    private HistoryConfigEditorDialog(HistoryRecordConfig configItem, IReadOnlyList<VariableTableEntry>? VariableList)
    {
        InitializeComponent();

        _variableList = VariableList;

        NameFrame.Text = configItem.Title;
        TriggerBitFrame.Text = configItem.TriggerAddress;
        RetainDaysNumberFrame.Text = configItem.RetainDays.ToString(CultureInfo.InvariantCulture);
        EnabledFrame.IsChecked = configItem.Enabled;
        ResetFrame.IsChecked = configItem.ReadAfterResetTriggerBit;

        foreach (var Item in configItem.DataItemCollection)
        {
            _rowCollection.Add(HistoryDataItemEditRow.FromModel(Item));
        }

        DataItemList.ItemsSource = _rowCollection;
        RefreshDataItemSummary();

        Loaded += (_, _) => NameFrame.Focus();
    }

    /// <summary>弹出编辑器。确定时已就地修改 <paramref name="configItem"/>（含数据项集合）。</summary>
    public static bool Show(HistoryRecordConfig configItem, IReadOnlyList<VariableTableEntry>? VariableList = null)
    {
        var Dialog = new HistoryConfigEditorDialog(configItem, VariableList)
        {
            Owner = Application.Current.MainWindow,
        };

        if (Dialog.ShowDialog() != true)
        {
            return false;
        }

        configItem.Title = Dialog.NameFrame.Text.Trim();
        configItem.TriggerAddress = Dialog.TriggerBitFrame.Text.Trim();
        configItem.Enabled = Dialog.EnabledFrame.IsChecked ?? false;
        configItem.ReadAfterResetTriggerBit = Dialog.ResetFrame.IsChecked ?? false;
        configItem.RetainDays = int.TryParse(
            Dialog.RetainDaysNumberFrame.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var daysNumber)
            ? daysNumber
            : configItem.RetainDays;
        configItem.DataItemCollection = Dialog._rowCollection.Select(Row => Row.ConvertModel()).ToList();

        return true;
    }

    /// <summary>仅供自检使用：实例化但不显示（用于验证 XAML 资源能否解析）。</summary>
    internal static Window CreateVerify(HistoryRecordConfig configItem) => new HistoryConfigEditorDialog(configItem, null);

    // ── 数据项增删 ──────────────────────────────────────────────────────

    private void AddDataItem(object sender, RoutedEventArgs eventArgs)
    {
        if (_rowCollection.Count >= HistoryRecordConfig.maxDataItemCount)
        {
            MessageText.Text = $"数据项最多 {HistoryRecordConfig.maxDataItemCount} 个，请先删除不用的列。";
            return;
        }

        _rowCollection.Add(new HistoryDataItemEditRow { TypeIndex = 0, DecimalBitIndex = 0 });
        RefreshDataItemSummary();
        MessageText.Text = string.Empty;
    }

    private void DeleteDataItem(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not FrameworkElement { DataContext: HistoryDataItemEditRow Row })
        {
            return;
        }

        _rowCollection.Remove(Row);
        RefreshDataItemSummary();
    }

    // ── 变量绑定 ────────────────────────────────────────────────────────

    private void TriggerBitSelectVariable(object sender, RoutedEventArgs eventArgs)
    {
        if (VariableSelectDialog.Show(_variableList ?? [], "为触发位选择变量") is not { } Entry)
        {
            return;
        }

        TriggerBitFrame.Text = Entry.SoftElementAddress;
    }

    private void DataItemSelectVariable(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not FrameworkElement { DataContext: HistoryDataItemEditRow Row })
        {
            return;
        }

        var titleText = string.IsNullOrWhiteSpace(Row.Name) ? "为数据项选择变量" : $"为「{Row.Name}」选择变量";

        if (VariableSelectDialog.Show(_variableList ?? [], titleText) is not { } Entry)
        {
            return;
        }

        Row.Address = Entry.SoftElementAddress;

        // 变量自带类型时顺带对齐（INT / DINT / REAL 三种都能直接映射）
        Row.TypeIndex = Entry.DataType switch
        {
            TagDataType.Int32 => 1,
            TagDataType.Float32 => 2,
            TagDataType.Int16 => 0,
            _ => Row.TypeIndex,   // 其它类型（如 Bool / Float64）保持管理员选择，由服务层校验拦截
        };
    }

    // ── 确定 / 取消 ────────────────────────────────────────────────────

    private void Confirm(object sender, RoutedEventArgs eventArgs)
    {
        if (Validate() is { } error)
        {
            MessageText.Text = error;
            return;
        }

        DialogResult = true;
    }

    private void Cancel(object sender, RoutedEventArgs eventArgs) => DialogResult = false;

    private void RefreshDataItemSummary() =>
        DataItemSummaryText.Text =
            $"共 {_rowCollection.Count} 项（最多 {HistoryRecordConfig.maxDataItemCount} 项）· " +
            "INT/DINT 的小数位用于缩放显示：填 2 时 PLC 的 600 显示为 6.00；" +
            "REAL（32 位浮点）直接按工程量显示，小数位只决定保留几位；" +
            "DINT 项可勾选 ASCII 按字符显示（4 字节 = 4 个字符）；每列可选红 / 黄 / 绿着色";

    /// <summary>界面侧的基本必填检查；返回第一条错误，全部通过返回 <c>null</c>。</summary>
    private string? Validate()
    {
        if (string.IsNullOrWhiteSpace(NameFrame.Text))
        {
            return "请填写数据体名称。";
        }

        if (string.IsNullOrWhiteSpace(TriggerBitFrame.Text))
        {
            return "请填写触发位地址（M 区，如 M700）。";
        }

        if (_rowCollection.Count == 0)
        {
            return "至少需要一个数据项。";
        }

        for (var Sequence = 0; Sequence < _rowCollection.Count; Sequence++)
        {
            var Row = _rowCollection[Sequence];

            if (string.IsNullOrWhiteSpace(Row.Name))
            {
                return $"第 {Sequence + 1} 项：请填写名称。";
            }

            if (string.IsNullOrWhiteSpace(Row.Address))
            {
                return $"第 {Sequence + 1} 项「{Row.Name}」：请填写读取地址（字元件，如 D2109）。";
            }
        }

        if (!int.TryParse(RetainDaysNumberFrame.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var daysNumber) ||
            daysNumber is < HistoryRecordConfig.minRetainDaysNumber or > HistoryRecordConfig.maxRetainDaysNumber)
        {
            return $"保留天数请填 {HistoryRecordConfig.minRetainDaysNumber}–{HistoryRecordConfig.maxRetainDaysNumber} 之间的整数。";
        }

        return null;
    }
}
