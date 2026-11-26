using 上位机.核心.模型;

namespace 上位机.核心.抽象;

/// <summary>
/// 历史记录服务：按触发位采集数据体、按天保存、供界面查询 / 导出 / 清除。
///
/// <para><b>为什么查询与采集放在同一个接口：</b>界面上"这一页能看到的记录"
/// 与"采集正在写入的记录"必须是同一份内存真相——两处各自读文件，
/// 就会出现"刚记的那条要重进页面才看得见"的错觉。</para>
/// </summary>
public interface 历史记录服务接口
{
    /// <summary>当前配置（永不返回 <c>null</c>；宿主启动前返回内置默认值）。</summary>
    历史记录配置 配置 { get; }

    /// <summary>配置已变更（触发地址 / 数据项被管理员修改）。</summary>
    event EventHandler? 配置已变更;

    /// <summary>新增了一条记录（采集线程推送，界面据此刷新并切回 UI 线程）。</summary>
    event EventHandler<历史记录条目>? 记录已新增;

    /// <summary>读取某一天的记录（按时间升序；无记录返回空列表）。</summary>
    Task<IReadOnlyList<历史记录条目>> 异步读取日(DateTime 日期, CancellationToken 取消令牌 = default);

    /// <summary>
    /// 有记录的日期集合（升序）。供"前一天 / 后一天"翻页时跳过空白日——
    /// 停机几天后翻页不该让人连点十几下。
    /// </summary>
    IReadOnlyList<DateTime> 有记录的日期集合();

    /// <summary>校验一份配置（编辑器确定前调用，错误逐条给出）。</summary>
    历史配置校验 校验(历史记录配置 配置项);

    /// <summary>初始化：加载配置、装载当天记录、清理超期文件。</summary>
    Task 异步初始化(CancellationToken 取消令牌 = default);

    /// <summary>保存配置（仅管理员，服务层二次校验权限）。</summary>
    Task 异步保存配置(历史记录配置 配置项, CancellationToken 取消令牌 = default);

    /// <summary>清除全部历史记录（仅管理员，服务层二次校验权限）。</summary>
    Task 异步清除全部(CancellationToken 取消令牌 = default);

    /// <summary>把某一天的记录导出为 CSV 文件（UTF-8 带 BOM）。</summary>
    Task 异步导出CSV到文件(string 文件路径, DateTime 日期, CancellationToken 取消令牌 = default);
}
