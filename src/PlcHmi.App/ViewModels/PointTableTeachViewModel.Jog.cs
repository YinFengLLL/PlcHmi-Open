using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PlcHmi.App.Services;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Models;

namespace PlcHmi.App.ViewModels;

/// <summary>
/// <see cref="PointTableTeachViewModel"/> 的「档位与轴切换」部分（partial）。
///
/// <para>统一方向键盘（▲ 正向 / ▼ 负向 / ◀ ▶ 切换轴 / 中央档位）用的命令与状态。
/// 六轴共用同一个速度档位地址（recipe-config 的 点动速度等级 全部同址）。</para>
/// </summary>
public abstract partial class PointTableTeachViewModel
{
    // ── 档位与轴切换（统一方向键盘布局） ────────────────────────────────

    /// <summary>当前点动档位文本（低/中/高）。</summary>
    [ObservableProperty] private string _gearText = "低";

    private int _currentGear = 0;

    /// <summary>
    /// 档位切换：低 → 中 → 高 → 低 循环，并写入速度档位寄存器。
    /// <para>现场要求（2026-10-13）：六轴<b>共用同一个</b>速度档位地址，
    /// 因此这里写哪个轴键都会落到同一个寄存器。</para>
    /// </summary>
    [RelayCommand]
    private async Task SwitchGearAsync()
    {
        // 写入值口径（2026-10-13）：低=0、中=1、高=2；循环 0 → 1 → 2 → 0
        _currentGear = (_currentGear + 1) % 3;
        GearText = _currentGear switch { 1 => "中", 2 => "高", _ => "低" };

        if (SelectedAxis is not null)
        {
            await UiCommand.ExecuteAsync(_log, _dialog, "切换点动档位",
                () => _manual.SetJogSpeedLevelAsync(SelectedAxis.AxisKey, _currentGear));
        }
    }

    /// <summary>按方向切换选中轴（◀ 上一轴 / ▶ 下一轴，循环）。</summary>
    [RelayCommand]
    private void SwitchAxis(string directionText)
    {
        if (AxisList.Count == 0)
        {
            return;
        }

        var currentIndex = AxisList.IndexOf(SelectedAxis!);
        var stepEnter = directionText == "上一轴" ? -1 : 1;
        var nextIndex = ((currentIndex + stepEnter) % AxisList.Count + AxisList.Count) % AxisList.Count;

        SelectAxis(AxisList[nextIndex]);
    }

}
