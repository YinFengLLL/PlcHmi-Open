using System.Globalization;
using System.IO;
using System.IO.Ports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using 上位机.应用.服务;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.应用.视图模型;

/// <summary>
/// 系统设置页「扫码枪设置」板块（2026-11-12 新增）。
///
/// <para><b>两块内容：</b>① 通讯（串口 / TCP/IP）与扫码枪参数；
/// ② 与 PLC 的握手信号——扫码成功信号寄存器（默认 D4050）与扫码触发寄存器（默认 D4010），
/// 以及工作模式（手动型 / 触发型）。</para>
///
/// <para><b>保存即生效：</b>走服务层校验 + 原子落盘 + 重连，不需要重启程序；
/// 只有管理员（编辑 PLC 连接权限）能改。</para>
/// </summary>
public sealed partial class 扫码枪设置视图模型 : ObservableObject
{
    private readonly 扫码枪服务接口 _扫码枪;
    private readonly 用户服务接口 _用户项;
    private readonly 对话框服务接口 _对话框;
    private readonly ILogger<扫码枪设置视图模型> _日志;

    [ObservableProperty] private bool _启用;
    [ObservableProperty] private int _通讯模式索引;
    [ObservableProperty] private string _串口号 = "COM1";
    [ObservableProperty] private string _波特率 = "9600";
    [ObservableProperty] private int _数据位索引 = 3;      // 5/6/7/8 → 默认 8
    [ObservableProperty] private int _停止位索引;          // 1 / 1.5 / 2 → 默认 1
    [ObservableProperty] private int _校验索引;            // 无 / 奇 / 偶 → 默认 无
    [ObservableProperty] private string _网口地址 = "192.168.1.11";
    [ObservableProperty] private string _网口端口 = "2000";
    [ObservableProperty] private string _扫码成功寄存器 = "D4050";
    [ObservableProperty] private string _扫码触发寄存器 = "D4010";
    [ObservableProperty] private int _工作模式索引;
    [ObservableProperty] private string _触发指令 = "TRIGGER";
    [ObservableProperty] private string _释放指令 = "RELEASE";
    [ObservableProperty] private string _轮询间隔毫秒 = "500";
    [ObservableProperty] private string _超时毫秒 = "5000";
    [ObservableProperty] private bool _可编辑扫码枪;
    [ObservableProperty] private string _连接状态文本 = "未启用";
    [ObservableProperty] private string _连接状态颜色键 = "MutedBrush";
    [ObservableProperty] private string _工单号文本 = "（尚未扫码）";

    public 扫码枪设置视图模型(
        扫码枪服务接口 扫码枪,
        用户服务接口 用户,
        对话框服务接口 对话框,
        ILogger<扫码枪设置视图模型> 日志)
    {
        _扫码枪 = 扫码枪;
        _用户项 = 用户;
        _对话框 = 对话框;
        _日志 = 日志;

        _扫码枪.连接已变更 += (_, 已连接) => 界面派发.投递(() => 应用连接状态(已连接));
        _扫码枪.工单号已变更 += (_, 工单号) => 界面派发.投递(() => 工单号文本 = 工单号);
        _用户项.用户已变更 += (_, _) => 界面派发.投递(刷新权限);

        从配置加载();
        刷新权限();
        应用连接状态(_扫码枪.已连接);

        if (!string.IsNullOrEmpty(_扫码枪.工单号))
        {
            工单号文本 = _扫码枪.工单号;
        }
    }

    /// <summary>通讯方式下拉。</summary>
    public string[] 通讯模式选项 { get; } = ["串口通讯", "TCP/IP 通讯"];

    /// <summary>工作模式下拉。</summary>
    public string[] 工作模式选项 { get; } = ["手动型", "触发型"];

    /// <summary>校验下拉。</summary>
    public string[] 校验选项 { get; } = ["无", "奇", "偶"];

    /// <summary>停止位下拉。</summary>
    public string[] 停止位选项 { get; } = ["1", "1.5", "2"];

    /// <summary>数据位下拉（索引 + 5）。</summary>
    public string[] 数据位选项 { get; } = ["5", "6", "7", "8"];

    /// <summary>
    /// 本机可用串口下拉项。
    ///
    /// <para><b>当前配置的串口必须在列表里</b>：扫码枪没插（或换了台机器）时
    /// <see cref="SerialPort.GetPortNames"/> 里没有 COMx，下拉就成了"配置写着 COM3、
    /// 选择框却空着"——现场会误以为配置丢了。所以枚举结果与当前串口号取并集。</para>
    /// </summary>
    [ObservableProperty] private string[] _可用串口 = [];

    /// <summary>刷新串口列表（插拔 USB 转串口后点一下）。</summary>
    [RelayCommand]
    private void 刷新串口() => 刷新可用串口();

    /// <summary>串口参数区是否可见（TCP 模式隐藏）。</summary>
    public bool 是串口模式 => 通讯模式索引 == 0;

    /// <summary>网口参数区是否可见。</summary>
    public bool 是网口模式 => 通讯模式索引 == 1;

    partial void On通讯模式索引Changed(int value)
    {
        OnPropertyChanged(nameof(是串口模式));
        OnPropertyChanged(nameof(是网口模式));
    }

    /// <summary>保存并立即应用（管理员）。</summary>
    [RelayCommand]
    private async Task 异步保存()
    {
        if (!可编辑扫码枪)
        {
            _对话框.提示("权限不足", "仅管理员可修改扫码枪设置。");
            return;
        }

        if (!尝试组装(out var 候选, out var 错误))
        {
            _对话框.错误信息("扫码枪设置无效", 错误);
            return;
        }

        try
        {
            await _扫码枪.异步保存设置(候选).ConfigureAwait(true);
            从配置加载();
            _对话框.提示("已保存", "扫码枪设置已保存并立即生效。");
        }
        catch (UnauthorizedAccessException 异常)
        {
            _对话框.错误信息("保存失败", 异常.Message);
        }
        catch (InvalidOperationException 异常)
        {
            _对话框.错误信息("保存失败", 异常.Message);
        }
        catch (IOException 异常)
        {
            _日志.LogError(异常, "保存扫码枪设置失败");
            _对话框.错误信息("保存失败", $"写入 appsettings.json 失败：{异常.Message}");
        }
    }

    /// <summary>按当前配置重连一次（串口拔插 / 扫码枪重启后用）。</summary>
    [RelayCommand]
    private async Task 异步重连()
    {
        异步安全调用.即发即忘(() => _扫码枪.异步重启(), _日志, "重连扫码枪");

        await Task.Delay(300).ConfigureAwait(true);
        应用连接状态(_扫码枪.已连接);
    }

    /// <summary>把运行配置搬到界面字段。</summary>
    private void 从配置加载()
    {
        var 配置 = _扫码枪.配置;

        启用 = 配置.启用;
        通讯模式索引 = (int)配置.通讯模式;
        串口号 = 配置.串口号;
        波特率 = 配置.波特率.ToString(CultureInfo.InvariantCulture);
        数据位索引 = Math.Clamp(配置.数据位 - 5, 0, 3);
        停止位索引 = 配置.停止位?.Trim() switch { "2" => 2, "1.5" => 1, _ => 0 };
        校验索引 = 配置.校验?.Trim() switch { "奇" => 1, "偶" => 2, _ => 0 };
        网口地址 = 配置.网口地址;
        网口端口 = 配置.网口端口.ToString(CultureInfo.InvariantCulture);
        扫码成功寄存器 = 配置.扫码成功寄存器;
        扫码触发寄存器 = 配置.扫码触发寄存器;
        工作模式索引 = (int)配置.工作模式;
        触发指令 = 配置.触发指令;
        释放指令 = 配置.释放指令;
        轮询间隔毫秒 = 配置.轮询间隔毫秒.ToString(CultureInfo.InvariantCulture);
        超时毫秒 = 配置.超时毫秒.ToString(CultureInfo.InvariantCulture);

        刷新可用串口();
        OnPropertyChanged(nameof(是串口模式));
        OnPropertyChanged(nameof(是网口模式));
    }

    private void 刷新权限() => 可编辑扫码枪 = _用户项.有(权限.编辑PLC连接);

    private void 应用连接状态(bool 已连接)
    {
        if (!启用)
        {
            连接状态文本 = "未启用";
            连接状态颜色键 = "MutedBrush";
            return;
        }

        连接状态文本 = 已连接 ? "已连接" : "未连接";
        连接状态颜色键 = 已连接 ? "SuccessBrush" : "WarningBrush";
    }

    private bool 尝试组装(out 扫码枪配置 候选, out string 错误)
    {
        候选 = new 扫码枪配置();
        错误 = string.Empty;

        if (!int.TryParse(波特率.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var 波特率值))
        {
            错误 = "波特率必须是整数（如 9600）。";
            return false;
        }

        if (!int.TryParse(网口端口.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var 端口值))
        {
            错误 = "网口端口必须是整数（如 2000）。";
            return false;
        }

        if (!int.TryParse(轮询间隔毫秒.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var 间隔值))
        {
            错误 = "轮询间隔必须是整数（毫秒）。";
            return false;
        }

        if (!int.TryParse(超时毫秒.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var 超时值))
        {
            错误 = "扫码超时必须是整数（毫秒，如 5000）。";
            return false;
        }

        候选.启用 = 启用;
        候选.通讯模式 = 通讯模式索引 == 1 ? 扫码通讯模式.TcpIp : 扫码通讯模式.串口;
        候选.串口号 = 串口号.Trim();
        候选.波特率 = 波特率值;
        候选.数据位 = 数据位索引 + 5;
        候选.停止位 = 停止位选项[Math.Clamp(停止位索引, 0, 2)];
        候选.校验 = 校验选项[Math.Clamp(校验索引, 0, 2)];
        候选.网口地址 = 网口地址.Trim();
        候选.网口端口 = 端口值;
        候选.扫码成功寄存器 = 扫码成功寄存器.Trim();
        候选.扫码触发寄存器 = 扫码触发寄存器.Trim();
        候选.工作模式 = 工作模式索引 == 1 ? 扫码工作模式.触发型 : 扫码工作模式.手动型;
        候选.触发指令 = 触发指令;
        候选.释放指令 = 释放指令;
        候选.轮询间隔毫秒 = 间隔值;
        候选.超时毫秒 = 超时值;

        return true;
    }

    /// <summary>重新枚举串口，并把当前配置的串口并进列表（保证选中项一定在下拉里）。</summary>
    private void 刷新可用串口()
    {
        var 端口 = 枚举串口();
        var 当前 = 串口号?.Trim() ?? string.Empty;

        if (当前.Length > 0 && !端口.Contains(当前, StringComparer.OrdinalIgnoreCase))
        {
            端口 = [.. 端口, 当前];
            Array.Sort(端口, StringComparer.OrdinalIgnoreCase);
        }

        可用串口 = 端口;
    }

    private static string[] 枚举串口()
    {
        string[] 端口;

        try
        {
            端口 = SerialPort.GetPortNames();
        }
        catch (IOException)
        {
            return [];
        }
        catch (InvalidOperationException)
        {
            return [];
        }

        Array.Sort(端口, StringComparer.OrdinalIgnoreCase);
        return 端口;
    }
}
