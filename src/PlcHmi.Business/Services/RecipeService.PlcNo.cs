using Microsoft.Extensions.Logging;
using 上位机.通信.Modbus;

namespace 上位机.业务.服务;

/// <summary>
/// <see cref="配方服务"/> 的「开机读一次 PLC 机种号」部分（partial）。
///
/// <para><b>2026-10-21 现场要求：</b>机种区段（D2500–D3999）整体不再参与周期采集，
/// <c>RecipeNo</c> 标签也不会再被轮询刷新。但"当前机种以 PLC 为准"这条不能丢——
/// PLC 停在 5 号机种、上位机却按本地记录的 1 号示教，数据会写进错误的机种。</para>
///
/// <para>因此这里在开机（以及连接建立后补做的那次初始化）<b>直接读一次</b>机种号寄存器。
/// 只读一次、不周期回读——正好符合"机种区不做自动回读"的口径。</para>
///
/// <para><b>读哪个地址：</b>用 <c>recipe-config.json</c> 的 <c>控件.配方号地址</c>（缺省 D3330）——
/// 与切换机种时写入的是同一个寄存器，读写不会各指一处。</para>
/// </summary>
public sealed partial class 配方服务
{
    /// <summary>读 PLC 的当前机种号；未连接 / 读失败 / 值超范围时返回 0（调用方回退本地记录）。</summary>
    private async Task<int> 异步读取PLC机种号(CancellationToken 取消令牌)
    {
        if (!_客户端.是否已连接)
        {
            return 0;
        }

        var 地址值 = _配置.控件.配方号地址;

        try
        {
            var 寄存器 = await _客户端
                .异步读取保持寄存器(地址值, 1, 取消令牌)
                .ConfigureAwait(false);

            if (寄存器.Length < 1)
            {
                return 0;
            }

            var 号 = 寄存器[0];
            return 号 >= 1 && 号 <= _配置.配方数量 ? 号 : 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException 异常)
        {
            _日志.LogWarning(异常, "读取 PLC 机种号失败（D{Address}），沿用本地记录", 地址值);
            return 0;
        }
        catch (TimeoutException 异常)
        {
            _日志.LogWarning(异常, "读取 PLC 机种号超时（D{Address}），沿用本地记录", 地址值);
            return 0;
        }
        catch (InvalidOperationException 异常)
        {
            _日志.LogWarning(异常, "读取 PLC 机种号被拒绝（D{Address}），沿用本地记录", 地址值);
            return 0;
        }
        catch (System.Net.Sockets.SocketException 异常)
        {
            _日志.LogWarning(异常, "读取 PLC 机种号时网络异常（D{Address}），沿用本地记录", 地址值);
            return 0;
        }
    }
}
