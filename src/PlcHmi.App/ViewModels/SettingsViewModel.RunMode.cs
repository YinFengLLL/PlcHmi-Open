using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using 上位机.应用.服务;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.应用.视图模型;

/// <summary>
/// 系统设置页 · 运行模式区（真实 PLC ↔ 模拟 PLC，仅 Modbus 模式、仅管理员）。
///
/// <para><b>为什么单独一个文件：</b>运行模式是"与谁通信"的开关，和连接参数编辑是两件事；
/// 而且它是唯一一处<b>不需要重启</b>就能改变通信对象的入口，值得独立成篇。
/// 另外本类已接近 400 行上限（见 <c>tools/check_thresholds.py</c>），
/// 按"改动超标文件时顺手把职责拆出去"的规矩分出来。</para>
///
/// <para><b>不跨重启记忆：</b>切换不写配置文件，重启程序一律回到真实 PLC。</para>
/// </summary>
public sealed partial class 设置视图模型
{
    // 模拟从站（Modbus 模式、模拟运行期供外部工具离线调试）
    [ObservableProperty] private string _编辑模拟从站端口 = string.Empty;
    [ObservableProperty] private string _编辑模拟从站监听地址 = string.Empty;

    // 运行模式（仅 Modbus 模式可运行期切换）
    [ObservableProperty] private bool _可切换运行模式;
    [ObservableProperty] private int _编辑运行模式索引;
    [ObservableProperty] private string _运行模式文本 = "真实 PLC";
    [ObservableProperty] private string _运行模式说明 = string.Empty;
    [ObservableProperty] private string _模拟从站状态文本 = "未启动";
    [ObservableProperty] private string _模拟从站端点文本 = string.Empty;

    /// <summary>运行模式下拉选项（索引与 运行模式 枚举一一对应）。</summary>
    public string[] 运行模式选项 { get; } = ["真实 PLC", "模拟 PLC（虚拟设备）"];

    /// <summary>当前运行模式（模拟 / 真实）。运行模式可在 Modbus 模式运行期切换，故取管理器的实时值。</summary>
    public string 模式文本
    {
        get
        {
            var 模式 = _运行模式?.当前模式
                       ?? (_PLC选项.使用模拟器 ? 运行模式.模拟PLC : 运行模式.真实PLC);

            return 模式 == 运行模式.模拟PLC ? "模拟模式（虚拟 PLC）" : "真实 PLC";
        }
    }

    /// <summary>
    /// 切换运行模式（真实 PLC ↔ 模拟 PLC，仅 Modbus 模式、仅管理员）。
    ///
    /// <para>流程：可选性 / 权限 / 目标校验 → 二次确认（切到模拟即脱离真实设备，必须知情）→
    /// 服务层切换（断连接 → 换实现 → 起停从站 → 重连）→ 刷新展示。</para>
    ///
    /// <para>切换<b>不写配置文件</b>，重启程序即回到真实 PLC（安全约定）。</para>
    /// </summary>
    [RelayCommand]
    private async Task 异步切换运行模式()
    {
        if (_运行模式 is null || !_运行模式.可在运行期切换)
        {
            _对话框.提示("不可用", "当前通讯协议不支持运行期切换运行模式；请在上方「通讯模式」中切换协议并重启程序。");
            return;
        }

        if (!_用户项.有(权限.切换运行模式))
        {
            _对话框.提示("权限不足", "只有管理员可以切换运行模式。");
            return;
        }

        if (编辑运行模式索引 is < 0 or > 1)
        {
            _对话框.提示("参数有误", "运行模式选择无效。");
            return;
        }

        var 目标 = (运行模式)编辑运行模式索引;

        if (目标 == _运行模式.当前模式)
        {
            _对话框.提示("无需切换", $"当前已是「{运行模式文本}」。");
            return;
        }

        if (目标 == 运行模式.模拟PLC)
        {
            if (!_对话框.确认("切换到模拟 PLC 模式",
                    "确认把运行模式切换为「模拟 PLC（虚拟设备）」？",
                    视图.确认等级.警告, "切换到模拟模式",
                    "切换后上位机将断开真实 PLC，改与内置虚拟 PLC 通信，并开放本地 Modbus 调试从站：\n"
                    + $"{_运行模式.模拟从站端点文本}\n"
                    + "可用 Modbus Poll / Modbus Slave 等工具连接进行离线调试。\n\n"
                    + "模拟模式不写入配置文件，重启程序即回到真实 PLC。"))
            {
                return;
            }
        }
        else if (!_对话框.确认("切换回真实 PLC 模式",
                     "确认把运行模式切换为「真实 PLC」？",
                     视图.确认等级.警告, "切换并连接真实 PLC",
                     $"切换后将停止模拟从站，并重新连接 {主机}:{端口}；"
                     + "期间采集与写入暂停，几秒内恢复。"))
        {
            return;
        }

        try
        {
            是否忙碌 = true;

            // 失败语义统一走 界面命令.异步执行（记日志 + 弹错误框，不向上抛）。
            // 从站监听失败等 InvalidOperationException 的原始消息由它原样呈现给管理员。
            if (!await 界面命令
                    .异步执行(_日志, _对话框, "切换运行模式", () => _运行模式.异步切换(目标))
                    .ConfigureAwait(true))
            {
                return;
            }

            _对话框.提示("已切换",
                $"运行模式已切换为「{运行模式文本}」。\n\n"
                + (目标 == 运行模式.模拟PLC
                    ? $"模拟调试从站：{_运行模式.模拟从站端点文本}\n"
                      + "可用标准 Modbus TCP 工具连接进行离线读写调试。"
                    : "已重新连接真实 PLC。"));
        }
        finally
        {
            是否忙碌 = false;
            刷新运行模式显示();
            刷新连接显示();
        }
    }

    /// <summary>刷新运行模式卡片（当前模式、可切换性、模拟从站状态）。</summary>
    private void 刷新运行模式显示()
    {
        var 模式 = _运行模式?.当前模式
                   ?? (_PLC选项.使用模拟器 ? 运行模式.模拟PLC : 运行模式.真实PLC);

        运行模式文本 = 模式 == 运行模式.模拟PLC ? "模拟 PLC（虚拟设备）" : "真实 PLC";
        编辑运行模式索引 = (int)模式;

        var 可切换 = _运行模式?.可在运行期切换 == true;

        可切换运行模式 = 可切换 && _用户项.有(权限.切换运行模式);

        运行模式说明 = 可切换
            ? "Modbus 模式支持在运行期切换「真实 PLC / 模拟 PLC」，无需重启；"
              + "切换不跨重启记忆，每次启动一律进入真实 PLC。"
            : "当前通讯协议（OPC UA）不支持运行期切换运行模式：请在上方「通讯模式」中切换并重启程序。";

        模拟从站端点文本 = _运行模式?.模拟从站端点文本 ?? string.Empty;
        模拟从站状态文本 = _运行模式?.模拟从站状态文本 ?? "未启动";
    }
}
