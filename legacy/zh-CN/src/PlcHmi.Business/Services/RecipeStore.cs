using System.Text.Json;
using Microsoft.Extensions.Logging;
using 上位机.业务.配置;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// 机种数据文件（<c>config/recipes.json</c>）的读写封装。
///
/// <para><b>为什么需要单独一层：</b><see cref="配方服务"/> 管参数、<see cref="示教服务"/> 管点表，
/// 两者写的是同一个文件。若各自维护一份内存副本，后保存的会把先前保存的覆盖掉——
/// 这是典型的"单机双写"陷阱。统一走这一个 Store，只保留一份内存真相。</para>
/// </summary>
public sealed class 配方存储 : IDisposable
{
    private readonly object _同步 = new();

    /// <summary>
    /// 落盘串行化。写文件是异步的，不能用 <c>lock</c> 跨 <c>await</c> 持有，故单独用信号量。
    /// 没有它时，两次保存并发会各自序列化、再各自写盘，"后写"的可能拿着更旧的快照覆盖新的。
    ///
    /// <para><b>必须 <c>await WaitAsync()</c>，禁用 <c>Wait()</c></b>——同步占线程会与 UI 线程互锁。</para>
    /// </summary>
    private readonly SemaphoreSlim _写入闸门 = new(1, 1);

    private readonly ILogger<配方存储> _日志;
    private readonly 配置健康接口? _健康;

    private 配方文件 _文件 = new();

    /// <summary>构造函数即加载文件。</summary>
    /// <param name="logger">日志。</param>
    /// <param name="health">
    /// 配置健康登记处（可选）。加载失败或从备份恢复时登记，
    /// 让界面能显示"配方数据降级"的横幅 —— 而不是安静地以空配方运行。
    /// </param>
    public 配方存储(ILogger<配方存储> 日志, 配置健康接口? 健康 = null)
    {
        _日志 = 日志;
        _健康 = 健康;
        加载();
    }

    // 说明：此处原有一个 `public RecipeFile Data => _file;` 属性。
    // 它把内部整个可变集合（含 List<RecipeData>）直接交出去，而 lock 只保护"取引用"那一瞬间，
    // 拿到引用的调用方随后即可无锁增删——等于锁形同虚设。经核查该属性从无调用者，故移除。
    // 需要数据请走下面的 Find / GetOrCreate。

    /// <summary>当前机种号。</summary>
    public int 当前配方号
    {
        get { lock (_同步) { return _文件.当前配方号; } }
    }

    /// <summary>
    /// 按机种号查找数据，不存在返回 <c>null</c>。
    ///
    /// <para><b>返回的是 Store 内部持有的可变对象，不是副本。</b>
    /// 调用方修改它即等于直接修改内存真相——这正是本 Store 存在的意义：
    /// 保证 <see cref="配方服务"/> 与 <see cref="示教服务"/> 共用同一份数据，
    /// 避免"各持副本、后保存者覆盖先保存者"。因此调用方须保证串行访问，
    /// 不要把它交给后台线程并发修改。</para>
    /// </summary>
    public 配方数据? 查找(int 配方号)
    {
        lock (_同步)
        {
            return _文件.配方集合.FirstOrDefault(坐标R => 坐标R.号 == 配方号);
        }
    }

    /// <summary>
    /// 按机种号取数据，不存在则创建并登记。
    /// <para>与 <see cref="查找"/> 相同：返回的是 Store 内部持有的可变对象，不是副本。</para>
    /// </summary>
    public 配方数据 获取或创建(int 配方号)
    {
        lock (_同步)
        {
            var 现有 = _文件.配方集合.FirstOrDefault(坐标R => 坐标R.号 == 配方号);
            if (现有 is not null)
            {
                return 现有;
            }

            var 已创建 = new 配方数据 { 号 = 配方号 };
            _文件.配方集合.Add(已创建);
            _文件.配方集合.Sort((甲, 乙) => 甲.号.CompareTo(乙.号));

            _日志.LogDebug("已创建机种 {No} 的数据条目", 配方号);
            return 已创建;
        }
    }

    /// <summary>设置当前机种号（不落盘）。</summary>
    public void 设置当前(int 配方号)
    {
        lock (_同步)
        {
            _文件.当前配方号 = 配方号;
        }
    }

    /// <summary>
    /// 从磁盘加载。主文件损坏时回退 <c>.bak</c>（见 <see cref="配置文件加载器"/>）；
    /// 两者都不可用时保持空数据以保证程序仍能启动，但会登记为降级状态。
    /// </summary>
    public void 加载()
    {
        var 结果值 = 配置文件加载器.加载JSON<配方文件>(
            配置路径.配方集合, JSON配置.读取, _日志);

        _健康.上报("机种数据（配方）", 结果值);

        lock (_同步)
        {
            _文件 = 结果值.值 ?? new 配方文件();
        }

        if (结果值.加载结果 is 加载结果分类.已加载)
        {
            _日志.LogInformation("已加载 {Count} 个机种，当前机种 {No}",
                _文件.配方集合.Count, _文件.当前配方号);
        }
        else if (结果值.加载结果 is 加载结果分类.缺失)
        {
            _日志.LogWarning("未找到机种数据文件：{Path}，将以空数据启动", 配置路径.配方集合);
        }
    }

    /// <summary>
    /// 写回磁盘。
    ///
    /// <para><b>必须走 <see cref="原子文件"/>：</b>这里是全部机种参数与点表的主存。
    /// 若直接 <c>File.WriteAllTextAsync</c>，写入途中断电或磁盘写满会让文件停在
    /// "已截断、未写完"的半截状态，下次启动反序列化失败 → 全部机种数据丢失。</para>
    ///
    /// <para><b>必须串行：</b>经由 <see cref="_写入闸门"/> 排队。两个服务同时保存时，
    /// 若各自序列化再各自写盘，"后写"的可能是更旧的快照，等于把先写的那份改动抹掉。</para>
    /// </summary>
    public async Task 异步保存(CancellationToken 取消令牌 = default)
    {
        await _写入闸门.WaitAsync(取消令牌).ConfigureAwait(false);

        try
        {
            string JSON文本;
            lock (_同步)
            {
                JSON文本 = JsonSerializer.Serialize(_文件, JSON配置.写入);
            }

            await 原子文件.异步写入全部文本(配置路径.配方集合, JSON文本, 取消令牌).ConfigureAwait(false);
            _日志.LogDebug("机种数据已保存到 {Path}", 配置路径.配方集合);
        }
        catch (Exception 异常)
        {
            _日志.LogError(异常, "保存机种数据失败：{Path}", 配置路径.配方集合);
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
