using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using 上位机.业务.服务;
using 上位机.核心.抽象;

namespace 上位机.应用.视图;

/// <summary>
/// 弹窗设置对话框（仅管理员入口）。
///
/// <para>新增 / 编辑 / 删除可配置弹窗：每个弹窗有一个 M 区<b>触发地址</b>（闭合即显示、断开即消失）、
/// 正文文案，以及两颗按钮——各配名称、M 区地址与<b>工作模式</b>（复归型 / 置位 ON / 复位 OFF）。</para>
///
/// <para>只负责收集与校验输入；落盘由调用方经 弹窗监控服务 原子写入 popups.json。</para>
/// </summary>
public partial class 弹窗设置对话框 : Window
{
    /// <summary>一行的可编辑状态。</summary>
    public sealed partial class 弹窗行 : ObservableObject
    {
        private readonly 弹窗定义 _定义;

        [ObservableProperty] private string _名称 = string.Empty;
        [ObservableProperty] private string _触发地址 = string.Empty;
        [ObservableProperty] private string _内容 = string.Empty;
        [ObservableProperty] private string _按钮一名称 = string.Empty;
        [ObservableProperty] private string _按钮一地址 = string.Empty;
        [ObservableProperty] private string _按钮二名称 = string.Empty;
        [ObservableProperty] private string _按钮二地址 = string.Empty;

        /// <summary>按钮一模式索引（0 复归型 / 1 置位 ON / 2 复位 OFF）。</summary>
        [ObservableProperty] private int _按钮一模式索引;

        /// <summary>按钮二模式索引（同上）。</summary>
        [ObservableProperty] private int _按钮二模式索引;

        /// <summary>模式下拉数据源（与 <see cref="弹窗定义.模式选项"/> 同一份）。</summary>
        public IReadOnlyList<string> 模式选项 => 弹窗定义.模式选项;

        public 弹窗行(弹窗定义 定义)
        {
            _定义 = 定义;
            名称 = 定义.名称;
            触发地址 = 定义.触发地址;
            内容 = 定义.内容;
            按钮一名称 = 定义.按钮一名称;
            按钮一地址 = 定义.按钮一地址;
            按钮二名称 = 定义.按钮二名称;
            按钮二地址 = 定义.按钮二地址;
            按钮一模式索引 = (int)定义.按钮一模式;
            按钮二模式索引 = (int)定义.按钮二模式;
        }

        /// <summary>底层定义（回写用）。</summary>
        public 弹窗定义 定义 => _定义;
    }

    private readonly 地址解析器接口 _解析器;

    private 弹窗设置对话框(弹窗监控服务 监控, 地址解析器接口 解析器)
    {
        InitializeComponent();

        _解析器 = 解析器;

        foreach (var 定义 in 监控.弹窗集合)
        {
            弹窗集合.Add(new 弹窗行(定义));
        }

        DataContext = this;
    }

    /// <summary>可编辑行集合。</summary>
    public ObservableCollection<弹窗行> 弹窗集合 { get; } = new();

    /// <summary>
    /// 弹出编辑器。返回编辑后的弹窗定义集合；<c>null</c> = 取消（调用方据此决定是否落盘）。
    /// </summary>
    public static IReadOnlyList<弹窗定义>? 显示(弹窗监控服务 监控, 地址解析器接口 解析器)
    {
        var 对话框 = new 弹窗设置对话框(监控, 解析器)
        {
            Owner = Application.Current.MainWindow,
        };

        return 对话框.ShowDialog() == true ? 对话框.收集() : null;
    }

    /// <summary>仅供自检使用：实例化但不显示。</summary>
    internal static Window 创建验证(弹窗监控服务 监控, 地址解析器接口 解析器) =>
        new 弹窗设置对话框(监控, 解析器);

    private void 新增弹窗(object 发送者, RoutedEventArgs 事件参数)
    {
        var 序号 = 弹窗集合.Count + 1;

        弹窗集合.Add(new 弹窗行(new 弹窗定义
        {
            名称 = $"新弹窗{序号}",
            触发地址 = "M0",
            内容 = "请填写弹窗内容。",
            按钮一名称 = "确认",
            按钮一地址 = "M0",
            按钮一模式 = 弹窗按钮模式.复归型,
        }));
    }

    private void 删除弹窗(object 发送者, RoutedEventArgs 事件参数)
    {
        if (发送者 is FrameworkElement 元素 && 元素.DataContext is 弹窗行 行)
        {
            弹窗集合.Remove(行);
        }
    }

    private void 取消(object 发送者, RoutedEventArgs 事件参数) => DialogResult = false;

    private void 确认(object 发送者, RoutedEventArgs 事件参数)
    {
        foreach (var 行 in 弹窗集合)
        {
            if (string.IsNullOrWhiteSpace(行.名称))
            {
                消息文本.Text = "弹窗名称不能为空。";
                return;
            }

            if (校验(行.触发地址, $"「{行.名称}」的触发地址") is { } 触发问题)
            {
                消息文本.Text = 触发问题;
                return;
            }

            if (string.IsNullOrWhiteSpace(行.按钮一名称))
            {
                消息文本.Text = $"「{行.名称}」的按钮一名称不能为空。";
                return;
            }

            if (校验(行.按钮一地址, $"「{行.名称}」的按钮一地址") is { } 按钮一问题)
            {
                消息文本.Text = 按钮一问题;
                return;
            }

            // 按钮二：名称与地址要么都有、要么都空
            var 有二名称 = !string.IsNullOrWhiteSpace(行.按钮二名称);
            var 有二地址 = !string.IsNullOrWhiteSpace(行.按钮二地址);

            if (有二名称 != 有二地址)
            {
                消息文本.Text = $"「{行.名称}」的按钮二：名称与地址必须同时填写，或同时留空。";
                return;
            }

            if (有二地址 && 校验(行.按钮二地址, $"「{行.名称}」的按钮二地址") is { } 按钮二问题)
            {
                消息文本.Text = 按钮二问题;
                return;
            }

            // 触发地址与按钮地址互不相同：同一线圈既触发又"确认"会让弹窗按住期间反复触发
            if (同址(行.触发地址, 行.按钮一地址) || 同址(行.触发地址, 行.按钮二地址))
            {
                消息文本.Text = $"「{行.名称}」的按钮地址不能与触发地址相同。";
                return;
            }
        }

        DialogResult = true;
    }

    /// <summary>地址校验：空地址不算错（触发地址必填在校验顺序里单独管），非法/非 M 区给原因。</summary>
    private string? 校验(string 地址文本, string 用途)
    {
        if (string.IsNullOrWhiteSpace(地址文本))
        {
            return $"{用途}不能为空。";
        }

        return 弹窗监控服务.校验地址(_解析器, 地址文本, 用途);
    }

    private bool 同址(string 甲, string 乙) =>
        !string.IsNullOrWhiteSpace(乙)
        && string.Equals(甲.Trim(), 乙.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>把编辑结果回写成定义集合（确认后调用）。</summary>
    private IReadOnlyList<弹窗定义> 收集()
    {
        var 结果 = new List<弹窗定义>();

        foreach (var 行 in 弹窗集合)
        {
            行.定义.名称 = 行.名称.Trim();
            行.定义.触发地址 = 行.触发地址.Trim();
            行.定义.内容 = 行.内容.Trim();
            行.定义.按钮一名称 = 行.按钮一名称.Trim();
            行.定义.按钮一地址 = 行.按钮一地址.Trim();
            行.定义.按钮一模式 = (弹窗按钮模式)Math.Clamp(行.按钮一模式索引, 0, 弹窗定义.模式选项.Count - 1);
            行.定义.按钮二名称 = 行.按钮二名称.Trim();
            行.定义.按钮二地址 = 行.按钮二地址.Trim();
            行.定义.按钮二模式 = (弹窗按钮模式)Math.Clamp(行.按钮二模式索引, 0, 弹窗定义.模式选项.Count - 1);

            结果.Add(行.定义);
        }

        return 结果;
    }
}
