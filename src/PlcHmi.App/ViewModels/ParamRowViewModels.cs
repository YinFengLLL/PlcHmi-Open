using CommunityToolkit.Mvvm.ComponentModel;
using 上位机.应用.服务;
using 上位机.核心.抽象;
using 上位机.核心.模型;

namespace 上位机.应用.视图模型;

/// <summary>轴速度表的一行：只含该轴的定位速度与它的寄存器地址。</summary>
public sealed partial class 轴速度行视图模型 : ObservableObject
{
    private readonly 轴定义 _定义项;
    private readonly 配方服务接口 _配方;
    private readonly bool _变量寻址;

    [ObservableProperty] private string _定位速度文本 = "0";

    /// <summary>
    /// 定位速度寄存器地址（如 <c>D1202</c>，各轴区偏移 2）。
    /// <para>仅管理员可改。<b>这里只是唯一编辑入口</b>，真正的写入由
    /// <c>IAxisAddressService</c> 统一做服务层校验并原子落盘——
    /// 页面自己写配置会绕过区间/重叠检查。</para>
    /// </summary>
    [ObservableProperty] private string _地址文本 = string.Empty;

    /// <summary>定位速度绑定的变量表变量名（选变量后自动填充）。</summary>
    [ObservableProperty] private string _变量文本 = string.Empty;

    public 轴速度行视图模型(轴定义 定义项, 配方服务接口 配方项, bool 变量寻址 = false)
    {
        _定义项 = 定义项;
        _配方 = 配方项;
        _变量寻址 = 变量寻址;
    }

    public string 名称 => _定义项.名称;
    public string 单元 => _定义项.单元;
    public string 轴键 => _定义项.轴;

    /// <summary>是否显示物理地址输入框（Modbus 模式）。OPC UA 模式改为只显示绑定变量。</summary>
    public bool 显示物理地址 => !_变量寻址;

    /// <summary>是否为 OPC UA 变量寻址模式（界面据此切换地址列显示）。</summary>
    public bool 变量寻址模式 => _变量寻址;

    /// <summary>OPC UA 模式下的绑定显示（变量名；未绑定时给出提示）。</summary>
    public string 绑定显示文本 =>
        _绑定变量.Length > 0 ? _绑定变量 : 绑定显示.未绑定提示;

    private string _绑定变量 => 变量文本.Trim();

    /// <summary>定位速度的参数键。</summary>
    public string 定位速度键 => $"{_定义项.轴}.LocateSpeed";

    /// <summary>从机种数据刷新（不触发写 PLC）。</summary>
    public void 刷新()
    {
        if (_配方.当前参数.TryGetValue(定位速度键, out var 值))
        {
            定位速度文本 = 坐标辅助.格式化(值);
        }
    }

    /// <summary>刷新地址显示（保存轴地址后需要重读一次）。</summary>
    public void 刷新地址()
    {
        var 规格项 = _配方.参数规格
            .FirstOrDefault(点位项 => string.Equals(点位项.键, 定位速度键, StringComparison.OrdinalIgnoreCase));

        地址文本 = 规格项 is null ? string.Empty : $"D{规格项.地址}";
        变量文本 = 规格项?.变量 ?? string.Empty;
    }

    /// <summary>改数值只改内存，不写 PLC。</summary>
    partial void On定位速度文本Changed(string value)
    {
        if (坐标辅助.尝试解析(value, out var 已解析))
        {
            _配方.设置参数(定位速度键, 已解析);
        }
    }

    /// <summary>变量绑定变化时刷新 OPC UA 模式下的绑定显示。</summary>
    partial void On变量文本Changed(string value) => OnPropertyChanged(nameof(绑定显示文本));
}

/// <summary>
/// 「轴地址」表的一行：当前位置 / 正反转点动 / 点动速度档位。
///
/// <para><b>为什么没有定位速度地址这一列</b>：它在上方「轴速度」表里已经有唯一编辑入口，
/// 同一字段在两处可改必然导致漂移与“改了哪一处才算数”的困惑。</para>
/// </summary>
public sealed partial class 轴地址行视图模型 : ObservableObject
{
    private readonly 轴地址条目 _条目;

    [ObservableProperty] private string _位置地址 = string.Empty;
    [ObservableProperty] private string _点动正转地址 = string.Empty;
    [ObservableProperty] private string _点动反转地址 = string.Empty;
    [ObservableProperty] private string _点动速度等级地址 = string.Empty;
    [ObservableProperty] private string _回零地址 = string.Empty;

    public 轴地址行视图模型(轴地址条目 条目项) => _条目 = 条目项;

    /// <summary>轴显示名，如 <c>X 轴</c>。</summary>
    public string 名称 => _条目.名称;

    /// <summary>轴标识，用于回写候选表。</summary>
    public string 轴 => _条目.轴;

    /// <summary>从服务快照刷新。</summary>
    public void 刷新()
    {
        位置地址 = _条目.位置地址;
        点动正转地址 = _条目.点动正转地址;
        点动反转地址 = _条目.点动反转地址;
        点动速度等级地址 = _条目.点动速度等级地址;
        回零地址 = _条目.回零地址;
    }

    /// <summary>把界面上的值写回目标条目（供服务层校验）。</summary>
    public void 应用(轴地址条目 目标)
    {
        目标.位置地址 = 位置地址.Trim();
        目标.点动正转地址 = 点动正转地址.Trim();
        目标.点动反转地址 = 点动反转地址.Trim();
        目标.点动速度等级地址 = 点动速度等级地址.Trim();
        目标.回零地址 = 回零地址.Trim();
    }
}
