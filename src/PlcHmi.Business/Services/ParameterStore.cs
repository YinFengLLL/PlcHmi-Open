using System.Text.Json;
using Microsoft.Extensions.Logging;
using 上位机.业务.配置;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// 全局参数文件（<c>config/parameters.json</c>）的读写封装。
///
/// <para><b>为什么独立于机种：</b>现场口径是"只有 D2500–D3999 跟随机种切换，其余 D 区全局记忆"。
/// 轴速度参数落在 D1200 段，若继续存在机种条目里，切换机种会把它们一起换掉——
/// 那不是现场要的行为。所以参数单独一份文件，20 个机种共用。</para>
///
/// <para><b>单机单写：</b>与 <see cref="配方存储"/> 一样只保留一份内存真相，
/// 由 <see cref="配方服务"/> 独占读写，避免"各持副本、后保存覆盖先保存"。</para>
/// </summary>
public sealed class 参数存储 : IDisposable
{
    private readonly object _同步 = new();

    /// <summary>落盘串行化（异步写不能跨 await 持 lock）。</summary>
    private readonly SemaphoreSlim _写入闸门 = new(1, 1);

    private readonly ILogger<参数存储> _日志;
    private readonly 配置健康接口? _健康;

    private 参数文件 _文件 = new();

    /// <summary>构造函数即加载文件。</summary>
    public 参数存储(ILogger<参数存储> 日志, 配置健康接口? 健康 = null)
    {
        _日志 = 日志;
        _健康 = 健康;
        加载();
    }

    /// <summary>
    /// 参数值的内存真相（返回内部持有的可变字典，调用方修改即等于修改内存真相）。
    /// <para>调用方须保证串行访问。</para>
    /// </summary>
    public Dictionary<string, double> 参数 => _文件.当前参数;

    /// <summary>从磁盘加载；主文件损坏时回退 <c>.bak</c>，都不可用则空数据启动并登记降级。</summary>
    public void 加载()
    {
        var 结果值 = 配置文件加载器.加载JSON<参数文件>(
            配置路径.参数集合, JSON配置.读取, _日志);

        _健康.上报("全局参数", 结果值);

        lock (_同步)
        {
            _文件 = 结果值.值 ?? new 参数文件();

            // 兼容：老文件若把键写成非大小写不敏感字典，这里统一换成忽略大小写的
            _文件.当前参数 = new Dictionary<string, double>(
                _文件.当前参数, StringComparer.OrdinalIgnoreCase);
        }

        if (结果值.加载结果 is 加载结果分类.已加载)
        {
            _日志.LogInformation("已加载 {Count} 项全局参数", _文件.当前参数.Count);
        }
        else if (结果值.加载结果 is 加载结果分类.缺失)
        {
            _日志.LogWarning("未找到全局参数文件：{Path}，将使用配置默认值", 配置路径.参数集合);
        }
    }

    /// <summary>原子写回磁盘（必须串行：并发保存会互相覆盖）。</summary>
    public async Task 异步保存(CancellationToken 取消令牌 = default)
    {
        await _写入闸门.WaitAsync(取消令牌).ConfigureAwait(false);

        try
        {
            string JSON文本;
            lock (_同步)
            {
                JSON文本 = JsonSerializer.Serialize(_文件, JSON配置.写入人工风格);
            }

            await 原子文件.异步写入全部文本(配置路径.参数集合, JSON文本, 取消令牌).ConfigureAwait(false);
            _日志.LogDebug("全局参数已保存到 {Path}", 配置路径.参数集合);
        }
        catch (Exception 异常)
        {
            _日志.LogError(异常, "保存全局参数失败：{Path}", 配置路径.参数集合);
            throw;
        }
        finally
        {
            _写入闸门.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _写入闸门.Dispose();
}
