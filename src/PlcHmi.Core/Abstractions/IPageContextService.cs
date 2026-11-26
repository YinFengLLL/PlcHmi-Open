namespace 上位机.核心.抽象;

/// <summary>
/// 页面上下文服务：把"当前页面号 / 当前示教点码"同步到 PLC
/// （见 <see cref="核心.模型.页面上下文寄存器"/>）。
///
/// <para><b>写入语义是 best-effort：</b>这几个字只是给 PLC 的上下文提示，
/// 写不进去绝不能把页面切换或点选卡住。未连接时先记在本机，连接建立后自动补写一次，
/// 保证 PLC 侧最终拿到的是正确值（而不是开机那一段的空白）。</para>
/// </summary>
public interface 页面上下文服务接口
{
    /// <summary>把当前页面号写入 D10（主监控 = 1，按侧栏固定顺序递增）。</summary>
    Task 异步写入页码(int 页码, CancellationToken 取消令牌 = default);

    /// <summary>
    /// 把当前点号（<b>0 基</b>，第一个点 = 0）写入指定示教页的点码寄存器
    /// （锁附示教 <c>D651</c> / 点胶示教 <c>D652</c>）。
    /// <para>寄存器由调用方按页传（<see cref="核心.模型.页面上下文寄存器"/>），
    /// 服务本身不认识"哪一页"——两个示教页的点表互相独立，各写各的字。</para>
    /// </summary>
    Task 异步写入点号(int 点号寄存器, int 点号, CancellationToken 取消令牌 = default);
}
