using Microsoft.Extensions.Logging;
using PlcHmi.App.Services;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Models;

namespace PlcHmi.App.ViewModels;

/// <summary>
/// 点胶示教页 ViewModel。
///
/// <para>指令码 / X / Y / Z，最多 30 点（指令码 D3400 单字、X D2700、Y D2900、Z D3100，
/// 32 位浮点，每点步长 2 字）。</para>
///
/// <para><b>辅助地址区（2026-10-12 现场要求）</b>：原「动作指令」参数区（提前关胶 /
/// 运动速度延时 / 关胶后延时 / 抬针高度 / 点胶速度）已并入<b>辅助地址</b>——
/// 这些本来就是"一个名称 + 一个 PLC 地址 + 一个数值"的元件，走统一的辅助地址机制后：
/// 管理员可在地址编辑里改地址、改类型、增删条目，数值在页面右侧直接读写，
/// 不再需要一套专用的参数服务。默认的五个条目见 <c>recipe-config.json</c> 的 auxiliaryAddresses。</para>
///
/// <para>手动控制区只显示点胶 X/Y/Z（点胶 Y 与锁附 Y 共轴），由
/// <c>recipe-config.json</c> 的点动分组过滤得出。</para>
/// </summary>
public sealed partial class DispenseTeachViewModel : PointTableTeachViewModel
{
    public DispenseTeachViewModel(
        IPointTableService PointTableService,
        IPageAddressService PageAddressService,
        RecipeConfig configPairObject,
        IManualWidgetService Manual,
        IUserService User,
        ITagValueService ValueList,
        IDialogService Dialog,
        IPageContextService PageContext,
        ILogger<DispenseTeachViewModel> Log)
        // 点码写 D652：锁附与点胶两张点表各写各自的字（现场口径 2026-10-14）
        : base("点胶", PageContextRegister.dispenseTeachPointCode, PointTableService, PageAddressService, configPairObject, Manual, User, ValueList, Dialog, PageContext, Log)
    {
        Title = "点胶示教";
        Subtitle = "指令码（辅助地址区下拉选择）/ X / Y / Z · 最多 30 点 · 坐标 XXXX.XX（MM） · 地址编辑仅管理员";
    }
}
