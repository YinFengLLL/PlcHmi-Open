using System.Globalization;
using Microsoft.Extensions.Logging;
using PlcHmi.Communication.Modbus;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// <see cref="RecipeService"/> 的「启动同步」部分（partial）。
///
/// <para>机种区段（D2500–D3999）以机种快照为准；区段之外的 D 区与 M 区<b>以 PLC 为准</b>——
/// 每次开启软件后从 PLC 读回来刷新即可。拆出来是为了守住主文件 400 行规模门禁。</para>
/// </summary>
public sealed partial class RecipeService
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
    private async Task<ConsistencyValidateReport> FromPlcSyncParameterAsync(CancellationToken cancellationToken)
    {
        var refreshItem = new List<string>();

        if (!_client.IsConnected)
        {
            _log.LogInformation("PLC 未连接，跳过启动全局参数同步（沿用本地缓存值）");
            return new ConsistencyValidateReport
            {
                IsConsistent = true,
                RecipeNumber = _currentNumber,
                AlreadyCheckedParameter = 0,
                AlreadyCheckedPoint = 0,
                Difference = refreshItem,
            };
        }

        var readGet = 0;

        foreach (var specItem in _config.Parameter)
        {
            try
            {
                // 按配置声明的类型决定读取字数：Float32 占 2 字，只读 1 字会拿半个值，必然解错
                var registerList = await _client
                    .ReadHoldingRegisterAsync(specItem.Address, specItem.RegisterCount, cancellationToken)
                    .ConfigureAwait(false);

                // 字数不足说明从站行为异常，跳过这一项而不是崩溃
                if (registerList.Length < specItem.RegisterCount)
                {
                    _log.LogWarning(
                        "读取 {Name} 时 PLC 只返回 {Got} 个字（D{Address} 需要 {Need} 个），跳过该项",
                        specItem.DisplayName, registerList.Length, specItem.Address, specItem.RegisterCount);
                    continue;
                }

                var decoded = RegisterCodec.Decode(specItem.DataType, registerList, WordOrder.LittleEndian);
                var plcValue = Convert.ToDouble(decoded, CultureInfo.InvariantCulture);

                // PLC 里被手工改成超范围值时，宁可保留本地值也不要让轴带着天文数字跑
                if (!specItem.IsInRange(plcValue))
                {
                    _log.LogWarning(
                        "{Name} 的 PLC 值 {Value:0.###} 超出范围 {Range}，保留本地值 {Local:0.###}",
                        specItem.DisplayName, plcValue, specItem.RangeText,
                        _parameterRepository.Parameter.TryGetValue(specItem.Key, out var retainValue) ? retainValue : specItem.Default);
                    continue;
                }

                var thisGroundValue = _parameterRepository.Parameter.TryGetValue(specItem.Key, out var OriginValue) ? OriginValue : specItem.Default;

                readGet++;

                if (Math.Abs(plcValue - thisGroundValue) > parameterTolerance)
                {
                    refreshItem.Add($"{specItem.DisplayName}：{thisGroundValue:0.###} → {plcValue:0.###}");
                }

                _parameterRepository.Parameter[specItem.Key] = plcValue;
            }
            catch (Exception exception)
            {
                _log.LogWarning(exception, "读取参数 {Key} 失败，跳过该项同步", specItem.Key);
            }
        }

        return new ConsistencyValidateReport
        {
            IsConsistent = refreshItem.Count == 0,
            RecipeNumber = _currentNumber,
            AlreadyCheckedParameter = readGet,
            AlreadyCheckedPoint = 0,
            Difference = refreshItem,
        };
    }

}
