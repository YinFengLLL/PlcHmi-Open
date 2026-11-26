using System.Text.Json;
using Microsoft.Extensions.Logging;
using PlcHmi.Business.Configuration;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// 机种数据文件（<c>config/recipes.json</c>）的读写封装。
///
/// <para><b>为什么需要单独一层：</b><see cref="RecipeService"/> 管参数、<see cref="示教服务"/> 管点表，
/// 两者写的是同一个文件。若各自维护一份内存副本，后保存的会把先前保存的覆盖掉——
/// 这是典型的"单机双写"陷阱。统一走这一个 Store，只保留一份内存真相。</para>
/// </summary>
public sealed class RecipeStore : IDisposable
{
    private readonly object _sync = new();

    /// <summary>
    /// 落盘串行化。写文件是异步的，不能用 <c>lock</c> 跨 <c>await</c> 持有，故单独用信号量。
    /// 没有它时，两次保存并发会各自序列化、再各自写盘，"后写"的可能拿着更旧的快照覆盖新的。
    ///
    /// <para><b>必须 <c>await WaitAsync()</c>，禁用 <c>Wait()</c></b>——同步占线程会与 UI 线程互锁。</para>
    /// </summary>
    private readonly SemaphoreSlim _writeGateValve = new(1, 1);

    private readonly ILogger<RecipeStore> _log;
    private readonly IConfigHealth? _health;

    private RecipeFile _file = new();

    /// <summary>构造函数即加载文件。</summary>
    /// <param name="logger">日志。</param>
    /// <param name="health">
    /// 配置健康登记处（可选）。加载失败或从备份恢复时登记，
    /// 让界面能显示"配方数据降级"的横幅 —— 而不是安静地以空配方运行。
    /// </param>
    public RecipeStore(ILogger<RecipeStore> Log, IConfigHealth? Health = null)
    {
        _log = Log;
        _health = Health;
        Load();
    }

    // 说明：此处原有一个 `public RecipeFile Data => _file;` 属性。
    // 它把内部整个可变集合（含 List<RecipeData>）直接交出去，而 lock 只保护"取引用"那一瞬间，
    // 拿到引用的调用方随后即可无锁增删——等于锁形同虚设。经核查该属性从无调用者，故移除。
    // 需要数据请走下面的 Find / GetOrCreate。

    /// <summary>当前机种号。</summary>
    public int CurrentRecipeNumber
    {
        get { lock (_sync) { return _file.CurrentRecipeNumber; } }
    }

    /// <summary>
    /// 按机种号查找数据，不存在返回 <c>null</c>。
    ///
    /// <para><b>返回的是 Store 内部持有的可变对象，不是副本。</b>
    /// 调用方修改它即等于直接修改内存真相——这正是本 Store 存在的意义：
    /// 保证 <see cref="RecipeService"/> 与 <see cref="示教服务"/> 共用同一份数据，
    /// 避免"各持副本、后保存者覆盖先保存者"。因此调用方须保证串行访问，
    /// 不要把它交给后台线程并发修改。</para>
    /// </summary>
    public RecipeData? Find(int RecipeNumber)
    {
        lock (_sync)
        {
            return _file.RecipeCollection.FirstOrDefault(coordinateR => coordinateR.Number == RecipeNumber);
        }
    }

    /// <summary>
    /// 按机种号取数据，不存在则创建并登记。
    /// <para>与 <see cref="Find"/> 相同：返回的是 Store 内部持有的可变对象，不是副本。</para>
    /// </summary>
    public RecipeData GetOrCreate(int RecipeNumber)
    {
        lock (_sync)
        {
            var currentHas = _file.RecipeCollection.FirstOrDefault(coordinateR => coordinateR.Number == RecipeNumber);
            if (currentHas is not null)
            {
                return currentHas;
            }

            var created = new RecipeData { Number = RecipeNumber };
            _file.RecipeCollection.Add(created);
            _file.RecipeCollection.Sort((item, item2) => item.Number.CompareTo(item2.Number));

            _log.LogDebug("已创建机种 {No} 的数据条目", RecipeNumber);
            return created;
        }
    }

    /// <summary>设置当前机种号（不落盘）。</summary>
    public void SetCurrent(int RecipeNumber)
    {
        lock (_sync)
        {
            _file.CurrentRecipeNumber = RecipeNumber;
        }
    }

    /// <summary>
    /// 从磁盘加载。主文件损坏时回退 <c>.bak</c>（见 <see cref="ConfigFileLoader"/>）；
    /// 两者都不可用时保持空数据以保证程序仍能启动，但会登记为降级状态。
    /// </summary>
    public void Load()
    {
        var resultValue = ConfigFileLoader.LoadJson<RecipeFile>(
            ConfigPath.RecipeCollection, JsonConfig.Read, _log);

        _health.Report("机种数据（配方）", resultValue);

        lock (_sync)
        {
            _file = resultValue.Value ?? new RecipeFile();
        }

        if (resultValue.loadResult is LoadResultSplitCategory.Loaded)
        {
            _log.LogInformation("已加载 {Count} 个机种，当前机种 {No}",
                _file.RecipeCollection.Count, _file.CurrentRecipeNumber);
        }
        else if (resultValue.loadResult is LoadResultSplitCategory.Missing)
        {
            _log.LogWarning("未找到机种数据文件：{Path}，将以空数据启动", ConfigPath.RecipeCollection);
        }
    }

    /// <summary>
    /// 写回磁盘。
    ///
    /// <para><b>必须走 <see cref="AtomicFile"/>：</b>这里是全部机种参数与点表的主存。
    /// 若直接 <c>File.WriteAllTextAsync</c>，写入途中断电或磁盘写满会让文件停在
    /// "已截断、未写完"的半截状态，下次启动反序列化失败 → 全部机种数据丢失。</para>
    ///
    /// <para><b>必须串行：</b>经由 <see cref="_writeGateValve"/> 排队。两个服务同时保存时，
    /// 若各自序列化再各自写盘，"后写"的可能是更旧的快照，等于把先写的那份改动抹掉。</para>
    /// </summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _writeGateValve.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            string jsonText;
            lock (_sync)
            {
                jsonText = JsonSerializer.Serialize(_file, JsonConfig.Write);
            }

            await AtomicFile.WriteAllTextAsync(ConfigPath.RecipeCollection, jsonText, cancellationToken).ConfigureAwait(false);
            _log.LogDebug("机种数据已保存到 {Path}", ConfigPath.RecipeCollection);
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "保存机种数据失败：{Path}", ConfigPath.RecipeCollection);
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
