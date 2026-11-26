using System.Globalization;
using 上位机.通信.Modbus;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// <see cref="点表服务"/> 的「当前浮高」部分（2026-10-24 现场要求）。
///
/// <para><b>为什么要单独一块：</b>浮高来自测高机构的一个独立 D 地址（REAL 占 2 字），
/// 既不是轴位置标签、也不参与周期采集。锁附页手动区要显示它，
/// 「保存坐标点」要把它写进选中点的「浮高」列——两处共用同一段读取逻辑。</para>
/// </summary>
public sealed partial class 点表服务
{
    /// <summary>浮高列键。</summary>
    private const string 浮高列键 = "浮高";

    /// <summary>
    /// 只有这张表参与「当前浮高」（2026-10-24 现场口径：<b>仅锁附示教</b>）。
    /// <para>点胶示教没有测高机构，保存坐标点也不该去写浮高——这里用表键显式限定，
    /// 不靠"点胶表恰好没配浮高地址"这种隐式条件兜底。</para>
    /// </summary>
    private const string 浮高表键 = "锁附";

    /// <summary>该表是否参与当前浮高（锁附表且配了浮高地址）。</summary>
    private static bool 是浮高表(点表定义 表项) =>
        表项.当前浮高地址 > 0
        && string.Equals(表项.键, 浮高表键, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<double?> 异步读取当前浮高(string 键, CancellationToken 取消令牌 = default)
    {
        var 表项 = 表(键);
        return await 异步读取浮高(表项, 取消令牌).ConfigureAwait(false);
    }

    /// <summary>
    /// 读当前浮高：未配置地址 / PLC 未连接 / 只读到一半都返回 <c>null</c>（不返回 0 假装读到了）。
    /// </summary>
    private async Task<double?> 异步读取浮高(点表定义 表项, CancellationToken 取消令牌)
    {
        if (!是浮高表(表项) || !_客户端.是否已连接)
        {
            return null;
        }

        var 寄存器列表 = await _客户端
            .异步读取保持寄存器(表项.当前浮高地址, 2, 取消令牌)
            .ConfigureAwait(false);

        if (寄存器列表.Length < 2)
        {
            return null;
        }

        return Convert.ToDouble(
            寄存器编解码器.解码(标签数据类型.Float32, 寄存器列表, 字顺序.小字节序),
            CultureInfo.InvariantCulture);
    }
}
