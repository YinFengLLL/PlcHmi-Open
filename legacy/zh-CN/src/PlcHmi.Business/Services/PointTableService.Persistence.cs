using System.Globalization;
using Microsoft.Extensions.Logging;
using 上位机.通信.Modbus;
using 上位机.通信.配方;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// <see cref="点表服务"/> 的「按机种快照装载 / 并入」部分（partial）。
///
/// <para><b>为什么不再有 point-tables.json：</b>现场口径是"<b>D2500–D3999 整段都是机种区域</b>"。
/// 点表数据既然整段属于机种，就不该再另存一份全局文件——那会变成两个真相源，
/// 切机种时到底听谁的都说不清。现在：</para>
/// <list type="bullet">
/// <item>PLC 的 D2500–D3999 = 当前机种生效的一份（运行时真相）；</item>
/// <item>机种快照（<c>recipes.json</c>）= 持久化真相，<b>由「下发点表」/「坐标示教」写完后自动并入</b>，
/// 也可在机种页「保存机种」从 PLC 整段读回。</item>
/// </list>
///
/// <para>页面装载是"从当前机种快照解码"，离线也能看到上次保存的数据。
/// <b>2026-10-16 修复：</b>此前下发点表只写 PLC 不动快照，于是"下发 → 切页 → 回来"看到的
/// 还是快照里的旧数据（必须先去机种页点「保存机种」才显形）——现场表现为"下发白做了"。</para>
/// </summary>
public sealed partial class 点表服务
{
    /// <summary>用当前机种的区段快照填充一张点表的内存行。</summary>
    private void 从机种快照填充(点表定义 表项)
    {
        var 快照 = _配方.区段快照(_配方.当前配方号);
        var 行列表 = _缓存[表项.键];
        var 映射项 = _映射[表项.键];

        foreach (var 行项 in 行列表)
        {
            行项.值.Clear();
        }

        foreach (var 列项 in 表项.列)
        {
            if (列项.基址 <= 0)
            {
                continue;
            }

            var 字缓冲 = new ushort[列项.字每点位];

            for (var 序号 = 0; 序号 < 映射项.点数 && 序号 < 行列表.Count; 序号++)
            {
                for (var 序 = 0; 序 < 列项.字每点位; 序++)
                {
                    var 地址 = 列项.基址 + 序号 * 列项.字每点位 + 序;

                    // 快照是稀疏的：没记录的字就是 0（与 PLC 里的实际读回一致）
                    字缓冲[序] = 快照.TryGetValue(地址 - 机种区段.起始, out var 值)
                        ? (ushort)值
                        : (ushort)0;
                }

                var 解码值 = 寄存器编解码器.解码(列项.数据类型, 字缓冲, 字顺序.小字节序);

                行列表[序号].设(列项.键, Convert.ToDouble(解码值, CultureInfo.InvariantCulture));
            }
        }
    }

    /// <summary>
    /// 把刚写进 PLC 的点表数据（指定序号集合）<b>并入当前机种快照</b>并落盘。
    ///
    /// <para><b>为什么必须并入（2026-10-16 现场问题）：</b>页面重新进入时是从
    /// <b>机种快照</b>装载的，而"下发点表 / 坐标示教"只写 PLC。两者不同步时，
    /// 操作员一下发就切页、再回来，看到的仍是快照里的旧数据（表现为"下发白做了"），
    /// 只能绕道机种页点「保存机种」整段读回才显形。</para>
    ///
    /// <para><b>为什么是并入而不是整段回读：</b>机种区段（D2500–D3999）里除了点表列
    /// 还有机种参数等别的数据。整段回读会把 PLC 里的旧值盖到那些本地未下发的数据上
    /// （操作员没下发的编辑会凭空消失）；只并入<b>本表各列覆盖到的字</b>，
    /// 既让"PLC 里刚写下的 = 快照里存着的"，又不动其它区段。</para>
    ///
    /// <para>值 0 也会并入：<c>异步写入机种快照</c> 按稀疏约定丢弃 0，于是被清空的点
    /// 在快照里也回到"未示教"——与 <see cref="从机种快照填充"/> 读不到即 0 对称。</para>
    /// </summary>
    private async Task 异步并入机种快照(
        点表定义 表项,
        IReadOnlyList<int> 序号集合,
        CancellationToken 取消令牌,
        IReadOnlyList<string>? 仅列键 = null)
    {
        var 映射项 = _映射[表项.键];
        var 号 = _配方.当前配方号;

        // 取现有快照的副本再改：整段替换由 异步写入机种快照 负责落盘
        var 快照 = new Dictionary<int, int>(_配方.区段快照(号));
        var 并入字数 = 0;

        foreach (var 序号 in 序号集合)
        {
            if (!映射项.序号合法(序号))
            {
                continue;
            }

            var 行项 = _缓存[表项.键][序号];

            foreach (var 列项 in 表项.列)
            {
                if (列项.基址 <= 0)
                {
                    continue;
                }

                // 只并入指定列（指令码下拉走这条）：其余列可能还是"改了没下发"的内存值
                if (仅列键 is not null
                    && !仅列键.Contains(列项.键, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var 寄存器列表 = 寄存器编解码器.编码(
                    列项.数据类型, 行项.取(列项.键), 字顺序.小字节序);

                for (var 序 = 0; 序 < 列项.字每点位 && 序 < 寄存器列表.Length; 序++)
                {
                    var 地址 = 列项.基址 + 序号 * 列项.字每点位 + 序;

                    // 只并入落在机种区段内的字：区段外的列不属于机种（防御性跳过）
                    if (!机种区段.包含(地址))
                    {
                        continue;
                    }

                    快照[地址 - 机种区段.起始] = 寄存器列表[序];
                    并入字数++;
                }
            }
        }

        await _配方.异步写入机种快照(号, 快照, 取消令牌).ConfigureAwait(false);

        _日志.LogInformation(
            "点表 {Table} 的 {Count} 个字已并入机种 {No} 的快照（页面重新进入即可见）",
            表项.显示名称, 并入字数, 号);
    }
}
