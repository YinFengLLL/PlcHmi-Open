using System.Globalization;
using PlcHmi.Communication.Modbus;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// <see cref="PointTableService"/> 的「当前浮高」部分（2026-10-24 现场要求）。
///
/// <para><b>为什么要单独一块：</b>浮高来自测高机构的一个独立 D 地址（REAL 占 2 字），
/// 既不是轴位置标签、也不参与周期采集。锁附页手动区要显示它，
/// 「保存坐标点」要把它写进选中点的「浮高」列——两处共用同一段读取逻辑。</para>
/// </summary>
public sealed partial class PointTableService
{
    /// <summary>浮高列键。</summary>
    private const string floatHeightColumnKey = "浮高";

    /// <summary>
    /// 只有这张表参与「当前浮高」（2026-10-24 现场口径：<b>仅锁附示教</b>）。
    /// <para>点胶示教没有测高机构，保存坐标点也不该去写浮高——这里用表键显式限定，
    /// 不靠"点胶表恰好没配浮高地址"这种隐式条件兜底。</para>
    /// </summary>
    private const string floatHeightTableKey = "锁附";

    /// <summary>该表是否参与当前浮高（锁附表且配了浮高地址）。</summary>
    private static bool IsFloatHeightTable(PointTableDefinition TableEntry) =>
        TableEntry.CurrentFloatHeightAddress > 0
        && string.Equals(TableEntry.Key, floatHeightTableKey, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<double?> ReadCurrentFloatHeightAsync(string Key, CancellationToken cancellationToken = default)
    {
        var TableEntry = Table(Key);
        return await ReadFloatHeightAsync(TableEntry, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 读当前浮高：未配置地址 / PLC 未连接 / 只读到一半都返回 <c>null</c>（不返回 0 假装读到了）。
    /// </summary>
    private async Task<double?> ReadFloatHeightAsync(PointTableDefinition TableEntry, CancellationToken cancellationToken)
    {
        if (!IsFloatHeightTable(TableEntry) || !_client.IsConnected)
        {
            return null;
        }

        var registerList = await _client
            .ReadHoldingRegisterAsync(TableEntry.CurrentFloatHeightAddress, 2, cancellationToken)
            .ConfigureAwait(false);

        if (registerList.Length < 2)
        {
            return null;
        }

        return Convert.ToDouble(
            RegisterCodec.Decode(TagDataType.Float32, registerList, WordOrder.LittleEndian),
            CultureInfo.InvariantCulture);
    }
}
