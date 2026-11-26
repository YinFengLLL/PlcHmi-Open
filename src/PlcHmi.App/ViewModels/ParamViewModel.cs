using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using 上位机.应用.服务;
using 上位机.业务.服务;
using 上位机.核心.抽象;
using 上位机.核心.模型;

namespace 上位机.应用.视图模型;

/// <summary>参数值读取的轻量访问器（避免页面直接依赖业务内部类型）。</summary>
public sealed class 标签值服务访问器
{
    private readonly 标签值服务接口 _值列表;

    public 标签值服务访问器(标签值服务接口 值列表) => _值列表 = 值列表;

    public bool 尝试获取(string 名称值, out double 值) => _值列表.尝试获取双精度(名称值, out 值);
}

/// <summary>延时表 / 按钮表的一行。</summary>
public sealed partial class 参数条目行视图模型 : ObservableObject
{
    private readonly 参数表格服务接口 _表格;
    private readonly 参数表格类别 _类别;
    private readonly bool _变量寻址;
    private bool _加载中的值;

    [ObservableProperty] private bool _是否启用 = true;
    [ObservableProperty] private string _值文本 = "0.000";
    [ObservableProperty] private string _按钮状态文本 = "—";

    public 参数条目行视图模型(
        参数条目定义 定义项, 参数表格类别 类别, 参数表格服务接口 表格, bool 变量寻址 = false)
    {
        定义 = 定义项;
        _类别 = 类别;
        _表格 = 表格;
        _变量寻址 = 变量寻址;

        _是否启用 = 定义项.启用;
        _值文本 = 定义项.值.ToString("0.000");
    }

    /// <summary>底层定义（编辑用）。</summary>
    public 参数条目定义 定义 { get; }

    public string 标识 => 定义.标识;
    public string 名称 => 定义.名称;

    /// <summary>地址列显示：OPC UA 模式且已绑定变量时显示变量名（物理地址无关）。</summary>
    public string 地址 => 绑定显示.地址文本(_变量寻址, 定义.变量, 定义.地址);

    /// <summary>取值显示（延时按秒，格式 XX.XXX S；PLC 的 D 区存毫秒整数，见模型注释）。</summary>
    public string 新值显示 => _类别 == 参数表格类别.延迟
        ? $"{定义.值:0.000} S"
        : "—";

    /// <summary>是否显示“当前值”列（按钮表不显示）。</summary>
    public bool 显示新值 => _类别 == 参数表格类别.延迟;

    /// <summary>是否为按钮行（显示置位 / 复位操作与状态）。</summary>
    public bool 是否按钮行 => _类别 == 参数表格类别.按钮;

    /// <summary>停用的行置灰。</summary>
    public double 行不透明度 => 定义.启用 ? 1.0 : 0.45;

    partial void On是否启用Changed(bool value)
    {
        定义.启用 = value;
        OnPropertyChanged(nameof(行不透明度));

        // 就地更新内存副本（不落盘，由「保存到本地」统一写）
        _表格.更新(_类别, 定义);
    }

    /// <summary>
    /// 延时值提交（失焦时触发，对应界面绑定 <c>UpdateSourceTrigger=LostFocus</c>）。
    /// <para>需求方确认：参数页元件直接绑定 D/M 区、<b>实时生效</b>，不再走「下发到 PLC」。
    /// 用 LostFocus 而不是 PropertyChanged，是为了避免每敲一个字符就写一次 PLC（红线 R3）。</para>
    /// </summary>
    public event EventHandler<参数条目行视图模型>? 新值已提交;

    partial void On值文本Changed(string value)
    {
        if (_加载中的值)
        {
            return;
        }

        if (坐标辅助.尝试解析(value, out var 已解析))
        {
            定义.值 = 已解析;
            OnPropertyChanged(nameof(新值显示));
            新值已提交?.Invoke(this, this);
        }
    }

    /// <summary>重载行显示（不触发写回），供表格重建后刷新用。</summary>
    public void 重载新值()
    {
        _加载中的值 = true;
        值文本 = 定义.值.ToString("0.000");
        _加载中的值 = false;
    }

    /// <summary>
    /// 记录按钮的本地操作结果。
    /// <para>2026-10-17 起按钮状态会由 <see cref="参数表格服务.异步回读"/> 周期回读，
    /// 这里的"开启 / 关闭"是<b>PLC 侧实时状态</b>（本地操作只是立刻刷新一次，不必等下一轮回读）。</para>
    /// </summary>
    public void 标记按钮状态(bool on) => 按钮状态文本 = on ? "开启" : "关闭";

    /// <summary>
    /// 应用回读到的延时值（秒）。
    /// <para><b>用 <see cref="_加载中的值"/> 挡住写回</b>：回读绝不能反过来写 PLC
    /// （那会形成"读→写→读"的无谓循环，还可能把别人刚改的值覆盖回去）。</para>
    /// </summary>
    public void 应用回读延时(double 秒)
    {
        if (Math.Abs(定义.值 - 秒) < 1e-9)
        {
            return;   // 与本地一致：不必每 2s 发一次无谓的属性通知
        }

        _加载中的值 = true;
        定义.值 = 秒;
        值文本 = 秒.ToString("0.000");
        _加载中的值 = false;

        OnPropertyChanged(nameof(新值显示));
    }
}

/// <summary>
/// 参数设置页 ViewModel。
///
/// <para><b>四张表：</b>轴速度（每轴只保留定位速度，含可编辑的寄存器地址）、延时表、按钮表，
/// 以及仅管理员可见的「轴地址」表（位置 / 正反转点动 / 点动速度档位）。
/// 点动测试已移到示教页与手动调试页。</para>
///
/// <para><b>写入时机：</b>轴速度数值改内存，只有按「下发到 PLC」或切换机种才写设备；
/// 延时数值实时下发；<b>地址改动一律走「保存轴地址」</b>，由服务层统一校验后落盘并重建采集计划。</para>
///
/// <para><b>权限：</b>延时表 / 按钮表 / 轴地址的增删改仅管理员可操作（服务层同步校验）。</para>
/// </summary>
/// <summary>
/// 参数设置页 ViewModel。
///
/// <para><b>四张表：</b>轴速度（每轴只保留定位速度，含可编辑的寄存器地址）、延时表、按钮表，
/// 以及仅管理员可见的「轴地址」表（位置 / 正反转点动 / 点动速度档位）。
/// 点动测试已移到示教页与手动调试页。</para>
///
/// <para><b>写入时机：</b>轴速度数值改内存，只有按「下发到 PLC」或切换机种才写设备；
/// 延时数值实时下发；<b>地址改动走「保存」或「保存轴地址」</b>，由服务层统一校验后落盘并重建采集计划。</para>
///
/// <para><b>权限：</b>延时表 / 按钮表 / 轴地址的增删改仅管理员可操作（服务层同步校验）。</para>
///
/// <para>轴地址部分见 <c>ParamViewModel.AxisAddress.cs</c>（同一 partial 类型）。</para>
/// </summary>
public sealed partial class 参数视图模型 : 视图模型基类, 参数回读页接口
{
    private readonly 配方服务接口 _配方;
    private readonly 参数表格服务接口 _表格;
    private readonly 轴地址服务接口 _轴地址;
    private readonly 用户服务接口 _用户项;
    private readonly 对话框服务接口 _对话框;
    private readonly PLC连接选项? _PLC选项;
    private readonly ILogger<参数视图模型> _日志;

    [ObservableProperty] private string _配方徽标 = "机种 --";
    [ObservableProperty] private string _状态文本 = "改数值不会立即写 PLC，需按「下发到 PLC」";
    [ObservableProperty] private bool _可编辑表格;
    [ObservableProperty] private bool _可操作值列表;

    /// <summary>OPC UA 模式：地址列显示变量名（物理地址无关），轴速度行隐藏物理地址输入框。</summary>
    public bool 变量寻址模式 => 绑定显示.是否变量寻址(_PLC选项);

    public 参数视图模型(
        配方服务接口 配方项,
        参数表格服务接口 表格,
        轴地址服务接口 轴地址列表,
        用户服务接口 用户,
        对话框服务接口 对话框集合,
        IReadOnlyList<轴定义> 轴集合,
        ILogger<参数视图模型> 日志,
        PLC连接选项? PLC选项 = null)
    {
        _配方 = 配方项;
        _表格 = 表格;
        _轴地址 = 轴地址列表;
        _用户项 = 用户;
        _对话框 = 对话框集合;
        _PLC选项 = PLC选项;
        _日志 = 日志;

        标题 = "参数设置";
        副标题 = "轴速度 / 延时 / 按钮 · 按钮可置位复位（即写 M5000–M5099），增删改地址仅管理员";

        var 变量寻址 = 绑定显示.是否变量寻址(PLC选项);

        foreach (var 轴号 in 轴集合)
        {
            轴速度.Add(new 轴速度行视图模型(轴号, 配方项, 变量寻址));
        }

        _配方.配方已变更 += (_, _) => 在界面线程执行(刷新全部);
        _表格.条目已变更 += (_, _) => 在界面线程执行(重建表格);
        _轴地址.地址已变更 += (_, _) => 在界面线程执行(刷新全部);
        _用户项.用户已变更 += (_, _) => 在界面线程执行(刷新权限);

        刷新全部();
        重建表格();
        刷新权限();
    }

    /// <summary>轴速度表。</summary>
    public ObservableCollection<轴速度行视图模型> 轴速度 { get; } = new();

    /// <summary>「轴地址」表（位置 / 正反转点动 / 点动速度档位）。仅管理员可见可改。</summary>
    public ObservableCollection<轴地址行视图模型> 轴地址集合 { get; } = new();

    /// <summary>延时表。</summary>
    public ObservableCollection<参数条目行视图模型> 延迟集合 { get; } = new();

    /// <summary>按钮表。</summary>
    public ObservableCollection<参数条目行视图模型> 按钮集合 { get; } = new();

    /// <inheritdoc />
    public override Task 异步初始化()
    {
        if (地址有改动())
        {
            // 地址栏是独立提交的草稿：换页返回时同样不能把没提交的改动刷掉
            // （现场把"读取/重进页面后地址回默认值"当成"改了保存不了"）。
            状态文本 = "地址栏有未保存的修改：按「保存」提交，或按「读取」放弃";

            foreach (var 行项 in 轴速度)
            {
                行项.刷新();
            }
        }
        else
        {
            刷新全部();
        }

        重建表格();
        刷新权限();
        return Task.CompletedTask;
    }

    // ── 下发 / 保存 ─────────────────────────────────────────────────────

    /// <summary>
    /// 读取（刷新）：「轴速度」数值与地址、延时/按钮表都从配置文件重读。
    ///
    /// <para>地址栏是<b>独立提交</b>的一套编辑（唯一出口是「保存轴地址」），所以刷新会把没提交的
    /// 地址改动丢掉——这在现场被理解成"改了地址保存不了"（2026-10-14 反馈）。
    /// 因此丢弃前先问一句，绝不静默回退。</para>
    /// </summary>
    [RelayCommand]
    private void 读取()
    {
        if (地址有改动() && !_对话框.确认("读取",
                "地址栏还有未保存的修改。\n\n读取会把这些修改恢复为配置文件里的值"
                + "（要保存修改请按「保存」或「保存轴地址」）。是否继续读取？"))
        {
            return;
        }

        刷新全部();
    }

    [RelayCommand]
    private async Task 异步写入()
    {
        if (!_对话框.确认("下发到 PLC",
                $"确认把当前界面的全部参数写入 PLC（机种 {_配方.当前配方号:00}）？",
                视图.确认等级.警告, "下发",
                "PLC 参数区将被界面当前值整体覆盖，设备行为立即改变。"))
        {
            return;
        }

        try
        {
            是否忙碌 = true;
            await _配方.异步写入参数().ConfigureAwait(false);

            var 数量值 = _配方.参数规格.Count;
            var 号 = _配方.当前配方号;

            状态文本 = $"已下发 {数量值} 项参数，并已保存到本机种 {号:00}";
            _对话框.提示("下发完成", $"参数已下发到 PLC，并已保存到本机种（机种 {号:00}）。");
        }
        catch (Exception 异常)
        {
            _日志.LogError(异常, "参数下发失败");
            _对话框.错误信息("下发失败", 异常.Message);
        }
        finally
        {
            是否忙碌 = false;
        }
    }

    [RelayCommand]
    private async Task 异步保存()
    {
        try
        {
            是否忙碌 = true;

            // 地址栏也是本页的编辑：只保存数值与表格，会让"改了地址再按保存"看起来没生效
            // （现场正是这么用的）。管理员且地址确有改动时，一并走轴地址保存流程。
            if (可编辑表格 && 地址有改动())
            {
                await 异步保存轴地址().ConfigureAwait(false);
            }

            await _配方.异步保存本地().ConfigureAwait(false);
            await _表格.异步保存().ConfigureAwait(false);
            _对话框.提示("保存成功", "机种参数与延时/按钮表已保存到本地配置文件。");
        }
        catch (Exception 异常)
        {
            _日志.LogError(异常, "保存失败");
            _对话框.错误信息("保存失败", 异常.Message);
        }
        finally
        {
            是否忙碌 = false;
        }
    }

    [RelayCommand]
    private void 恢复默认值()
    {
        if (!_对话框.确认("恢复默认",
                "确认将本机种的参数恢复为出厂默认值？",
                视图.确认等级.危险, "恢复默认",
                "界面上已调整的参数值会被覆盖；这里只恢复到界面，需再按「下发到 PLC」才会写入设备。"))
        {
            return;
        }

        _配方.恢复默认值();
        刷新全部();

        状态文本 = "已恢复为默认值（尚未写入 PLC）";
    }

    // ── 内部 ────────────────────────────────────────────────────────────

    private async Task 异步添加条目(参数表格类别 类别, string 标签名)
    {
        if (!确保可编辑())
        {
            return;
        }

        var 条目 = new 参数条目定义
        {
            标识 = _表格.新标识(类别),
            名称 = $"新{标签名}",
            地址 = 类别 == 参数表格类别.延迟
                ? $"D{参数表格服务.延迟地址最小}"
                : $"M{参数表格服务.按钮地址最小}",
            单元 = "s",
            最小 = 0,
            最大 = 2147483.647,   // DINT 毫秒上限（2026-10-16 起延时按 32 位存）
            启用 = true,
            顺序 = _表格.获取(类别).Count + 1,
        };

        if (!_对话框.编辑参数条目(类别, 条目))
        {
            return;
        }

        var 校验项 = _表格.校验(类别, 条目);

        if (!校验项.是否有效)
        {
            _对话框.错误信息("校验失败", 校验项.摘要);
            return;
        }

        _表格.添加(类别, 条目);
        await 异步保存表格($"{标签名}「{条目.名称}」已添加。").ConfigureAwait(false);
    }

    private async Task 异步编辑条目(参数表格类别 类别, 参数条目行视图模型? 行项, string 标签名)
    {
        if (行项 is null || !确保可编辑())
        {
            return;
        }

        var 已编辑 = 行项.定义.克隆();

        if (!_对话框.编辑参数条目(类别, 已编辑))
        {
            return;
        }

        // isUpdate: true —— 编辑的是克隆副本，同 Id 的条目就是自身，不能报「已被占用」。
        var 校验项 = _表格.校验(类别, 已编辑, 是否更新: true);

        if (!校验项.是否有效)
        {
            _对话框.错误信息("校验失败", 校验项.摘要);
            return;
        }

        _表格.更新(类别, 已编辑);
        await 异步保存表格($"{标签名}「{已编辑.名称}」已更新。").ConfigureAwait(false);
    }

    private async Task 异步移除条目(参数表格类别 类别, 参数条目行视图模型? 行项, string 标签名)
    {
        if (行项 is null || !确保可编辑())
        {
            return;
        }

        if (!_对话框.确认($"删除{标签名}",
                $"确认删除{标签名}「{行项.名称}」？",
                视图.确认等级.危险, "删除",
                "该条目将从配置文件中移除，删除后需重新录入。"))
        {
            return;
        }

        _表格.移除(类别, 行项.标识);
        await 异步保存表格(null).ConfigureAwait(false);
    }

    private bool 确保可编辑()
    {
        if (可编辑表格)
        {
            return true;
        }

        _对话框.提示("权限不足", "只有管理员可以修改延时表与按钮表。");
        return false;
    }

    private async Task 异步保存表格(string? 消息)
    {
        try
        {
            await _表格.异步保存().ConfigureAwait(false);

            if (消息 is not null)
            {
                _对话框.提示("已保存", 消息);
            }
        }
        catch (Exception 异常)
        {
            _日志.LogError(异常, "保存参数表失败");
            _对话框.错误信息("保存失败", 异常.Message);
        }
    }

    private void 刷新全部()
    {
        配方徽标 = string.IsNullOrWhiteSpace(_配方.当前配方名称)
            ? $"机种 {_配方.当前配方号:00}"
            : $"机种 {_配方.当前配方号:00} · {_配方.当前配方名称}";

        foreach (var 行项 in 轴速度)
        {
            行项.刷新();
            行项.刷新地址();
        }

        重建轴地址();
    }

    /// <summary>按服务的当前快照重建「轴地址」表。</summary>
    private void 重建轴地址()
    {
        轴地址集合.Clear();

        foreach (var 条目项 in _轴地址.表格.轴列表)
        {
            var 行项 = new 轴地址行视图模型(条目项);
            行项.刷新();
            轴地址集合.Add(行项);
        }
    }

    private void 重建表格()
    {
        延迟集合.Clear();

        var 变量寻址 = 变量寻址模式;

        foreach (var 条目 in _表格.延迟集合)
        {
            var 行项 = new 参数条目行视图模型(条目, 参数表格类别.延迟, _表格, 变量寻址);
            行项.新值已提交 += 行新值已提交;
            延迟集合.Add(行项);
        }

        按钮集合.Clear();

        foreach (var 条目 in _表格.按钮集合)
        {
            按钮集合.Add(new 参数条目行视图模型(条目, 参数表格类别.按钮, _表格, 变量寻址));
        }
    }

    private void 行新值已提交(object? 发送者, 参数条目行视图模型 行项) =>
        异步安全调用.即发即忘(
            () => 异步提交延迟新值(行项),
            _日志,
            $"延时「{行项.名称}」实时下发");

    /// <summary>延时值失焦提交：立即写 D 区并静默落盘（实时生效，需求方 2026-09-11 确认）。</summary>
    private async Task 异步提交延迟新值(参数条目行视图模型 行项)
    {
        if (!可操作值列表)
        {
            return;   // 输入框对无权限者已禁用，这里是服务层之外的双保险
        }

        try
        {
            await _表格.异步写入延迟(行项.定义).ConfigureAwait(false);

            var 毫秒 = (int)Math.Round(行项.定义.值 * 1000, MidpointRounding.AwayFromZero);
            状态文本 = $"「{行项.名称}」已实时生效：{行项.定义.值:0.###} S（PLC 写入 {毫秒} ms）";
        }
        catch (Exception 异常)
        {
            _日志.LogError(异常, "延时「{Name}」实时下发失败", 行项.名称);
            在界面线程执行(() => _对话框.错误信息("下发失败", 异常.Message));
        }
    }

    private void 刷新权限()
    {
        // 增删条目与改绑定地址 → 管理员；改数值与操作按钮（置位/复位）→ 工程师
        可编辑表格 = _用户项.有(权限.编辑绑定地址);
        可操作值列表 = _用户项.有(权限.编辑参数值列表);
    }

    // ── 轴地址（仅管理员）：见 ParamViewModel.AxisAddress.cs（partial）────────

    // ── 参数回读（2026-10-17 现场要求）────────────────────────────────────

    /// <summary>
    /// 回读延时表与按钮表的 PLC 当前值（由主界面 1s 时钟按 2s 间隔调用）。
    ///
    /// <para><b>为什么必须回读：</b>延时（D1000–D1098）与按钮（M5000–M5099）不在 <c>tags.json</c> 里，
    /// 不走轮询引擎；但 PLC 侧同样可能改写它们（切换机种、HMI 旁的操作面板、断电前的保持状态）。
    /// 界面只显示本地配置值会与设备实际不符——现场反馈"改了的延时没生效"正是这种现象。</para>
    /// </summary>
    public async Task 异步刷新参数回读()
    {
        await _表格.异步回读().ConfigureAwait(true);

        在界面线程执行(() =>
        {
            foreach (var 行项 in 延迟集合)
            {
                if (_表格.回读延时值(行项.标识) is { } 秒)
                {
                    行项.应用回读延时(秒);
                }
            }

            foreach (var 行项 in 按钮集合)
            {
                if (_表格.回读按钮状态(行项.标识) is { } 状态)
                {
                    行项.标记按钮状态(状态);
                }
            }
        });
    }
}
