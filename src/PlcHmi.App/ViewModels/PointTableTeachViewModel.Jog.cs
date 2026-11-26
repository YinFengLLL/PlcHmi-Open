using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using 上位机.应用.服务;
using 上位机.核心.抽象;
using 上位机.核心.模型;

namespace 上位机.应用.视图模型;

/// <summary>
/// <see cref="点表示教视图模型"/> 的「档位与轴切换」部分（partial）。
///
/// <para>统一方向键盘（▲ 正向 / ▼ 负向 / ◀ ▶ 切换轴 / 中央档位）用的命令与状态。
/// 六轴共用同一个速度档位地址（recipe-config 的 点动速度等级 全部同址）。</para>
/// </summary>
public abstract partial class 点表示教视图模型
{
    // ── 档位与轴切换（统一方向键盘布局） ────────────────────────────────

    /// <summary>当前点动档位文本（低/中/高）。</summary>
    [ObservableProperty] private string _档位文本 = "低";

    private int _当前档位 = 0;

    /// <summary>
    /// 档位切换：低 → 中 → 高 → 低 循环，并写入速度档位寄存器。
    /// <para>现场要求（2026-10-13）：六轴<b>共用同一个</b>速度档位地址，
    /// 因此这里写哪个轴键都会落到同一个寄存器。</para>
    /// </summary>
    [RelayCommand]
    private async Task 异步切换档位()
    {
        // 写入值口径（2026-10-13）：低=0、中=1、高=2；循环 0 → 1 → 2 → 0
        _当前档位 = (_当前档位 + 1) % 3;
        档位文本 = _当前档位 switch { 1 => "中", 2 => "高", _ => "低" };

        if (选中轴 is not null)
        {
            await 界面命令.异步执行(_日志, _对话框, "切换点动档位",
                () => _手动.异步设置点动速度等级(选中轴.轴键, _当前档位));
        }
    }

    /// <summary>按方向切换选中轴（◀ 上一轴 / ▶ 下一轴，循环）。</summary>
    [RelayCommand]
    private void 切换轴(string 方向文本)
    {
        if (轴列表.Count == 0)
        {
            return;
        }

        var 当前序 = 轴列表.IndexOf(选中轴!);
        var 步进 = 方向文本 == "上一轴" ? -1 : 1;
        var 下一序 = ((当前序 + 步进) % 轴列表.Count + 轴列表.Count) % 轴列表.Count;

        选择轴(轴列表[下一序]);
    }

}
