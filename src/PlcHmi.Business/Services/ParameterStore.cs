using System.Text.Json;
using Microsoft.Extensions.Logging;
using PlcHmi.Business.Configuration;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// 全局参数文件（<c>config/parameters.json</c>）的读写封装。
///
/// <para><b>为什么独立于机种：</b>现场口径是"只有 D2500–D3999 跟随机种切换，其余 D 区全局记忆"。
/// 轴速度参数落在 D1200 段，若继续存在机种条目里，切换机种会把它们一起换掉——
/// 那不是现场要的行为。所以参数单独一份文件，20 个机种共用。</para>
///
/// <para><b>单机单写：</b>与 <see cref="RecipeStore"/> 一样只保留一份内存真相，
/// 由 <see cref="RecipeService"/> 独占读写，避免"各持副本、后保存覆盖先保存"。</para>
/// </summary>
public sealed class ParameterStore : IDisposable
{
    private readonly object _sync = new();

    /// <summary>落盘串行化（异步写不能跨 await 持 lock）。</summary>
    private readonly SemaphoreSlim _writeGateValve = new(1, 1);

    private readonly ILogger<ParameterStore> _log;
    private readonly IConfigHealth? _health;

    private ParameterFile _file = new();

    /// <summary>构造函数即加载文件。</summary>
    public ParameterStore(ILogger<ParameterStore> Log, IConfigHealth? Health = null)
    {
        _log = Log;
        _health = Health;
        Load();
    }

    /// <summary>
    /// 参数值的内存真相（返回内部持有的可变字典，调用方修改即等于修改内存真相）。
    /// <para>调用方须保证串行访问。</para>
    /// </summary>
    public Dictionary<string, double> Parameter => _file.CurrentParameter;

    /// <summary>从磁盘加载；主文件损坏时回退 <c>.bak</c>，都不可用则空数据启动并登记降级。</summary>
    public void Load()
    {
        var resultValue = ConfigFileLoader.LoadJson<ParameterFile>(
            ConfigPath.ParameterCollection, JsonConfig.Read, _log);

        _health.Report("全局参数", resultValue);

        lock (_sync)
        {
            _file = resultValue.Value ?? new ParameterFile();

            // 兼容：老文件若把键写成非大小写不敏感字典，这里统一换成忽略大小写的
            _file.CurrentParameter = new Dictionary<string, double>(
                _file.CurrentParameter, StringComparer.OrdinalIgnoreCase);
        }

        if (resultValue.loadResult is LoadResultSplitCategory.Loaded)
        {
            _log.LogInformation("已加载 {Count} 项全局参数", _file.CurrentParameter.Count);
        }
        else if (resultValue.loadResult is LoadResultSplitCategory.Missing)
        {
            _log.LogWarning("未找到全局参数文件：{Path}，将使用配置默认值", ConfigPath.ParameterCollection);
        }
    }

    /// <summary>原子写回磁盘（必须串行：并发保存会互相覆盖）。</summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _writeGateValve.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            string jsonText;
            lock (_sync)
            {
                jsonText = JsonSerializer.Serialize(_file, JsonConfig.writeHumanStyle);
            }

            await AtomicFile.WriteAllTextAsync(ConfigPath.ParameterCollection, jsonText, cancellationToken).ConfigureAwait(false);
            _log.LogDebug("全局参数已保存到 {Path}", ConfigPath.ParameterCollection);
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "保存全局参数失败：{Path}", ConfigPath.ParameterCollection);
            throw;
        }
        finally
        {
            _writeGateValve.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _writeGateValve.Dispose();
}
