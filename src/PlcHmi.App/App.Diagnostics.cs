using Microsoft.Extensions.DependencyInjection;
using Serilog;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using 上位机.应用.视图;
using 上位机.应用.视图模型;
using 上位机.核心.抽象;
using 上位机.核心.模型;

namespace 上位机.应用;

/// <summary>
/// 应用程序的冒烟自检部分（<c>--smoke</c> 模式专用）。
///
/// <para>交互自检与对话框自检从入口文件拆出：入口只负责启动流程，自检逻辑集中在这里维护。</para>
/// </summary>
public partial class 应用
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
    private static readonly Dictionary<string, string> 期望视图 = new()
    {
        ["运行监控"] = nameof(运行视图),
        ["参数设置"] = nameof(参数视图),
        ["锁附示教"] = nameof(锁附示教视图),
        ["点胶示教"] = nameof(点胶示教视图),
        ["标定 · 其它点"] = nameof(标定视图),
        ["机种管理"] = nameof(配方视图),
        ["手动调试"] = nameof(手动视图),
        ["IO 点位"] = nameof(IO视图),
        ["报警记录"] = nameof(报警视图),
        ["历史记录"] = nameof(历史记录视图),
        ["系统设置"] = nameof(设置视图),
    };

    /// <summary>
    /// 把导航标题转成合法文件名：替换 Windows 不允许的字符（<c>\ / : * ? " &lt; &gt; |</c>）。
    ///
    /// <para>页面标题是界面文案，随时可能带斜杠（如「标定 / 其它点」）——
    /// 直接拼进路径会抛 <c>DirectoryNotFoundException</c> 并把整个截图循环打断，
    /// 表现是"只出了前几张图，退出码却是 0"的假绿。</para>
    /// </summary>
    private static string 合法文件名(string 标题)
    {
        var 非法字符 = Path.GetInvalidFileNameChars();
        return new string(标题.Select(字符 => 非法字符.Contains(字符) ? '_' : 字符).ToArray());
    }

    /// <summary>
    /// 交互自检：直接调用 ViewModel 的命令，验证状态是否按预期变化。
    ///
    /// <para>状态类 bug（“点了没反应”）静态看代码看不出来，只能实际调一次命令再断言结果。</para>
    /// </summary>
    private int 验证交互()
    {
        if (_主机 is null)
        {
            return 0;
        }

        var 失败 = 0;

        try
        {
            var 手动 = _主机.Services.GetRequiredService<手动视图模型>();

            if (手动.轴列表.Count < 2)
            {
                Log.Warning("交互自检跳过：轴数量不足");
                return 0;
            }

            var 首个 = 手动.轴列表[0];
            var 第二 = 手动.轴列表[1];

            // 初始应选中第一个轴
            手动.选择轴Command.Execute(首个);

            if (手动.选中轴 != 首个 || !首个.是否选中)
            {
                失败++;
                Log.Error("交互自检失败：选中第一个轴后状态不对（SelectedAxis={Sel}, IsSelected={Flag}）",
                    手动.选中轴?.名称 ?? "null", 首个.是否选中);
            }
            else
            {
                Log.Information("交互自检通过：选中 {Name}", 首个.名称);
            }

            // 切到第二个轴：选中标志应转移，旧的应被清掉
            手动.选择轴Command.Execute(第二);

            if (手动.选中轴 != 第二)
            {
                失败++;
                Log.Error("交互自检失败：切换后 SelectedAxis 仍是 {Sel}", 手动.选中轴?.名称 ?? "null");
            }
            else if (首个.是否选中)
            {
                失败++;
                Log.Error("交互自检失败：切换后旧的轴 {Name} 仍处于选中态", 首个.名称);
            }
            else if (!第二.是否选中)
            {
                失败++;
                Log.Error("交互自检失败：切换后新轴 {Name} 未进入选中态", 第二.名称);
            }
            else
            {
                Log.Information("交互自检通过：切换到 {Name}，旧轴已取消选中", 第二.名称);
            }

            // 示教页的轴选择（用于点动）是同一类易错点，一并断言，防止“点了没反应”复发
            失败 += 验证示教轴选择();
        }
        catch (Exception 异常)
        {
            失败++;
            Log.Error(异常, "交互自检异常");
        }

        return 失败;
    }

    /// <summary>示教页轴选择断言（与手动页同一类易错点）。</summary>
    private int 验证示教轴选择()
    {
        var 示教 = _主机!.Services.GetRequiredService<锁附示教视图模型>();

        if (示教.轴列表.Count < 2)
        {
            Log.Warning("交互自检跳过：示教页轴数量不足");
            return 0;
        }

        var 失败 = 0;
        var 示教首个 = 示教.轴列表[0];
        var 示教第二 = 示教.轴列表[1];

        示教.选择轴Command.Execute(示教第二);

        if (示教.选中轴 != 示教第二)
        {
            失败++;
            Log.Error("交互自检失败：示教页切换轴后 SelectedAxis 仍是 {Sel}", 示教.选中轴?.名称 ?? "null");
        }
        else if (示教首个.是否选中)
        {
            失败++;
            Log.Error("交互自检失败：示教页切换后旧轴 {Name} 仍处于选中态", 示教首个.名称);
        }
        else if (!示教第二.是否选中)
        {
            失败++;
            Log.Error("交互自检失败：示教页切换后新轴 {Name} 未进入选中态", 示教第二.名称);
        }
        else
        {
            Log.Information("交互自检通过：示教页切换到 {Name}，旧轴已取消选中", 示教第二.名称);
        }

        return 失败;
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
    private int 验证对话框()
    {
        if (_主机 is null)
        {
            return 0;
        }

        var 用户 = _主机.Services.GetRequiredService<用户服务接口>();

        var 用例 = new (string 名称, Func<Window> Factory)[]
        {
            ("登录框", () => 上位机.应用.视图.登录对话框.创建验证(用户)),
            ("操作确认框", () => 上位机.应用.视图.确认对话框.创建验证()),
            ("手动页元件编辑器", () => 上位机.应用.视图.元件编辑器对话框.创建验证(
                new 手动元件定义
                {
                    标识 = "verify",
                    名称 = "自检元件",
                    类型 = 手动元件类型.气缸,
                    地址 = "M220",
                })),
            ("报警定义编辑器", () => 上位机.应用.视图.报警编辑器对话框.创建验证(
                new 报警定义 { 标识 = "V001", 消息 = "自检报警", 地址 = "M500", 位 = -1 })),
            ("变量选择对话框", () => 上位机.应用.视图.变量选择对话框.创建验证(
                new List<变量表条目>
                {
                    new() { 变量名 = "自检变量.b_示例", 软元件地址 = "M900", 数据类型 = 上位机.核心.枚举.标签数据类型.布尔 },
                })),
            ("弹窗设置", () => 上位机.应用.视图.弹窗设置对话框.创建验证(
                _主机.Services.GetRequiredService<上位机.业务.服务.弹窗监控服务>(),
                _主机.Services.GetRequiredService<地址解析器接口>())),
            ("可配置弹窗", () => 上位机.应用.视图.弹窗窗口.创建验证(
                _主机.Services.GetRequiredService<弹窗宿主视图模型>())),
            ("历史记录配置编辑器", () => 上位机.应用.视图.历史配置编辑器对话框.创建验证(
                new 历史记录配置
                {
                    标题 = "自检数据体",
                    触发地址 = "M700",
                    数据项集合 =
                    [
                        new 历史数据项
                        {
                            名称 = "实际扭力",
                            地址 = "D2109",
                            数据类型 = 上位机.核心.枚举.标签数据类型.Int32,
                            小数位 = 2,
                            单元 = "N·m",
                        },
                    ],
                })),
            ("地址编辑器", () => 上位机.应用.视图.地址编辑对话框.创建验证()),
            ("其它点编辑器", () => 上位机.应用.视图.辅助点编辑对话框.创建验证()),
        };

        var 失败 = 0;

        foreach (var (名称值, 工厂) in 用例)
        {
            try
            {
                var 对话框 = 工厂();

                // 触发模板应用，确保资源与绑定都能解析
                对话框.Measure(new System.Windows.Size(900, 700));
                对话框.Close();

                Log.Information("对话框自检通过：{Name}", 名称值);
            }
            catch (Exception 异常)
            {
                失败++;
                Log.Error(异常, "对话框自检失败：{Name}", 名称值);
            }
        }

        return 失败;
    }
}
