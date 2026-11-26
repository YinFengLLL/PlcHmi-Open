using Microsoft.Extensions.Logging;
using PlcHmi.App.Services;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Models;

namespace PlcHmi.App.ViewModels;

/// <summary>
/// 锁附示教页 ViewModel。
///
/// <para>锁附 X / Y / 浮高，最多 30 点（X 基址 D2500、Y 基址 D2560、浮高 D2620，
/// 32 位浮点，每点步长 2 字）。</para>
///
/// <para>手动控制区只显示锁附 X/Y/Z/ZZ；动作按钮与辅助地址（拍照点、上料点、
/// ZZ 轴设置、浮高开关…）由配置按分组驱动，地址均由管理员在「编辑地址」中维护。</para>
/// </summary>
public sealed partial class ScrewTeachViewModel : PointTableTeachViewModel
{
    public ScrewTeachViewModel(
        IPointTableService PointTableService,
        IPageAddressService PageAddressService,
        RecipeConfig configPairObject,
        IManualWidgetService Manual,
        IUserService User,
        ITagValueService ValueList,
        IDialogService Dialog,
        IPageContextService PageContext,
        ILogger<ScrewTeachViewModel> Log)
        // 点码写 D651：锁附与点胶两张点表各写各自的字（现场口径 2026-10-14）
        : base("锁附", PageContextRegister.screwTeachPointCode, PointTableService, PageAddressService, configPairObject, Manual, User, ValueList, Dialog, PageContext, Log)
    {
        Title = "锁附示教";
        Subtitle = "锁附 X / Y / 浮高 · 最多 30 点 · 坐标 XXXX.XX（MM） · 地址编辑仅管理员";
    }
}
