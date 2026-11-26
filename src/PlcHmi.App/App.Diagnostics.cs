using Microsoft.Extensions.DependencyInjection;
using Serilog;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PlcHmi.App.Views;
using PlcHmi.App.ViewModels;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Models;

namespace PlcHmi.App;

/// <summary>
/// 应用程序的冒烟自检部分（<c>--smoke</c> 模式专用）。
///
/// <para>交互自检与对话框自检从入口文件拆出：入口只负责启动流程，自检逻辑集中在这里维护。</para>
/// </summary>
public partial class App
{
    /// <summary>
    /// 导航标题 → 期望的视图类型名。用于自检模式校验页面映射（<c>--smoke</c> 与 <c>--shots</c> 共用）。
    ///
    /// <para>用 <c>nameof</c> 而不是字面量：类型一旦改名，这里编译期就会跟着变，
    /// 不会出现"自检拿旧名字比对，全部页面报失败"的假警报。</para>
    ///
    /// <para>放在自检文件里而不是入口文件：它是自检/截图唯一的"页面清单真相"，
    /// 与入口的启动流程无关（入口文件已到规模上限）。</para>
    /// </summary>
    private static readonly Dictionary<string, string> expectedView = new()
    {
        ["运行监控"] = nameof(RunView),
        ["参数设置"] = nameof(ParameterView),
        ["锁附示教"] = nameof(ScrewTeachView),
        ["点胶示教"] = nameof(DispenseTeachView),
        ["标定 · 其它点"] = nameof(CalibrationView),
        ["机种管理"] = nameof(RecipeView),
        ["手动调试"] = nameof(ManualView),
        ["IO 点位"] = nameof(IoView),
        ["报警记录"] = nameof(AlarmView),
        ["历史记录"] = nameof(HistoryRecordView),
        ["系统设置"] = nameof(SettingsView),
    };

    /// <summary>
    /// 把导航标题转成合法文件名：替换 Windows 不允许的字符（<c>\ / : * ? " &lt; &gt; |</c>）。
    ///
    /// <para>页面标题是界面文案，随时可能带斜杠（如「标定 / 其它点」）——
    /// 直接拼进路径会抛 <c>DirectoryNotFoundException</c> 并把整个截图循环打断，
    /// 表现是"只出了前几张图，退出码却是 0"的假绿。</para>
    /// </summary>
    private static string ValidFileName(string Title)
    {
        var illegalCharacter = Path.GetInvalidFileNameChars();
        return new string(Title.Select(character => illegalCharacter.Contains(character) ? '_' : character).ToArray());
    }

    /// <summary>
    /// 交互自检：直接调用 ViewModel 的命令，验证状态是否按预期变化。
    ///
    /// <para>状态类 bug（“点了没反应”）静态看代码看不出来，只能实际调一次命令再断言结果。</para>
    /// </summary>
    private int VerifyCommitMutual()
    {
        if (_host is null)
        {
            return 0;
        }

        var Failed = 0;

        try
        {
            var Manual = _host.Services.GetRequiredService<ManualViewModel>();

            if (Manual.AxisList.Count < 2)
            {
                Log.Warning("交互自检跳过：轴数量不足");
                return 0;
            }

            var first = Manual.AxisList[0];
            var noTwo = Manual.AxisList[1];

            // 初始应选中第一个轴
            Manual.SelectAxisCommand.Execute(first);

            if (Manual.SelectedAxis != first || !first.IsSelected)
            {
                Failed++;
                Log.Error("交互自检失败：选中第一个轴后状态不对（SelectedAxis={Sel}, IsSelected={Flag}）",
                    Manual.SelectedAxis?.Name ?? "null", first.IsSelected);
            }
            else
            {
                Log.Information("交互自检通过：选中 {Name}", first.Name);
            }

            // 切到第二个轴：选中标志应转移，旧的应被清掉
            Manual.SelectAxisCommand.Execute(noTwo);

            if (Manual.SelectedAxis != noTwo)
            {
                Failed++;
                Log.Error("交互自检失败：切换后 SelectedAxis 仍是 {Sel}", Manual.SelectedAxis?.Name ?? "null");
            }
            else if (first.IsSelected)
            {
                Failed++;
                Log.Error("交互自检失败：切换后旧的轴 {Name} 仍处于选中态", first.Name);
            }
            else if (!noTwo.IsSelected)
            {
                Failed++;
                Log.Error("交互自检失败：切换后新轴 {Name} 未进入选中态", noTwo.Name);
            }
            else
            {
                Log.Information("交互自检通过：切换到 {Name}，旧轴已取消选中", noTwo.Name);
            }

            // 示教页的轴选择（用于点动）是同一类易错点，一并断言，防止“点了没反应”复发
            Failed += VerifyTeachAxisSelect();
        }
        catch (Exception exception)
        {
            Failed++;
            Log.Error(exception, "交互自检异常");
        }

        return Failed;
    }

    /// <summary>示教页轴选择断言（与手动页同一类易错点）。</summary>
    private int VerifyTeachAxisSelect()
    {
        var Teach = _host!.Services.GetRequiredService<ScrewTeachViewModel>();

        if (Teach.AxisList.Count < 2)
        {
            Log.Warning("交互自检跳过：示教页轴数量不足");
            return 0;
        }

        var Failed = 0;
        var teachFirst = Teach.AxisList[0];
        var teachNoTwo = Teach.AxisList[1];

        Teach.SelectAxisCommand.Execute(teachNoTwo);

        if (Teach.SelectedAxis != teachNoTwo)
        {
            Failed++;
            Log.Error("交互自检失败：示教页切换轴后 SelectedAxis 仍是 {Sel}", Teach.SelectedAxis?.Name ?? "null");
        }
        else if (teachFirst.IsSelected)
        {
            Failed++;
            Log.Error("交互自检失败：示教页切换后旧轴 {Name} 仍处于选中态", teachFirst.Name);
        }
        else if (!teachNoTwo.IsSelected)
        {
            Failed++;
            Log.Error("交互自检失败：示教页切换后新轴 {Name} 未进入选中态", teachNoTwo.Name);
        }
        else
        {
            Log.Information("交互自检通过：示教页切换到 {Name}，旧轴已取消选中", teachNoTwo.Name);
        }

        return Failed;
    }

    /// <summary>
    /// 对话框自检：实例化但不显示。
    ///
    /// <para><b>为什么需要这个</b>：XAML 里的 <c>{StaticResource}</c> 在加载时求值，
    /// 写错键名会当场抛 <c>StaticResourceExtension</c> 异常。
    /// 但对话框不在导航里，<c>--smoke</c> 遍历页面时碰不到它们——
    /// 结果就是“程序能启动、8 页全通过，一点「切换身份」就崩”。
    /// 这里把每个对话框都 new 一次，把这类问题提前暴露出来。</para>
    /// </summary>
    private int VerifyDialog()
    {
        if (_host is null)
        {
            return 0;
        }

        var User = _host.Services.GetRequiredService<IUserService>();

        var testCase = new (string Name, Func<Window> Factory)[]
        {
            ("登录框", () => PlcHmi.App.Views.LoginDialog.CreateVerify(User)),
            ("操作确认框", () => PlcHmi.App.Views.ConfirmDialog.CreateVerify()),
            ("手动页元件编辑器", () => PlcHmi.App.Views.ComponentEditorDialog.CreateVerify(
                new ManualComponentDefinition
                {
                    Id = "verify",
                    Name = "自检元件",
                    Kind = ManualComponentType.Cylinder,
                    Address = "M220",
                })),
            ("报警定义编辑器", () => PlcHmi.App.Views.AlarmEditorDialog.CreateVerify(
                new AlarmDefinition { Id = "V001", Message = "自检报警", Address = "M500", Bit = -1 })),
            ("变量选择对话框", () => PlcHmi.App.Views.VariableSelectDialog.CreateVerify(
                new List<VariableTableEntry>
                {
                    new() { VariableName = "自检变量.b_示例", SoftElementAddress = "M900", DataType = PlcHmi.Core.Enums.TagDataType.Boolean },
                })),
            ("弹窗设置", () => PlcHmi.App.Views.PopupSettingsDialog.CreateVerify(
                _host.Services.GetRequiredService<PlcHmi.Business.Services.PopupMonitorService>(),
                _host.Services.GetRequiredService<IAddressParser>())),
            ("可配置弹窗", () => PlcHmi.App.Views.PopupWindow.CreateVerify(
                _host.Services.GetRequiredService<PopupHostViewModel>())),
            ("历史记录配置编辑器", () => PlcHmi.App.Views.HistoryConfigEditorDialog.CreateVerify(
                new HistoryRecordConfig
                {
                    Title = "自检数据体",
                    TriggerAddress = "M700",
                    DataItemCollection =
                    [
                        new HistoryDataItem
                        {
                            Name = "实际扭力",
                            Address = "D2109",
                            DataType = PlcHmi.Core.Enums.TagDataType.Int32,
                            DecimalBit = 2,
                            Unit = "N·m",
                        },
                    ],
                })),
            ("地址编辑器", () => PlcHmi.App.Views.AddressEditDialog.CreateVerify()),
            ("其它点编辑器", () => PlcHmi.App.Views.AuxPointEditDialog.CreateVerify()),
        };

        var Failed = 0;

        foreach (var (nameValue, workFactory) in testCase)
        {
            try
            {
                var Dialog = workFactory();

                // 触发模板应用，确保资源与绑定都能解析
                Dialog.Measure(new System.Windows.Size(900, 700));
                Dialog.Close();

                Log.Information("对话框自检通过：{Name}", nameValue);
            }
            catch (Exception exception)
            {
                Failed++;
                Log.Error(exception, "对话框自检失败：{Name}", nameValue);
            }
        }

        return Failed;
    }
}
