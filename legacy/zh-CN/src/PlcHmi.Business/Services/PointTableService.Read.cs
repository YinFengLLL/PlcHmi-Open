using System.Globalization;
using Microsoft.Extensions.Logging;
using 上位机.通信.Modbus;
using 上位机.通信.配方;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// <see cref="点表服务"/> 的「按需读取」部分（partial）。
///
/// <para><b>2026-10-21 现场要求：</b>机种区段（D2500–D3999）里的点表<b>不再自动回读</b>
/// （轮询引擎已跳过该区段），要看 PLC 现状必须由操作员在示教页按「读取点表」。
/// 这样示教页显示的一定是"上次装载 / 下发 / 手动读取"的值，不会在与 PLC 的写入拉锯中跳来跳去。</para>
///
/// <para><b>整表读成功才落内存：</b>列是一列一次块读；中途某一列失败就整体放弃，
/// 绝不留下"一半新一半旧"的点表——现场看到的那种表比不读更危险（改第 3 点会按错坐标定位）。</para>
/// </summary>
public sealed partial class 点表服务
{
    /// <inheritdoc />
    public async Task 异步读取点表(string 键, CancellationToken 取消令牌 = default)
    {
        // 2026-11-19 权限补齐（R5）：读回来会整体覆盖内存点表，
        // 操作员身份不该通过命令/脚本路径触达（界面隐藏按钮不是边界）。
        _权限.要求(权限.视图全部页面);

        var 表项 = 表(键);
        var 映射项 = _映射[表项.键];

        if (!_客户端.是否已连接)
        {
            throw new InvalidOperationException("PLC 未连接，无法读取点表。");
        }

        var 点数 = 映射项.点数;
        var 读回 = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var 列项 in 表项.列)
        {
            var 字数 = 映射项.列块字(列项.键);
            var 寄存器列表 = await _客户端
                .异步读取保持寄存器(列项.基址, 字数, 取消令牌)
                .ConfigureAwait(false);

            if (寄存器列表.Length < 字数)
            {
                throw new InvalidOperationException(
                    $"读取「{列项.显示名称}」时 PLC 只返回 {寄存器列表.Length}/{字数} 个字（D{列项.基址}）。");
            }

            var 值列表 = new double[点数];
            var 字缓冲 = new ushort[列项.字每点位];

            for (var 序号 = 0; 序号 < 点数; 序号++)
            {
                Array.Copy(寄存器列表, 序号 * 列项.字每点位, 字缓冲, 0, 列项.字每点位);

                var 已解码 = 寄存器编解码器.解码(列项.数据类型, 字缓冲, 字顺序.小字节序);
                值列表[序号] = Convert.ToDouble(已解码, CultureInfo.InvariantCulture);
            }

            读回[列项.键] = 值列表;
        }

        // 全部列都成功：一次性落到内存行（只发一次变更事件，界面不会逐格刷新）
        for (var 序号 = 0; 序号 < 点数; 序号++)
        {
            var 行项 = _缓存[表项.键][序号];

            foreach (var 列项 in 表项.列)
            {
                行项.设(列项.键, 读回[列项.键][序号]);
            }
        }

        点表已变更?.Invoke(this, 表项.键);

        _日志.LogInformation("已从 PLC 读取点表 {Table}（{Count} 点）", 表项.显示名称, 点数);
    }
}
