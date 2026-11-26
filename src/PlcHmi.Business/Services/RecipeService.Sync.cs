using System.Globalization;
using Microsoft.Extensions.Logging;
using 上位机.通信.Modbus;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// <see cref="配方服务"/> 的「启动同步」部分（partial）。
///
/// <para>机种区段（D2500–D3999）以机种快照为准；区段之外的 D 区与 M 区<b>以 PLC 为准</b>——
/// 每次开启软件后从 PLC 读回来刷新即可。拆出来是为了守住主文件 400 行规模门禁。</para>
/// </summary>
public sealed partial class 配方服务
{
    // ── 启动同步（机种区外的 D 区 / M 区） ──────────────────────────────

    /// <summary>
    /// 启动时把<b>全局参数</b>从 PLC 读回来刷新内存并落盘留底。
    ///
    /// <para><b>权威方向（现场口径 2026-10-12）：</b>机种区段（D2500–D3999）由机种快照管；
    /// 区段之外的 D 区（轴速度参数、标定、其它点、动作指令）与 M 区的运行数据，
    /// 一律"<b>每次开启软件后从 PLC 读取刷新即可</b>"——所以这里不是"本地为准的比对"，
    /// 而是直接读回覆盖。M 区由轮询引擎持续刷新，不在这里处理。</para>
    ///
    /// <para><b>单项容错：</b>某一项读数非法（超范围 / 字数不足 / 通信抖断）时只跳过该项并记警告，
    /// 不整体拒绝——PLC 里某个残留字不该把整套参数卡住。</para>
    /// </summary>
    private async Task<一致性校验报告> 异步从PLC同步参数(CancellationToken 取消令牌)
    {
        var 刷新项 = new List<string>();

        if (!_客户端.是否已连接)
        {
            _日志.LogInformation("PLC 未连接，跳过启动全局参数同步（沿用本地缓存值）");
            return new 一致性校验报告
            {
                是否一致 = true,
                配方号 = _当前号,
                已勾选参数 = 0,
                已勾选点位 = 0,
                差异 = 刷新项,
            };
        }

        var 已读取 = 0;

        foreach (var 规格项 in _配置.参数)
        {
            try
            {
                // 按配置声明的类型决定读取字数：Float32 占 2 字，只读 1 字会拿半个值，必然解错
                var 寄存器列表 = await _客户端
                    .异步读取保持寄存器(规格项.地址, 规格项.寄存器数量, 取消令牌)
                    .ConfigureAwait(false);

                // 字数不足说明从站行为异常，跳过这一项而不是崩溃
                if (寄存器列表.Length < 规格项.寄存器数量)
                {
                    _日志.LogWarning(
                        "读取 {Name} 时 PLC 只返回 {Got} 个字（D{Address} 需要 {Need} 个），跳过该项",
                        规格项.显示名称, 寄存器列表.Length, 规格项.地址, 规格项.寄存器数量);
                    continue;
                }

                var 已解码 = 寄存器编解码器.解码(规格项.数据类型, 寄存器列表, 字顺序.小字节序);
                var PLC值 = Convert.ToDouble(已解码, CultureInfo.InvariantCulture);

                // PLC 里被手工改成超范围值时，宁可保留本地值也不要让轴带着天文数字跑
                if (!规格项.是否在范围内(PLC值))
                {
                    _日志.LogWarning(
                        "{Name} 的 PLC 值 {Value:0.###} 超出范围 {Range}，保留本地值 {Local:0.###}",
                        规格项.显示名称, PLC值, 规格项.范围文本,
                        _参数库.参数.TryGetValue(规格项.键, out var 保留值) ? 保留值 : 规格项.默认);
                    continue;
                }

                var 本地值 = _参数库.参数.TryGetValue(规格项.键, out var 原值) ? 原值 : 规格项.默认;

                已读取++;

                if (Math.Abs(PLC值 - 本地值) > 参数容差)
                {
                    刷新项.Add($"{规格项.显示名称}：{本地值:0.###} → {PLC值:0.###}");
                }

                _参数库.参数[规格项.键] = PLC值;
            }
            catch (Exception 异常)
            {
                _日志.LogWarning(异常, "读取参数 {Key} 失败，跳过该项同步", 规格项.键);
            }
        }

        return new 一致性校验报告
        {
            是否一致 = 刷新项.Count == 0,
            配方号 = _当前号,
            已勾选参数 = 已读取,
            已勾选点位 = 0,
            差异 = 刷新项,
        };
    }

}
