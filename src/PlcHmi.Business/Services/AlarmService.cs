using Microsoft.Extensions.Logging;
using 上位机.业务.配置;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// 报警服务。管理活动报警与历史报警。
/// <para>报警来源：PLC 的报警字位（本阶段先提供 Raise/Clear 接口，
/// 待点位表确定后接入报警字解析）。</para>
/// </summary>
public sealed class 报警服务 : 报警服务接口
{
    /// <summary>历史报警保留上限。</summary>
    private const int 最大历史数量 = 1000;

    private readonly 标签写入器 _写入器;
    private readonly ILogger<报警服务> _日志;
    private readonly 报警历史存储? _历史存储;
    private readonly object _同步 = new();

    private readonly List<报警条目> _活动 = new();
    private readonly List<报警条目> _历史 = new();

    /// <param name="writer">PLC 写入器（用于复位命令）。</param>
    /// <param name="logger">日志。</param>
    /// <param name="historyStore">
    /// 历史落盘存储。<c>null</c> 表示只保留在内存（单元测试用）——
    /// 生产环境由 DI 注入，报警历史跨重启保留。
    /// </param>
    public 报警服务(
        标签写入器 写入器, ILogger<报警服务> 日志, 报警历史存储? 历史存储 = null)
    {
        _写入器 = 写入器;
        _日志 = 日志;
        _历史存储 = 历史存储;

        // 先把上一班的历史读回来：报警历史必须跨重启存活（硬件 HMI 面板本来就会存）
        if (_历史存储 is not null)
        {
            _历史.AddRange(_历史存储.加载().Take(最大历史数量));
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<报警条目> 活动报警
    {
        get
        {
            lock (_同步)
            {
                return _活动.ToList();
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<报警条目> 历史
    {
        get
        {
            lock (_同步)
            {
                return _历史.ToList();
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<报警条目>? 报警已触发;

    /// <inheritdoc />
    public event EventHandler<报警条目>? 报警已清除;

    /// <summary>触发一条报警。相同代码的活动报警不会重复触发。</summary>
    public void 触发(string 代码值, string 消息, 报警等级 等级)
    {
        报警条目 条目;

        lock (_同步)
        {
            var 现有 = _活动.FirstOrDefault(甲 => 甲.代码 == 代码值);
            if (现有 is not null)
            {
                return;
            }

            条目 = new 报警条目
            {
                代码 = 代码值,
                消息 = 消息,
                等级 = 等级,
                已触发 = DateTime.Now,
            };

            _活动.Add(条目);
            _历史.Insert(0, 条目.克隆());

            if (_历史.Count > 最大历史数量)
            {
                _历史.RemoveRange(最大历史数量, _历史.Count - 最大历史数量);
            }
        }

        _日志.LogInformation("报警触发 [{Level}] {Code} {Message}", 等级, 代码值, 消息);
        落盘历史();
        报警已触发?.Invoke(this, 条目);
    }

    /// <summary>消除一条报警。</summary>
    public void 清空(string 代码值)
    {
        报警条目? 条目;

        lock (_同步)
        {
            条目 = _活动.FirstOrDefault(甲 => 甲.代码 == 代码值);
            if (条目 is null)
            {
                return;
            }

            条目.已清除 = DateTime.Now;
            _活动.Remove(条目);

            var 历史数据项 = _历史.FirstOrDefault(历史条目 => 历史条目.代码 == 代码值 && 历史条目.已清除 is null);
            if (历史数据项 is not null)
            {
                历史数据项.已清除 = 条目.已清除;
            }
        }

        _日志.LogInformation("报警消除 {Code}", 代码值);
        落盘历史();
        报警已清除?.Invoke(this, 条目);
    }

    /// <inheritdoc />
    public void 确认(string 代码值)
    {
        lock (_同步)
        {
            var 条目 = _活动.FirstOrDefault(甲 => 甲.代码 == 代码值);
            if (条目 is null)
            {
                return;
            }

            条目.是否已确认 = true;

            var 历史数据项 = _历史.FirstOrDefault(历史条目 => 历史条目.代码 == 代码值 && 历史条目.已清除 is null);
            if (历史数据项 is not null)
            {
                历史数据项.是否已确认 = true;
            }
        }

        落盘历史();
    }

    /// <inheritdoc />
    public void 确认全部()
    {
        lock (_同步)
        {
            foreach (var 条目 in _活动)
            {
                条目.是否已确认 = true;

                // 历史里的同一条也必须同步。
                // 此前只改 _active，导致"全部确认"之后历史列表仍显示未确认 ——
                // 同一件事在同一个界面上有两种说法，操作员只会信错的那个。
                var 历史数据项 = _历史.FirstOrDefault(
                    历史条目 => 历史条目.代码 == 条目.代码 && 历史条目.已清除 is null);

                if (历史数据项 is not null)
                {
                    历史数据项.是否已确认 = true;
                }
            }
        }

        落盘历史();
    }

    /// <inheritdoc />
    public Task 异步重置(CancellationToken 取消令牌 = default) =>
        _写入器.异步脉冲("CmdReset", TimeSpan.FromMilliseconds(200), 取消令牌);

    /// <inheritdoc />
    public void 清空历史()
    {
        lock (_同步)
        {
            _历史.Clear();
        }

        落盘历史();
    }

    /// <summary>
    /// 把当前历史交给落盘存储（非阻塞，内部合并连续请求）。
    /// 取快照时已出锁，避免磁盘 I/O 与报警处理互相等待。
    /// </summary>
    private void 落盘历史()
    {
        if (_历史存储 is null)
        {
            return;
        }

        List<报警条目> 快照;

        lock (_同步)
        {
            快照 = _历史.ToList();
        }

        _历史存储.请求保存(快照);
    }
}
