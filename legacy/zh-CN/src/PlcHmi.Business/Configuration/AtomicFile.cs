using System.Text;

namespace 上位机.业务.配置;

/// <summary>
/// 配置文件的安全落盘。
///
/// <para><b>为什么不能直接用 <c>File.WriteAllText</c>：</b>
/// 它是"先截断、再写入"。若在写入过程中断电、进程被杀或磁盘写满，
/// 目标文件会停在"已截断、尚未写完"的中间态，下次启动反序列化失败。
/// 对 <c>recipes.json</c> 而言，这意味着<b>全部机种的参数与点表一次性丢失</b> ——
/// 现场表现是"开机后配方全没了"，而且不可恢复。</para>
///
/// <para><b>做法：</b>先写同目录下的 <c>.tmp</c>（并 <c>FlushFileBuffers</c> 落到物理介质），
/// 再原子替换目标文件；替换时把旧文件留成 <c>.bak</c> 作为回滚副本。
/// 同卷内的替换是原子操作，因此任何时刻磁盘上要么是完整的旧文件，
/// 要么是完整的新文件，绝不会是半个。</para>
///
/// <para><b>配套的一半在 <see cref="配置文件加载器"/>：</b>只留 <c>.bak</c> 而不去读它，
/// 备份就是死的。加载失败时必须真的回退到 <c>.bak</c>，否则断电一次仍然是全丢。</para>
///
/// <para>用 <c>File.Replace</c> 而非"先删再改名"，是因为前者在操作系统层面是一次替换，
/// 后者中间存在"目标文件已删、新文件未就位"的窗口，仍可能丢文件。</para>
/// </summary>
public static class 原子文件
{
    /// <summary>临时文件名的进程内序号，配合 PID 保证同路径并发写不互踩。</summary>
    private static int _临时序号;

    /// <summary>
    /// 原子写入文本文件。成功返回时磁盘上已是完整内容；
    /// 失败时抛出异常，且目标文件保持原样（不被破坏）。
    /// </summary>
    /// <param name="path">目标文件绝对路径。</param>
    /// <param name="content">要写入的完整文本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task 异步写入全部文本(
        string 文件路径,
        string 内容,
        CancellationToken 取消令牌 = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(文件路径);

        var 目录 = Path.GetDirectoryName(文件路径);
        if (!string.IsNullOrEmpty(目录))
        {
            Directory.CreateDirectory(目录);
        }

        // 临时文件名带进程内唯一后缀：两个服务并发写同一路径（如 页面地址服务 与
        // 轴地址服务 都可能写 recipe-config.json）时，固定的 ".tmp" 会互相覆盖，
        // 把一次完好的"原子写"变成损坏替换
        var 临时 = $"{文件路径}.tmp.{Environment.ProcessId}.{Interlocked.Increment(ref _临时序号):X}";
        var 备份 = 文件路径 + ".bak";

        try
        {
            // 1. 内容先完整落到临时文件，并**刷到物理介质**
            //
            //    只 Dispose 不等于落盘：操作系统此时仍把数据放在写缓存里。
            //    断电时可能出现"目录项（改名）已落盘、数据块没有"的次序 —— 目标文件
            //    于是变成 0 字节或半截。File.Replace 保证的是**原子性**（不会半新半旧），
            //    保证不了**持久性**（内容真的在盘上）。FlushFileBuffers 补上这一环。
            await 异步写入穿过(临时, 内容, 取消令牌).ConfigureAwait(false);

            // 2. 原子替换；已有文件顺带留一份 .bak 供回滚
            //
            //    仅当现有文件**非空**时才轮换成 .bak。被截断的文件通常是 0 字节，
            //    若照样轮换，就会把唯一的好副本覆盖成损坏内容 —— 那等于亲手毁掉退路。
            //
            //    替换动作带短重试（见 <see cref="带短重试"/>）：杀毒软件的实时扫描
            //    会短暂锁住刚关闭的文件，Replace/Move 在此窗口内会报 IOException。
            //    Replace 本身没有异步版，连同重试一起丢进线程池，不占调用方线程。
            await Task.Run(() =>
            {
                if (File.Exists(文件路径) && new FileInfo(文件路径).Length > 0)
                {
                    带短重试(() => File.Replace(临时, 文件路径, 备份, ignoreMetadataErrors: true));
                }
                else
                {
                    带短重试(() => File.Move(临时, 文件路径, overwrite: true));
                }
            }, 取消令牌).ConfigureAwait(false);
        }
        finally
        {
            // 中途失败时可能残留写了一半的 .tmp，清掉避免目录里堆积脏文件。
            // 成功路径上 tmp 已被 Replace/Move 消费掉，这里是空操作。
            尝试删除(临时);
        }
    }

    private static async Task 异步写入穿过(
        string 文件路径, string 内容, CancellationToken 取消令牌)
    {
        // UTF-8 不带 BOM：与 File.WriteAllText 的默认口径一致，避免给现场手工编辑的 JSON 加 BOM
        var 字节 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(内容);

        await using var 流 = new FileStream(
            文件路径, FileMode.Create, FileAccess.Write, FileShare.None);

        await 流.WriteAsync(字节, 取消令牌).ConfigureAwait(false);
        await 流.FlushAsync(取消令牌).ConfigureAwait(false);

        // FlushFileBuffers：把内容从 OS 写缓存推给物理介质。
        // 这一步是"断电后文件仍然完整"的关键，缺了它原子替换只防一半。
        //
        // 它**没有异步版本**（FlushAsync 只刷到 OS 缓存，不落盘），所以丢到线程池执行：
        // 既不占 UI 线程，也不触发 CA1849（异步方法内同步阻塞）。
        await Task.Run(() => 流.Flush(flushToDisk: true), 取消令牌).ConfigureAwait(false);
    }

    /// <summary>
    /// 主文件不可用时，用同目录的 <c>.bak</c> 副本把主文件修复回来。
    ///
    /// <para><b>为什么不用 <see cref="异步写入全部文本"/> 来恢复：</b>它会把"当前的坏文件"
    /// 轮换成 <c>.bak</c> —— 那正好把要救的那份好副本覆盖掉。所以这里直接 Copy。</para>
    /// </summary>
    /// <returns>恢复成功返回 <c>true</c>；没有备份或复制失败返回 <c>false</c>。</returns>
    public static bool 尝试恢复备份(string 文件路径)
    {
        var 备份 = 文件路径 + ".bak";

        try
        {
            if (!File.Exists(备份) || new FileInfo(备份).Length == 0)
            {
                return false;
            }

            File.Copy(备份, 文件路径, overwrite: true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 替换类文件操作带短重试。
    ///
    /// <para><b>为什么必须重试：</b>现场工控机普遍装有杀毒软件，文件句柄刚关闭就会被
    /// 实时扫描短暂打开锁定，这个窗口内 <see cref="File.Replace"/> / <see cref="File.Move"/>
    /// 会抛 IOException（"另一个程序正在使用此文件"）。间隔数十毫秒重试即可越过；
    /// 若不重试，一次正常保存就会失败 —— 对 recipes.json 是不可接受的。</para>
    /// </summary>
    private static void 带短重试(Action 操作)
    {
        const int 次数上限 = 3;
        var 间隔序列 = new[] { 50, 100, 200 };

        for (var 第几次 = 1; ; 第几次++)
        {
            try
            {
                操作();
                return;
            }
            catch (IOException) when (第几次 <= 次数上限)
            {
                Thread.Sleep(间隔序列[第几次 - 1]);
            }
            catch (UnauthorizedAccessException) when (第几次 <= 次数上限)
            {
                Thread.Sleep(间隔序列[第几次 - 1]);
            }
        }
    }

    private static void 尝试删除(string 文件路径)
    {
        try
        {
            if (File.Exists(文件路径))
            {
                File.Delete(文件路径);
            }
        }
        catch (IOException)
        {
            // 清理失败不影响主流程；下次写入会覆盖同名 .tmp
        }
        catch (UnauthorizedAccessException)
        {
            // 同上
        }
    }

    /// <summary>
    /// 判断一个文件是否是 <see cref="异步写入全部文本"/> 产生的中间产物
    /// （<c>.tmp</c> / <c>.bak</c>，含带唯一后缀的 <c>.tmp.&lt;pid&gt;.&lt;n&gt;</c>）。
    /// 供目录扫描时过滤用。
    /// </summary>
    public static bool 是否中间文件(string 文件路径)
    {
        return 文件路径.Contains(".tmp.", StringComparison.OrdinalIgnoreCase)
            || 文件路径.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || 文件路径.EndsWith(".bak", StringComparison.OrdinalIgnoreCase);
    }
}
