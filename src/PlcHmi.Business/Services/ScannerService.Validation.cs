using PlcHmi.Communication.Modbus;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// <see cref="ScannerService"/> 的「地址解析与参数校验」部分（partial）。
///
/// <para>拆出来的原因：主文件（生命周期 / 通道 / 握手）已到 400 行规模门禁；
/// 校验规则是纯函数性质的一块，改校验不必读整条扫码链路。</para>
/// </summary>
public sealed partial class ScannerService
{
    // ── 校验与地址 ──────────────────────────────────────────────────────

    private void RefreshRegisterAddress()
    {
        _successRegister = TryParseRegister(_config.ScanSuccessRegister, out var Success) ? Success : 0;
        _triggerRegister = TryParseRegister(_config.ScanTriggerRegister, out var Trigger) ? Trigger : 0;
    }

    private bool TryParseRegister(string? Text, out int Address)
    {
        Address = 0;

        if (string.IsNullOrWhiteSpace(Text))
        {
            return false;
        }

        if (!_parser.TryParse(Text.Trim(), out var Value) || Value is null)
        {
            return false;
        }

        if (Value.Area != ModbusArea.HoldingRegister || Value.BitIndex >= 0)
        {
            return false;
        }

        Address = Value.ModbusAddress;
        return true;
    }

    private void Validate(ScannerConfig candidate)
    {
        var error = new List<string>();

        if (candidate.CommunicationMode == ScanCommunicationMode.SerialPort)
        {
            if (string.IsNullOrWhiteSpace(candidate.SerialPortName))
            {
                error.Add("串口号不能为空（如 COM3）。");
            }

            if (candidate.BaudRate is < 300 or > 921600)
            {
                error.Add("波特率必须在 300–921600 之间。");
            }

            if (candidate.DataBit is < 5 or > 8)
            {
                error.Add("数据位必须是 5–8。");
            }

            if (candidate.Validate?.Trim() is not ("无" or "奇" or "偶"))
            {
                error.Add("校验只能是 无 / 奇 / 偶。");
            }

            if (candidate.StopBits?.Trim() is not ("1" or "1.5" or "2"))
            {
                error.Add("停止位只能是 1 / 1.5 / 2。");
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(candidate.EthernetAddress))
            {
                error.Add("网口地址不能为空（扫码枪 IP）。");
            }

            if (candidate.EthernetPort is < 1 or > 65535)
            {
                error.Add("网口端口必须在 1–65535 之间。");
            }
        }

        if (!TryParseRegister(candidate.ScanSuccessRegister, out var Success))
        {
            error.Add($"扫码成功信号寄存器「{candidate.ScanSuccessRegister}」无效（应为 D 区字地址，如 D4050）。");
        }

        if (!TryParseRegister(candidate.ScanTriggerRegister, out var Trigger))
        {
            error.Add($"扫码触发寄存器「{candidate.ScanTriggerRegister}」无效（应为 D 区字地址，如 D4010）。");
        }

        if (Success > 0 && Success == Trigger)
        {
            error.Add("扫码成功信号寄存器与触发寄存器不能是同一个地址。");
        }

        if (candidate.PollingIntervalMilliseconds is < 100 or > 5000)
        {
            error.Add("轮询间隔必须在 100–5000 毫秒之间。");
        }

        if (candidate.TimeoutMilliseconds is < 200 or > 60000)
        {
            error.Add("扫码超时必须在 200–60000 毫秒（0.2–60 秒）之间。");
        }

        if (error.Count > 0)
        {
            throw new InvalidOperationException(string.Join("\n", error));
        }
    }
}
