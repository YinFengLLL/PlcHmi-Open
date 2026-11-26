using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using 上位机.应用.服务;
using 上位机.业务.服务;
using 上位机.业务.配置;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.应用.视图模型;

/// <summary>
/// 系统设置页 ViewModel。
///
/// <para>只读展示所有人可见；「连接参数编辑」区仅管理员（<see cref="权限.编辑PLC连接"/>）可见。
/// 保存走服务层校验 + 原子落盘 + 断开重连，<b>立即生效</b>，不需要重启程序。</para>
/// </summary>
public sealed partial class 设置视图模型 : 视图模型基类
{
    private readonly PLC连接选项 _PLC选项;
    private readonly 轮询选项 _轮询选项;
    private readonly 连接管理器接口 _连接;
    private readonly PLC连接设置服务接口 _设置;
    private readonly 用户服务接口 _用户项;
    private readonly 对话框服务接口 _对话框;
    private readonly 变量表服务接口? _变量表;
    private readonly 文件对话框服务接口 _文件对话框;
    private readonly 运行模式管理器接口? _运行模式;
    private readonly 界面选项? _界面选项;
    private readonly ILogger<设置视图模型> _日志;

    [ObservableProperty] private string _连接状态文本 = "未连接";
    [ObservableProperty] private string _连接状态颜色键 = "MutedBrush";
    [ObservableProperty] private bool _可编辑连接;
    [ObservableProperty] private bool _可导入变量表;

    // ── 连接参数编辑区（仅管理员）─────────────────────────────────────
    // 数值项用 string 承接输入框：解析失败由本层指出具体字段，范围校验由服务层兜底。
    [ObservableProperty] private string _编辑主机 = string.Empty;
    [ObservableProperty] private string _编辑端口 = string.Empty;
    [ObservableProperty] private string _编辑单元标识 = string.Empty;
    [ObservableProperty] private string _编辑连接超时 = string.Empty;
    [ObservableProperty] private string _编辑读取超时 = string.Empty;
    [ObservableProperty] private string _编辑心跳 = string.Empty;
    [ObservableProperty] private string _编辑重连初始 = string.Empty;
    [ObservableProperty] private string _编辑重连最大 = string.Empty;
    [ObservableProperty] private string _编辑最大寄存器 = string.Empty;
    [ObservableProperty] private bool _编辑自动重连;

    // 通讯模式（协议下拉 0=Modbus TCP 1=OPC UA）与 OPC UA 参数
    [ObservableProperty] private int _编辑协议索引;
    [ObservableProperty] private string _编辑OPCUA端口 = string.Empty;
    [ObservableProperty] private string _编辑命名空间 = string.Empty;

    /// <summary>协议下拉选项（索引与 通讯协议 枚举一一对应）。</summary>
    public string[] 协议选项 { get; } = ["Modbus TCP", "OPC UA"];

    /// <summary>可配置弹窗的监控服务（弹窗设置入口用；未注册时为 null，界面隐藏入口）。</summary>
    private readonly 弹窗监控服务? _弹窗监控;

    /// <summary>M 区地址校验用解析器。</summary>
    private readonly 地址解析器接口? _解析器;

    public 设置视图模型(
        PLC连接选项 PLC选项,
        轮询选项 轮询选项,
        连接管理器接口 连接,
        PLC连接设置服务接口 设置,
        用户服务接口 用户,
        对话框服务接口 对话框集合,
        文件对话框服务接口 文件对话框,
        ILogger<设置视图模型> 日志,
        变量表服务接口? 变量表 = null,
        运行模式管理器接口? 运行模式 = null,
        界面选项? 界面选项 = null,
        弹窗监控服务? 弹窗监控 = null,
        地址解析器接口? 解析器 = null,
        扫码枪设置视图模型? 扫码枪设置 = null)
    {
        _PLC选项 = PLC选项;
        _轮询选项 = 轮询选项;
        _连接 = 连接;
        _设置 = 设置;
        _用户项 = 用户;
        _对话框 = 对话框集合;
        _文件对话框 = 文件对话框;
        _日志 = 日志;
        _变量表 = 变量表;
        _运行模式 = 运行模式;
        _界面选项 = 界面选项;
        _弹窗监控 = 弹窗监控;
        _解析器 = 解析器;
        扫码枪设置区 = 扫码枪设置;

        标题 = "系统设置";
        副标题 = "通信与采集参数";

        _连接.状态已变更 += (_, 状态值) => 在界面线程执行(() => 应用状态(状态值));
        _用户项.用户已变更 += (_, _) => 在界面线程执行(刷新权限);
        _设置.已保存 += (_, _) => 在界面线程执行(() =>
        {
            刷新连接显示();
            加载编辑字段();
        });

        if (_变量表 is not null)
        {
            _变量表.已重新导入 += (_, _) => 在界面线程执行(刷新变量表显示);
        }

        if (_运行模式 is not null)
        {
            _运行模式.模式已变更 += (_, _) => 在界面线程执行(() =>
            {
                刷新运行模式显示();
                刷新连接显示();
            });
        }

        应用状态(_连接.状态);
        刷新权限();
        加载编辑字段();
        刷新运行模式显示();
        刷新变量表显示();
    }

    // ── 通信配置（只读展示）───────────────────────────────────────────

    public string 主机 => _PLC选项.主机;
    public int 端口 => _PLC选项.端口;
    public string 单元标识 => _PLC选项.单元标识.ToString();
    public string 系列文本 => _PLC选项.系列 switch
    {
        PLC系列.小型PLC => "小型 PLC（EASY320 / EASY52X / H5U / H3U）",
        PLC系列.中等PLC => "中型 PLC（AM600 / AC800）",
        _ => "未知",
    };

    public string 连接超时文本 => $"{_PLC选项.连接超时毫秒} ms";
    public string 读取超时文本 => $"{_PLC选项.读取超时毫秒} ms";
    public string 心跳文本 => $"{_PLC选项.心跳间隔毫秒} ms";
    public string 最大寄存器文本 => $"{_PLC选项.每次请求最大寄存器数} 个 / 次";
    public string 自动重连文本 => _PLC选项.自动重连 ? "已启用" : "已禁用";

    // ── 采集配置 ───────────────────────────────────────────────────────

    public string 快间隔文本 => $"{_轮询选项.快间隔毫秒} ms";
    public string 慢间隔文本 => $"{_轮询选项.慢间隔毫秒} ms";
    public string 事件间隔文本 => $"{_轮询选项.事件间隔毫秒} ms";

    /// <summary>配置建议提示。</summary>
    public string 备注文本 =>
        "汇川 PLC 单次读寄存器上限 123、写上限 121，本程序按每块 100 个寄存器保守取值；" +
        "H5U 默认最多允许 8 个 Modbus TCP 客户端连接，故上位机只维持一个连接。";

    [RelayCommand]
    private async Task 异步弹窗设置()
    {
        if (_弹窗监控 is null || _解析器 is null)
        {
            _对话框.提示("弹窗设置", "弹窗服务未就绪，请重启程序后重试。");
            return;
        }

        var 结果 = 视图.弹窗设置对话框.显示(_弹窗监控, _解析器);

        if (结果 is null)
        {
            return;
        }

        if (await 界面命令
                .异步执行(_日志, _对话框, "保存弹窗设置", () => _弹窗监控.异步保存集合(结果))
                .ConfigureAwait(true))
        {
            _对话框.提示("弹窗设置", $"已保存 {结果.Count} 个弹窗，立即生效。");
        }
    }

    [RelayCommand]
    private void 测试连接()
    {
        var 状态值 = _连接.状态;
        _对话框.提示("连接状态测试",
            状态值 == 连接状态.已连接
                ? $"连接正常。\n\n目标：{主机}:{端口}\n模式：{模式文本}"
                : $"当前未连接。\n\n目标：{主机}:{端口}\n状态：{连接状态文本}\n\n程序会按指数退避自动重连。");
    }

    /// <summary>
    /// 保存连接参数（仅管理员）。
    ///
    /// <para>流程：输入解析 → 服务层范围校验 → 二次确认（连接会中断几秒）→
    /// 服务保存（落盘 + 更新共享参数 + 重启连接）→ 刷新展示。
    /// 解析失败在本层指出具体字段；范围校验以服务层为准——界面校验只是提前反馈，不是防线。</para>
    /// </summary>
    [RelayCommand]
    private async Task 异步保存连接()
    {
        if (!可编辑连接)
        {
            _对话框.提示("权限不足", "只有管理员可以修改 PLC 连接参数。");
            return;
        }

        if (!尝试解析整数(编辑端口, "端口", out var 端口号) ||
            !尝试解析整数(编辑单元标识, "从站号", out var 单元标识值) ||
            !尝试解析整数(编辑连接超时, "连接超时", out var 连接超时) ||
            !尝试解析整数(编辑读取超时, "读写超时", out var 读取超时) ||
            !尝试解析整数(编辑心跳, "心跳间隔", out var 心跳) ||
            !尝试解析整数(编辑重连初始, "重连初始退避", out var 重连初始) ||
            !尝试解析整数(编辑重连最大, "重连退避上限", out var 重连最大) ||
            !尝试解析整数(编辑最大寄存器, "单次最大寄存器", out var 最大寄存器) ||
            !尝试解析整数(编辑OPCUA端口, "OPC UA 端口", out var opcua端口) ||
            !尝试解析整数(编辑命名空间, "OPC UA 命名空间索引", out var 命名空间) ||
            !尝试解析整数(编辑模拟从站端口, "模拟从站端口", out var 模拟从站端口))
        {
            return;
        }

        if (编辑协议索引 is < 0 or > 1)
        {
            _对话框.提示("参数有误", "通讯协议选择无效。");
            return;
        }

        var 原协议 = _PLC选项.通讯协议;

        var 候选 = new PLC连接编辑模型
        {
            主机 = 编辑主机,
            端口 = 端口号,
            单元标识 = 单元标识值,
            连接超时毫秒 = 连接超时,
            读取超时毫秒 = 读取超时,
            心跳间隔毫秒 = 心跳,
            重连初始延迟毫秒 = 重连初始,
            重连最大延迟毫秒 = 重连最大,
            每次请求最大寄存器数 = 最大寄存器,
            自动重连 = 编辑自动重连,
            通讯协议 = (通讯协议)编辑协议索引,
            OPCUA端口 = opcua端口,
            OPCUA命名空间索引 = 命名空间,
            模拟从站端口 = 模拟从站端口,
            模拟从站监听地址 = 编辑模拟从站监听地址,
        };

        var 错误列表 = _设置.校验(候选);
        if (错误列表.Count > 0)
        {
            _对话框.提示("参数有误", string.Join("\n\n", 错误列表));
            return;
        }

        // 协议切换属重启级变更：保存前必须知情确认
        if (候选.通讯协议 != 原协议)
        {
            var 目标名称 = 候选.通讯协议 == 通讯协议.OPCUA ? "OPC UA" : "Modbus TCP";

            if (!_对话框.确认("切换通讯协议",
                    $"确认把通讯模式从 {原协议} 切换为 {目标名称}？",
                    视图.确认等级.警告, "保存并重启程序生效",
                    "协议由程序装配定型：保存写入配置文件，重启程序后按新协议连接；\n"
                    + "当前会话仍按旧协议运行。OPC UA 端口与命名空间也一并写入。"))
            {
                return;
            }
        }

        // 改 IP / 端口会断开当前连接：如果是生产中的连接，属于必须知情的中断
        if (!_对话框.确认("变更 PLC 连接",
                $"确认把连接目标改为 {候选.主机.Trim()}:{候选.端口}（从站号 {候选.单元标识}）？",
                视图.确认等级.警告, "保存并重连",
                "保存后会断开当前连接并用新参数重连，几秒内恢复；期间采集与写入暂停。\n"
                + "参数同时写入 config/appsettings.json，重启程序后仍生效。"))
        {
            return;
        }

        try
        {
            // 不加 ConfigureAwait(false)：后续弹窗与展示刷新需要回到 UI 线程
            await _设置.异步保存(候选);
        }
        catch (InvalidOperationException 异常)
        {
            _对话框.提示("参数有误", 异常.Message);
            return;
        }
        catch (Exception 异常)
        {
            _日志.LogError(异常, "保存 PLC 连接参数失败");
            _对话框.提示("保存失败",
                $"参数已校验通过，但写入配置文件失败：\n{异常.Message}\n\n请检查磁盘与权限后重试。");
            return;
        }

        if (候选.通讯协议 != 原协议)
        {
            _对话框.提示("已保存，重启后切换协议",
                $"通讯模式已写入配置文件：{候选.通讯协议}。\n\n"
                + "协议由程序装配定型，请重启程序以按新协议连接；\n"
                + "重启前当前会话仍按原协议运行。");
            return;
        }

        _对话框.提示("已生效",
            $"PLC 连接参数已保存并生效：{候选.主机.Trim()}:{候选.端口}。\n\n连接正在用新参数重连。");
    }

    /// <summary>把当前生效参数灌入编辑框（初始化 / 保存成功后调用）。</summary>
    private void 加载编辑字段()
    {
        var 模型对象 = _设置.创建编辑模型();
        编辑主机 = 模型对象.主机;
        编辑端口 = 模型对象.端口.ToString();
        编辑单元标识 = 模型对象.单元标识.ToString();
        编辑连接超时 = 模型对象.连接超时毫秒.ToString();
        编辑读取超时 = 模型对象.读取超时毫秒.ToString();
        编辑心跳 = 模型对象.心跳间隔毫秒.ToString();
        编辑重连初始 = 模型对象.重连初始延迟毫秒.ToString();
        编辑重连最大 = 模型对象.重连最大延迟毫秒.ToString();
        编辑最大寄存器 = 模型对象.每次请求最大寄存器数.ToString();
        编辑自动重连 = 模型对象.自动重连;
        编辑协议索引 = (int)模型对象.通讯协议;
        编辑OPCUA端口 = 模型对象.OPCUA端口.ToString();
        编辑命名空间 = 模型对象.OPCUA命名空间索引.ToString();
        编辑模拟从站端口 = 模型对象.模拟从站端口.ToString();
        编辑模拟从站监听地址 = 模型对象.模拟从站监听地址;
    }

    /// <summary>刷新只读展示区（这些属性直接读共享单例，保存后需要手动通知）。</summary>
    private void 刷新连接显示()
    {
        OnPropertyChanged(nameof(主机));
        OnPropertyChanged(nameof(端口));
        OnPropertyChanged(nameof(单元标识));
        OnPropertyChanged(nameof(连接超时文本));
        OnPropertyChanged(nameof(读取超时文本));
        OnPropertyChanged(nameof(心跳文本));
        OnPropertyChanged(nameof(最大寄存器文本));
        OnPropertyChanged(nameof(自动重连文本));
        OnPropertyChanged(nameof(模式文本));
    }

    private void 刷新权限()
    {
        // 改 PLC 连接目标是高危操作：只给管理员
        可编辑连接 = _用户项.有(权限.编辑PLC连接);
        可导入变量表 = _用户项.有(权限.导入变量表);

        // 运行模式的"可切换"也含权限维度：身份变化后要重算
        刷新运行模式显示();
    }

    /// <summary>解析整数输入框，失败时弹窗指出具体字段并返回 false。</summary>
    private bool 尝试解析整数(string 文本, string 标签名, out int 值)
    {
        if (int.TryParse(文本?.Trim(), out 值))
        {
            return true;
        }

        值 = 0;
        _对话框.提示("输入有误", $"「{标签名}」必须是整数，当前输入：{文本}");
        return false;
    }

    private void 应用状态(连接状态 状态值)
    {
        (连接状态文本, 连接状态颜色键) = 状态值 switch
        {
            连接状态.已连接 => ("已连接", "SuccessBrush"),
            连接状态.连接中 => ("连接中", "WarningBrush"),
            连接状态.重连中 => ("重连中", "WarningBrush"),
            连接状态.故障 => ("连接故障", "DangerBrush"),
            _ => ("未连接", "MutedBrush"),
        };
    }
}
