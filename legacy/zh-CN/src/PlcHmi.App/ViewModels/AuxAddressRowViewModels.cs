using CommunityToolkit.Mvvm.ComponentModel;
using 上位机.应用.服务;
using 上位机.核心.抽象;
using 上位机.核心.模型;

namespace 上位机.应用.视图模型;

/// <summary>
/// 页面上一个辅助地址行（具名数值元件或开关元件）。
///
/// <para><b>结构体数组（2026-10-21）：</b>点胶页的单点工艺参数在 PLC 里是"每点一份"的结构体数组
/// （见 <see cref="辅助地址项.按点地址"/>）。行本身记住<b>当前点序号</b>，
/// 地址与读写都按它实时换算——切换点时只改序号、不重建行，
/// 这样界面控件不会闪、也不会丢焦点。</para>
///
/// <para><b>实时读写（2026-11-12 现场要求）：</b>辅助地址的值改为
/// ① 页面停留期间周期从 PLC 读回（见 <see cref="点表示教视图模型.异步刷新动作按钮状态"/>），
/// ② 填完一个数（回车或离开输入框）立刻写 PLC——不再有「写入」按钮。
/// 写入前必须已打开手动使能（<see cref="可编辑"/> 由页根据使能状态下发），
/// 避免"随手改一个数就把工艺参数改了"。</para>
/// </summary>
public sealed partial class 辅助地址行视图模型 : ObservableObject, 输入提交行
{
    private readonly 页面地址服务接口 _服务;

    /// <summary>程序正在按 PLC 值刷新（此时赋值不得反向触发写入）。</summary>
    private bool _刷新中;

    [ObservableProperty] private string _文本 = "0.00";
    [ObservableProperty] private bool _开关状态;

    /// <summary>当前点序号（0 基）。结构体数组模式下行寻址用；固定地址模式下无影响。</summary>
    [ObservableProperty] private int _点序号;

    /// <summary>是否允许修改（= 已打开手动使能）。关闭时输入框与开关置灰。</summary>
    [ObservableProperty] private bool _可编辑;

    /// <summary>
    /// 操作员正在这个输入框里打字（周期回读要跳过它，否则读到一半被 PLC 值覆盖）。
    /// 由 <see cref="输入提交手势"/> 在焦点变化时维护。
    /// </summary>
    [ObservableProperty] private bool _编辑中;

    public 辅助地址行视图模型(辅助地址项 定义项, 页面地址服务接口 服务)
    {
        定义 = 定义项;
        _服务 = 服务;

        // 数组模式的初始值：取主定义里的"第 0 点"地址（结构体基址 + 偏移）
        地址文本 = 定义.按点地址(0);
    }

    /// <summary>底层定义（含结构体参数）。</summary>
    public 辅助地址项 定义 { get; }

    /// <summary>显示名称。</summary>
    public string 名称 => 定义.显示名称;

    /// <summary>
    /// 提交请求：本行有一次"用户改完"要落 PLC。由页面接管（权限 / 手动使能 / 失败提示都在那边统一做）。
    /// </summary>
    public Action<辅助地址行视图模型>? 提交请求 { get; set; }

    /// <summary>当前点对应的地址文本（结构体数组模式随 <see cref="点序号"/> 变化）。</summary>
    [ObservableProperty] private string _地址文本 = string.Empty;

    /// <summary>是否开关类（M 区布尔）。</summary>
    public bool 是开关 => 定义.是开关;

    /// <summary>是否数值类（非开关）。</summary>
    public bool 是数值 => !定义.是开关;

    /// <summary>是否跟随点序号寻址（界面上给个提示）。</summary>
    public bool 是结构体数组 => 定义.是结构体数组;

    /// <summary>单位文本。</summary>
    public string 单元 => 定义.单元 ?? string.Empty;

    /// <summary>输入提示：未开手动使能时说明为什么改不了。</summary>
    public string 输入提示 => 可编辑
        ? $"{名称}：填完按回车（或点别处）即写入 PLC"
        : $"{名称}：请先打开手动使能，才能修改辅助地址的值";

    partial void On点序号Changed(int value) => 刷新地址();

    partial void On可编辑Changed(bool value) => OnPropertyChanged(nameof(输入提示));

    /// <summary>
    /// 按当前点序号重算地址文本（2026-10-21）。
    /// <para><b>为什么单独一个方法：</b>结构体成员的类型 / 长度被改过之后（编辑器里保存），
    /// 点序号可能没变，但地址必须重算——否则界面还显示旧偏移（例如点胶速度停在 D3506，
    /// 而结构体重排后它已经前移到 D3505）。</para>
    /// </summary>
    public void 刷新地址() => 地址文本 = 定义.按点地址(点序号);

    /// <summary>开关勾选/取消：立即提交（与数值框"填完提交"同一套语义）。</summary>
    partial void On开关状态Changed(bool value)
    {
        if (_刷新中 || !可编辑)
        {
            return;
        }

        提交请求?.Invoke(this);
    }

    /// <inheritdoc />
    public void 标记编辑中(bool 值) => 编辑中 = 值;

    /// <inheritdoc />
    public void 提交()
    {
        if (!可编辑)
        {
            return;
        }

        提交请求?.Invoke(this);
    }

    /// <summary>按当前点序号换算出"用于读写的那一份定义"（地址已解析）。</summary>
    private 辅助地址项 当前项()
    {
        if (!定义.是结构体数组)
        {
            return 定义;
        }

        var 副本 = 定义.克隆();
        副本.地址 = 地址文本;
        return 副本;
    }

    /// <summary>从 PLC 读取当前值（数值读 D 区，开关读 M 区线圈）。</summary>
    public async Task 异步读取()
    {
        if (是开关)
        {
            var 状态 = await _服务.异步读取辅助开关(当前项()).ConfigureAwait(false);

            _刷新中 = true;
            开关状态 = 状态;
            _刷新中 = false;
            return;
        }

        var 值 = await _服务.异步读取辅助地址(当前项()).ConfigureAwait(false);
        文本 = 坐标辅助.格式化(值);
    }

    /// <summary>
    /// 写回 PLC 后按 PLC 的值刷新显示（写入失败或求值失败时用，避免界面停在"看起来成功了"的值）。
    /// </summary>
    public Task 异步刷新显示() => 异步读取();

    /// <summary>
    /// 一整页辅助地址行的批量刷新（2026-11-19 性能改造）。
    /// <para><b>为什么放在行类里：</b>所有行都持有同一个 <see cref="_服务"/> 实例（由
    /// <c>辅助地址实时写入手柄.创建</c> 统一注入），这里取第 0 行的服务即可，
    /// 不必改手柄的构造器、也不必改各页 ViewModel 的构造流程。</para>
    /// <para><b>与逐行读语义一致：</b>跳过正在输入的行（不能把 PLC 旧值盖到打字中的输入框）、
    /// 开关类（M 区线圈）仍逐点读、单项/单块失败只跳过该项。</para>
    /// </summary>
    public static async Task 异步批量刷新(IReadOnlyList<辅助地址行视图模型> 行集合)
    {
        if (行集合.Count == 0)
        {
            return;
        }

        // 开关（M 区）不进 D 区合并计划，逐点读保持原有语义
        foreach (var 行项 in 行集合.Where(行 => !行.编辑中 && 行.是开关))
        {
            try
            {
                await 行项.异步读取().ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // 单项失败只跳过，与逐行读取的容错口径一致
            }
            catch (System.IO.IOException)
            {
            }
            catch (System.Net.Sockets.SocketException)
            {
            }
        }

        var 数值行 = 行集合.Where(行 => !行.编辑中 && !行.是开关).ToList();

        if (数值行.Count == 0)
        {
            return;
        }

        var 项列表 = 数值行.Select(行 => 行.当前项()).ToList();

        IReadOnlyList<double?> 值列表;

        try
        {
            值列表 = await 数值行[0]._服务
                .异步批量读取辅助地址(项列表)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return;   // 整批失败：本周期跳过，不打断页面
        }
        catch (System.IO.IOException)
        {
            return;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return;
        }

        for (var 序号 = 0; 序号 < 数值行.Count && 序号 < 值列表.Count; 序号++)
        {
            if (值列表[序号] is { } 值)
            {
                数值行[序号].文本 = 坐标辅助.格式化(值);
            }
        }
    }

    /// <summary>把当前文本写入 PLC。</summary>
    public async Task 异步写入()
    {
        if (!坐标辅助.尝试解析(文本, out var 值))
        {
            throw new InvalidOperationException($"{名称} 的值「{文本}」不是有效数字。");
        }

        await _服务.异步写入辅助地址(当前项(), 值).ConfigureAwait(false);
    }

    /// <summary>写入开关状态（M 区线圈）。</summary>
    public async Task 异步写入开关() =>
        await _服务.异步写入辅助开关(当前项(), 开关状态).ConfigureAwait(false);
}

/// <summary>
/// 页面上一个动作按钮（电机复位、电批启动、出胶阀、吸螺丝真空…）。
///
/// <para><b>切换型（现场口径 2026-10-13）：</b>首次按下置位（激活），再次按下复位（取消激活）。
/// 激活状态直接显示在按钮外观上——比弹确认框更直观，也不会让人对弹窗麻木。</para>
/// </summary>
public sealed partial class 动作按钮视图模型 : ObservableObject
{
    /// <summary>是否已激活（= 对应 M 线圈已置位）。</summary>
    [ObservableProperty] private bool _已激活;

    public 动作按钮视图模型(动作按钮项 定义项) => 定义 = 定义项;

    /// <summary>按钮定义。</summary>
    public 动作按钮项 定义 { get; }

    /// <summary>显示名称。</summary>
    public string 名称 => 定义.显示名称;

    /// <summary>是否需要二次确认（切换型按钮已不再弹确认，保留供配置兼容与后续使用）。</summary>
    public bool 是否危险 => 定义.是否危险;

    /// <summary>地址文本（只读展示）。</summary>
    public string 地址文本 => 定义.地址;

    /// <summary>
    /// 按钮上显示的文字：固定按钮按线圈状态切换（0 = 默认参数 / 1 = 独立参数），
    /// 普通动作按钮始终显示名称。
    /// </summary>
    public string 显示名称 => 定义.状态文本(已激活);

    /// <summary>状态提示（悬停显示当前状态与下一次按下的效果）。</summary>
    public string 状态提示 => 已激活
        ? $"{名称}：{显示名称}（{地址文本} 置位中，再次按下取消）"
        : $"{名称}：{显示名称}（按下置位 {地址文本}）";

    partial void On已激活Changed(bool value)
    {
        // 固定按钮的文字随状态变化，必须显式通知（否则回读到 1 后按钮仍写着"默认参数"）
        OnPropertyChanged(nameof(显示名称));
        OnPropertyChanged(nameof(状态提示));
    }
}
