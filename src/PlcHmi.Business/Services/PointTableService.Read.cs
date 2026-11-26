using System.Globalization;
using Microsoft.Extensions.Logging;
using PlcHmi.Communication.Modbus;
using PlcHmi.Communication.Recipe;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// <see cref="PointTableService"/> 的「按需读取」部分（partial）。
///
/// <para><b>2026-10-21 现场要求：</b>机种区段（D2500–D3999）里的点表<b>不再自动回读</b>
/// （轮询引擎已跳过该区段），要看 PLC 现状必须由操作员在示教页按「读取点表」。
/// 这样示教页显示的一定是"上次装载 / 下发 / 手动读取"的值，不会在与 PLC 的写入拉锯中跳来跳去。</para>
///
/// <para><b>整表读成功才落内存：</b>列是一列一次块读；中途某一列失败就整体放弃，
/// 绝不留下"一半新一半旧"的点表——现场看到的那种表比不读更危险（改第 3 点会按错坐标定位）。</para>
/// </summary>
public sealed partial class PointTableService
{
    /// <inheritdoc />
    public async Task ReadPointTableAsync(string Key, CancellationToken cancellationToken = default)
    {
        // 2026-11-19 权限补齐（R5）：读回来会整体覆盖内存点表，
        // 操作员身份不该通过命令/脚本路径触达（界面隐藏按钮不是边界）。
        _permission.WantRequire(Permission.ViewAllPage);

        var TableEntry = Table(Key);
        var mapItem = _map[TableEntry.Key];

        if (!_client.IsConnected)
        {
            throw new InvalidOperationException("PLC 未连接，无法读取点表。");
        }

        var PointCount = mapItem.PointCount;
        var ReadBackOnce = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var columnItem in TableEntry.Column)
        {
            var wordCount = mapItem.ColumnBlockWord(columnItem.Key);
            var registerList = await _client
                .ReadHoldingRegisterAsync(columnItem.BaseAddress, wordCount, cancellationToken)
                .ConfigureAwait(false);

            if (registerList.Length < wordCount)
            {
                throw new InvalidOperationException(
                    $"读取「{columnItem.DisplayName}」时 PLC 只返回 {registerList.Length}/{wordCount} 个字（D{columnItem.BaseAddress}）。");
            }

            var ValueList = new double[PointCount];
            var wordBuffer = new ushort[columnItem.WordPerPointBit];

            for (var Sequence = 0; Sequence < PointCount; Sequence++)
            {
                Array.Copy(registerList, Sequence * columnItem.WordPerPointBit, wordBuffer, 0, columnItem.WordPerPointBit);

                var decoded = RegisterCodec.Decode(columnItem.DataType, wordBuffer, WordOrder.LittleEndian);
                ValueList[Sequence] = Convert.ToDouble(decoded, CultureInfo.InvariantCulture);
            }

            ReadBackOnce[columnItem.Key] = ValueList;
        }

        // 全部列都成功：一次性落到内存行（只发一次变更事件，界面不会逐格刷新）
        for (var Sequence = 0; Sequence < PointCount; Sequence++)
        {
            var rowItem = _bufferStore[TableEntry.Key][Sequence];

            foreach (var columnItem in TableEntry.Column)
            {
                rowItem.setup(columnItem.Key, ReadBackOnce[columnItem.Key][Sequence]);
            }
        }

        pointTableChanged?.Invoke(this, TableEntry.Key);

        _log.LogInformation("已从 PLC 读取点表 {Table}（{Count} 点）", TableEntry.DisplayName, PointCount);
    }
}
