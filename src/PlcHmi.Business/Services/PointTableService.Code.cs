using Microsoft.Extensions.Logging;
using 上位机.通信.Modbus;
using 上位机.通信.配方;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// <see cref="点表服务"/> 的「指令码」部分（partial）。
///
/// <para><b>2026-10-21 现场要求：</b>点胶示教页的指令码改成下拉选单（00 结束 … 09 对针），
/// 选中即把码值写进该点的指令码字（第 n 点 = <c>D3400 + n - 1</c>，例：第 2 点选「直线」→ D3401 = 2）。
/// 这条路径与「下发点表」的区别是<b>只动指令码这一个字</b>：
/// 不会把尚未示教的坐标（0）带下 PLC，也不会校验坐标范围。</para>
///
/// <para><b>写入顺序（先 PLC、后内存）：</b>PLC 写失败时内存与下拉框都不改，
/// 界面显示与 PLC 实际值保持一致——工控界面上"看起来选好了、其实没写进去"是最坏的假象。</para>
/// </summary>
public sealed partial class 点表服务
{
    /// <inheritdoc />
    public async Task 异步写入指令码(string 键, int 序号, int 码, CancellationToken 取消令牌 = default)
    {
        _权限.要求(权限.编辑示教);

        var 表项 = 表(键);
        var 映射项 = _映射[表项.键];

        if (!映射项.序号合法(序号))
        {
            throw new ArgumentOutOfRangeException(nameof(序号), 序号, "点序号超出范围。");
        }

        var 列项 = 表项.查找列(点表定义.指令码列键)
            ?? throw new InvalidOperationException(
                $"点表「{表项.显示名称}」没有指令码列，无法写入指令码。");

        if (!_客户端.是否已连接)
        {
            throw new InvalidOperationException("PLC 未连接，无法写入指令码。");
        }

        var 地址值 = 映射项.地址(序号, 列项.键);
        var 寄存器列表 = 寄存器编解码器.编码(列项.数据类型, 码, 字顺序.小字节序);

        await _客户端
            .异步写入多个寄存器(地址值, 寄存器列表, 取消令牌)
            .ConfigureAwait(false);

        设置点值(键, 序号, 列项.键, 码);

        // 只并入指令码这一列：操作员可能刚在界面上改了坐标还没下发，
        // 顺手并入会把"没下发的编辑"存进机种快照，下次进页面看起来像是已下发（误导现场）
        await 异步并入机种快照(表项, [序号], 取消令牌, [列项.键]).ConfigureAwait(false);

        _日志.LogInformation(
            "{Table} 第 {Point} 点指令码写入 {Code}（D{Address}）",
            表项.显示名称, 序号 + 1, 码, 地址值);
    }
}
