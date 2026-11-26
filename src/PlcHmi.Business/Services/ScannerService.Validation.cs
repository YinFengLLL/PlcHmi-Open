using 上位机.通信.Modbus;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// <see cref="扫码枪服务"/> 的「地址解析与参数校验」部分（partial）。
///
/// <para>拆出来的原因：主文件（生命周期 / 通道 / 握手）已到 400 行规模门禁；
/// 校验规则是纯函数性质的一块，改校验不必读整条扫码链路。</para>
/// </summary>
public sealed partial class 扫码枪服务
{
    // ── 校验与地址 ──────────────────────────────────────────────────────

    private void 刷新寄存器地址()
    {
        _成功寄存器 = 尝试解析寄存器(_配置.扫码成功寄存器, out var 成功) ? 成功 : 0;
        _触发寄存器 = 尝试解析寄存器(_配置.扫码触发寄存器, out var 触发) ? 触发 : 0;
    }

    private bool 尝试解析寄存器(string? 文本, out int 地址)
    {
        地址 = 0;

        if (string.IsNullOrWhiteSpace(文本))
        {
            return false;
        }

        if (!_解析器.尝试解析(文本.Trim(), out var 值) || 值 is null)
        {
            return false;
        }

        if (值.区域 != Modbus区域.保持寄存器 || 值.位序号 >= 0)
        {
            return false;
        }

        地址 = 值.Modbus地址;
        return true;
    }

    private void 校验(扫码枪配置 候选)
    {
        var 错误 = new List<string>();

        if (候选.通讯模式 == 扫码通讯模式.串口)
        {
            if (string.IsNullOrWhiteSpace(候选.串口号))
            {
                错误.Add("串口号不能为空（如 COM3）。");
            }

            if (候选.波特率 is < 300 or > 921600)
            {
                错误.Add("波特率必须在 300–921600 之间。");
            }

            if (候选.数据位 is < 5 or > 8)
            {
                错误.Add("数据位必须是 5–8。");
            }

            if (候选.校验?.Trim() is not ("无" or "奇" or "偶"))
            {
                错误.Add("校验只能是 无 / 奇 / 偶。");
            }

            if (候选.停止位?.Trim() is not ("1" or "1.5" or "2"))
            {
                错误.Add("停止位只能是 1 / 1.5 / 2。");
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(候选.网口地址))
            {
                错误.Add("网口地址不能为空（扫码枪 IP）。");
            }

            if (候选.网口端口 is < 1 or > 65535)
            {
                错误.Add("网口端口必须在 1–65535 之间。");
            }
        }

        if (!尝试解析寄存器(候选.扫码成功寄存器, out var 成功))
        {
            错误.Add($"扫码成功信号寄存器「{候选.扫码成功寄存器}」无效（应为 D 区字地址，如 D4050）。");
        }

        if (!尝试解析寄存器(候选.扫码触发寄存器, out var 触发))
        {
            错误.Add($"扫码触发寄存器「{候选.扫码触发寄存器}」无效（应为 D 区字地址，如 D4010）。");
        }

        if (成功 > 0 && 成功 == 触发)
        {
            错误.Add("扫码成功信号寄存器与触发寄存器不能是同一个地址。");
        }

        if (候选.轮询间隔毫秒 is < 100 or > 5000)
        {
            错误.Add("轮询间隔必须在 100–5000 毫秒之间。");
        }

        if (候选.超时毫秒 is < 200 or > 60000)
        {
            错误.Add("扫码超时必须在 200–60000 毫秒（0.2–60 秒）之间。");
        }

        if (错误.Count > 0)
        {
            throw new InvalidOperationException(string.Join("\n", 错误));
        }
    }
}
