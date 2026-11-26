using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PlcHmi.Core.Models;

namespace PlcHmi.App.ViewModels;

/// <summary>
/// IO 点位监控页的行视图模型（单点 / 分组）。
///
/// <para><b>为什么拆出来：</b>IoViewModel.cs 已超过 400 行规模门禁，
/// 行模型是独立的可复用单元（标定页的辅助点行也是同款结构）。</para>
/// </summary>
public sealed partial class IoPointViewModel : ObservableObject
{
    [ObservableProperty] private string _meaning;
    [ObservableProperty] private bool _isOpenStart;
    [ObservableProperty] private bool _isEdit;

    public IoPointViewModel(IoPoint modelPairObject)
    {
        Model = modelPairObject;
        Code = modelPairObject.Code;
        _meaning = modelPairObject.Meaning;
    }

    /// <summary>底层配置模型。</summary>
    public IoPoint Model { get; }

    /// <summary>点位编号，如 X0 / Y10。<b>X / Y 为八进制编号。</b></summary>
    public string Code { get; }

    /// <summary>是否启用（未启用时界面置灰占位）。</summary>
    public bool Enabled => Model.Enabled;

    /// <summary>是否反相（常闭信号）。</summary>
    public bool Negate => Model.Negate;

    /// <summary>是否已定义含义。</summary>
    public bool IsDefined => !string.IsNullOrWhiteSpace(Meaning);

    /// <summary>状态文字。</summary>
    public string StateText => IsOpenStart ? "ON" : "OFF";

    partial void OnMeaningChanged(string value)
    {
        Model.Meaning = value;
        OnPropertyChanged(nameof(IsDefined));
    }

    partial void OnIsOpenStartChanged(bool value) => OnPropertyChanged(nameof(StateText));
}

/// <summary>IO 点分组（按八进制，每 8 点一组）。</summary>
public sealed class IoGroupViewModel
{
    public IoGroupViewModel(string titleText, IEnumerable<IoPointViewModel> PointItemList)
    {
        Title = titleText;
        PointCollection = new ObservableCollection<IoPointViewModel>(PointItemList);
    }

    /// <summary>组标题，如 “X0 — X7”。</summary>
    public string Title { get; }

    /// <summary>组内点位。</summary>
    public ObservableCollection<IoPointViewModel> PointCollection { get; }

    /// <summary>已定义含义的点数描述。</summary>
    public string DefinedSummary
    {
        get
        {
            var defined = 0;

            // 单遍统计：曾经的两次枚举（Count(谓词) + Count）每个绑定刷新都遍历两轮
            for (var Sequence = 0; Sequence < PointCollection.Count; Sequence++)
            {
                if (PointCollection[Sequence].IsDefined)
                {
                    defined++;
                }
            }

            return $"{defined} / {PointCollection.Count} 已定义";
        }
    }
}
