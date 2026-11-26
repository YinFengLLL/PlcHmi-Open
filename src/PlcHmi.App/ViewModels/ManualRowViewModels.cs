using CommunityToolkit.Mvvm.ComponentModel;
using 上位机.应用.服务;
using 上位机.核心.抽象;
using 上位机.核心.模型;

namespace 上位机.应用.视图模型;

/// <summary>
/// 手动页的一个元件（气缸 / 按钮 / 指示灯）。
///
/// <para><b>地址硬约束：</b>元件只能绑定 M 区。服务层会再校验一次，
/// 即使配置文件被手工改错也不会执行到非 M 区地址。</para>
/// </summary>
public sealed partial class 手动元件视图模型 : ObservableObject
{
    private readonly 手动控件服务接口 _手动;
    private readonly bool _变量寻址;

    [ObservableProperty] private bool _是否活动;
    [ObservableProperty] private string _状态文本 = string.Empty;

    public 手动元件视图模型(手动元件定义 定义项, 手动控件服务接口 手动, bool 变量寻址 = false)
    {
        定义 = 定义项;
        _手动 = 手动;
        _变量寻址 = 变量寻址;
        更新状态文本();
    }

    /// <summary>底层定义（编辑用）。</summary>
    public 手动元件定义 定义 { get; }

    public string 标识 => 定义.标识;
    public string 名称 => 定义.名称;
    public bool 是否气缸 => 定义.是否气缸;
    public bool 是否按钮 => 定义.是否按钮;
    public bool 是否指示灯 => 定义.是否指示灯;

    /// <summary>
    /// 绑定地址提示。
    /// <para>OPC UA 模式且已绑定变量时显示<b>变量名</b>（按变量名符号寻址，物理地址无关）；
    /// 否则显示软元件地址。</para>
    /// </summary>
    public string 地址文本 => 绑定显示.地址文本(
        _变量寻址,
        定义.是否指示灯 ? 定义.状态值变量 : 定义.变量,
        定义.是否指示灯 ? 定义.状态地址 : 定义.地址);

    /// <summary>主操作按钮的文案。</summary>
    public string 动作文本 => 定义.是否按钮 ? "触发" : 是否活动 ? "缩回" : "伸出";

    partial void On是否活动Changed(bool value) => 更新状态文本();

    private void 更新状态文本()
    {
        if (定义.是否指示灯)
        {
            状态文本 = 是否活动 ? "ON" : "OFF";
        }
        else
        {
            状态文本 = 是否活动 ? "伸出" : "缩回";
        }

        OnPropertyChanged(nameof(动作文本));
    }

    /// <summary>
    /// 气缸伸出（置位）/ 按钮触发（脉冲）。
    ///
    /// <para><b>单线圈气缸</b>：置位该位即伸出、复位即缩回，不需要两个地址。</para>
    /// </summary>
    public async Task 异步激活()
    {
        if (定义.是否按钮)
        {
            await _手动.异步脉冲线圈(定义.地址, TimeSpan.Zero).ConfigureAwait(false);
            return;
        }

        await _手动.异步写入线圈(定义.地址, true).ConfigureAwait(false);
        是否活动 = true;
    }

    /// <summary>气缸缩回（复位同一位）。</summary>
    public async Task 异步停用()
    {
        if (!定义.是否气缸)
        {
            return;
        }

        await _手动.异步写入线圈(定义.地址, false).ConfigureAwait(false);
        是否活动 = false;
    }
}
