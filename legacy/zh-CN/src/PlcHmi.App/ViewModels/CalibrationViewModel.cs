using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using 上位机.应用.服务;
using 上位机.核心.抽象;
using 上位机.核心.模型;

namespace 上位机.应用.视图模型;

/// <summary>其它点列表里的一行（一个辅助点位）。</summary>
public sealed partial class 辅助点行视图模型 : ObservableObject
{
    private readonly 辅助点服务接口 _辅助点;
    private readonly Func<int, Task>? _选中回调;

    [ObservableProperty] private string _X文本 = "--";
    [ObservableProperty] private string _Y文本 = "--";
    [ObservableProperty] private string _Z文本 = "--";
    [ObservableProperty] private bool _是否当前;

    public 辅助点行视图模型(辅助点定义 定义项, int 序号值, 辅助点服务接口 辅助点, Func<int, Task>? 选中回调 = null)
    {
        定义 = 定义项;
        序号 = 序号值;
        _辅助点 = 辅助点;
        _选中回调 = 选中回调;
    }

    /// <summary>
    /// 点码（<b>0 基</b>）：在其它点定义里的固定次序，选中后写 PLC 的 D653。
    /// <para>按名字在<b>全量列表</b>里定位得出，不随查询过滤而变——否则同一个点在不同筛选下
    /// 会报出两个点码，PLC 侧拿到的一会儿是这个点、一会儿是那个点。</para>
    /// </summary>
    public int 序号 { get; }

    /// <summary>界面显示的点号（1 基，两位）。</summary>
    public string 标签 => (序号 + 1).ToString("00");

    /// <summary>底层定义。</summary>
    public 辅助点定义 定义 { get; }

    /// <summary>名称。</summary>
    public string 名称 => 定义.名称;

    /// <summary>地址文本（只读展示）。</summary>
    public string 地址文本 => $"D{定义.X基址} / D{定义.Y基址} / D{定义.Z基址}";

    /// <summary>从 PLC 读取坐标。</summary>
    public async Task 异步读取()
    {
        var (X, Y, Z) = await _辅助点.异步读取坐标(定义).ConfigureAwait(false);
        X文本 = 坐标辅助.格式化(X);
        Y文本 = 坐标辅助.格式化(Y);
        Z文本 = 坐标辅助.格式化(Z);
    }

    /// <summary>把坐标写入 PLC。</summary>
    public async Task 异步写入()
    {
        if (!坐标辅助.尝试解析(X文本, out var X)
            || !坐标辅助.尝试解析(Y文本, out var Y)
            || !坐标辅助.尝试解析(Z文本, out var Z))
        {
            throw new InvalidOperationException($"{名称} 的坐标不是有效数字。");
        }

        await _辅助点.异步写入坐标(定义, X, Y, Z).ConfigureAwait(false);
    }

    /// <summary>点击该行空白处：选中这个点（并把点码写进 D653）。</summary>
    [RelayCommand]
    private Task 选中() =>
        是否当前 || _选中回调 is null ? Task.CompletedTask : _选中回调(序号);
}

/// <summary>
/// 标定 / 其它点页 ViewModel（上下一页，2026-10-15 起不再分 Tab）。
///
/// <para><b>上半：标定示教</b>——标定中心坐标 X/Y/Z、每点偏移量、标定启动，
/// 锁附 X/Y/Z 轴实时位置，以及控制这三个轴的手动按钮（X±/Y±/Z±）。</para>
///
/// <para><b>下半：其它点坐标</b>——辅助点位（滑螺丝点、相机中心…）的增删改查与 CSV 导入导出，
/// 点行可选中并把点码写进 PLC 的 D653，见 <c>CalibrationViewModel.AuxPoints.cs</c>。</para>
/// </summary>
public sealed partial class 标定视图模型 : 视图模型基类, 动作按钮页接口
{
    private readonly 标定服务接口 _标定;
    private readonly 辅助点服务接口 _辅助点;
    private readonly 页面地址服务接口 _页面地址;
    private readonly 手动控件服务接口 _手动;
    private readonly 用户服务接口 _用户项;
    private readonly 对话框服务接口 _对话框;
    private readonly 文件对话框服务接口 _文件对话框;
    private readonly 页面上下文服务接口 _页面上下文;
    private readonly 配方配置 _配置;
    private readonly 标签值服务接口 _值列表;
    private readonly ILogger<标定视图模型> _日志;

    /// <summary>辅助地址「实时读写」的统一实现（2026-11-12）：填完即写 + 周期回读 + 手动使能拦截。</summary>
    private readonly 辅助地址实时写入手柄 _辅助地址写入;

    // ── 上相机标定 ──────────────────────────────────────────────────────
    [ObservableProperty] private string _中心X = "0.00";
    [ObservableProperty] private string _中心Y = "0.00";
    [ObservableProperty] private string _中心Z = "0.00";
    [ObservableProperty] private string _偏移量 = "0.00";

    // ── 下相机标定（2026-10-17 新增；**没有 Y**，只有 X / Z）─────────────
    [ObservableProperty] private string _下相机X = "0.00";
    [ObservableProperty] private string _下相机Z = "0.00";
    [ObservableProperty] private string _下相机偏移量 = "0.00";
    [ObservableProperty] private string _查询关键字 = string.Empty;
    [ObservableProperty] private bool _可编辑地址;
    [ObservableProperty] private bool _是否手动启用;

    public 标定视图模型(
        标定服务接口 标定,
        辅助点服务接口 辅助点,
        页面地址服务接口 页面地址,
        手动控件服务接口 手动,
        用户服务接口 用户,
        对话框服务接口 对话框,
        文件对话框服务接口 文件对话框,
        页面上下文服务接口 页面上下文,
        配方配置 配置对象,
        标签值服务接口 值列表,
        ILogger<标定视图模型> 日志)
    {
        _标定 = 标定;
        _辅助点 = 辅助点;
        _页面地址 = 页面地址;
        _手动 = 手动;
        _用户项 = 用户;
        _对话框 = 对话框;
        _文件对话框 = 文件对话框;
        _页面上下文 = 页面上下文;
        _配置 = 配置对象;
        _值列表 = 值列表;
        _日志 = 日志;

        // 辅助地址实时读写（2026-11-12）：写入门槛 = 本页手动使能
        _辅助地址写入 = new 辅助地址实时写入手柄(日志, 对话框, () => 是否手动启用);

        标题 = "标定 / 其它点";
        副标题 = "标定示教 + 其它点坐标（同页操作 · 选中其它点即写 PLC 的 D653 点码）";

        _辅助点.已变更 += (_, _) => 在界面线程执行(刷新辅助点);
        _手动.启用已变更 += (_, 启用) => 在界面线程执行(() => 是否手动启用 = 启用);
        _用户项.用户已变更 += (_, _) => 在界面线程执行(刷新权限);
        _值列表.新值已变更 += (_, 值) => 在界面线程执行(() => 标签已变更(值));

        是否手动启用 = _手动.是否启用;
        刷新权限();
        重建元件区();
        刷新辅助点();
    }

    /// <summary>其它点行集合。</summary>
    public 批量可观察集合<辅助点行视图模型> 辅助点集合 { get; } = new();

    // ── 初始化 ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    public override async Task 异步初始化()
    {
        刷新权限();
        重建元件区();
        重建轴列表();
        await 异步读取辅助地址().ConfigureAwait(false);
        await 异步同步动作按钮状态().ConfigureAwait(false);

        var (X, Y, Z) = await _标定.异步读取中心点().ConfigureAwait(false);
        var 偏移 = await _标定.异步读取偏移量().ConfigureAwait(false);
        var (下相机X值, 下相机Z值) = await _标定.异步读取下相机中心点().ConfigureAwait(false);
        var 下相机偏移 = await _标定.异步读取下相机偏移量().ConfigureAwait(false);

        在界面线程执行(() =>
        {
            中心X = 坐标辅助.格式化(X);
            中心Y = 坐标辅助.格式化(Y);
            中心Z = 坐标辅助.格式化(Z);
            偏移量 = 坐标辅助.格式化(偏移);

            下相机X = 坐标辅助.格式化(下相机X值);
            下相机Z = 坐标辅助.格式化(下相机Z值);
            下相机偏移量 = 坐标辅助.格式化(下相机偏移);
        });

        await 异步读取全部辅助点().ConfigureAwait(false);
        await 异步同步其它点点码().ConfigureAwait(false);

        在界面线程执行(刷新轴位置快照);
    }

    // ── 标定（上相机 / 下相机的写入与启动）：见 CalibrationViewModel.Camera.cs ──

    // ── 地址编辑（仅管理员） ────────────────────────────────────────────

    /// <summary>
    /// 编辑标定页所有与 PLC 交互的元件地址：中心点坐标、每点偏移量、标定启动，
    /// 以及锁附 X/Y/Z 的点动与回零线圈。
    /// </summary>
    [RelayCommand]
    private void 异步编辑地址()
    {
        if (!可编辑地址)
        {
            _对话框.提示("权限不足", "仅管理员可编辑地址，请联系设备管理员。");
            return;
        }

        异步安全调用.即发即忘(异步编辑地址核心, _日志, "编辑标定页地址");
    }

    private async Task 异步编辑地址核心()
    {
        var 请求 = new 地址编辑请求
        {
            标题 = "标定 / 其它点",
            默认分组 = "标定",
            项集合 = _页面地址.收集标定页().ToList(),
        };

        if (!_对话框.编辑页面地址(请求))
        {
            return;
        }

        是否忙碌 = true;

        try
        {
            if (await 界面命令
                    .异步执行(_日志, _对话框, "保存", () => _页面地址.异步保存标定页(请求))
                    .ConfigureAwait(true))
            {
                在界面线程执行(() =>
                {
                    重建元件区();
                    _对话框.提示("已保存", "标定页元件地址已保存并立即生效。");
                });
            }
        }
        finally
        {
            是否忙碌 = false;
        }
    }

    // ── 档位切换（统一方向键盘中央按钮） ────────────────────────────────

    /// <summary>当前点动档位文本（低/中/高）。</summary>
    [ObservableProperty] private string _档位文本 = "低";

    private int _当前档位 = 0;

    /// <summary>
    /// 档位切换：低 → 中 → 高 → 低 循环，写入值 0/1/2。
    /// <para>六轴共用同一个速度档位地址（2026-10-13 现场要求），写哪个轴键都落到同一寄存器。</para>
    /// </summary>
    [RelayCommand]
    private async Task 异步切换标定档位()
    {
        _当前档位 = (_当前档位 + 1) % 3;
        档位文本 = _当前档位 switch { 1 => "中", 2 => "高", _ => "低" };

        await 界面命令.异步执行(_日志, _对话框, "切换点动档位",
            () => _手动.异步设置点动速度等级("X", _当前档位));
    }

    // ── 标定页手动点动（X / Y / Z） ─────────────────────────────────────

    /// <summary>按住方向按钮：启动点动。方向键形如 <c>X+</c> / <c>Z-</c>。</summary>
    public void 开始标定点动(string 方向键)
    {
        if (方向键.Length < 2)
        {
            return;
        }

        var 轴键 = 方向键[..1];
        var 方向 = 方向键[^1] == '-' ? -1 : 1;

        异步安全调用.即发即忘(
            () => 异步标定点动核心(轴键, 方向),
            _日志,
            $"{轴键} 轴标定点动");
    }

    /// <summary>松开方向按钮：停止点动（无条件生效）。</summary>
    public void 结束标定点动()
    {
        异步安全调用.即发即忘(
            async () =>
            {
                await _手动.异步停止点动轴("X").ConfigureAwait(false);
                await _手动.异步停止点动轴("Y").ConfigureAwait(false);
                await _手动.异步停止点动轴("Z").ConfigureAwait(false);
            },
            _日志,
            "标定页停止点动");
    }

    private async Task 异步标定点动核心(string 轴键, int 方向)
    {
        if (!是否手动启用)
        {
            _对话框.提示("操作被拒绝", "请先开启手动使能，再执行点动。");
            return;
        }

        await 界面命令
            .异步执行(_日志, _对话框, $"{轴键} 点动", () => _手动.异步点动轴(轴键, 方向))
            .ConfigureAwait(true);
    }

    // ── 刷新 ────────────────────────────────────────────────────────────

    private void 刷新权限() => 可编辑地址 = _用户项.有(权限.编辑绑定地址);

    private void 重载辅助点(IReadOnlyList<辅助点定义> 列表)
    {
        // 选中跟随"点名"而不是序号：列表重建（新增 / 删除 / 改名 / 换查询关键字）后序号会整体挪位。
        // 点名仍在全量列表里就保持选中（哪怕当前被查询过滤掉了），被删掉才回到未选中。
        var 选中名 = 辅助点集合.FirstOrDefault(行项 => 行项.是否当前)?.名称;

        // 点码按"全量列表里的次序"算，不随查询过滤变动——否则同一个点
        // 在不同筛选下会报出两个点码，PLC 侧拿到的一会儿这个点一会儿那个点
        辅助点集合.替换全部(列表.Select(项 =>
            new 辅助点行视图模型(项, 序号Of(项.名称), _辅助点, 选中其它点)));

        选中其它点序号 = 选中名 is null ? -1 : 序号Of(选中名);
        刷新选中态();
    }
}
