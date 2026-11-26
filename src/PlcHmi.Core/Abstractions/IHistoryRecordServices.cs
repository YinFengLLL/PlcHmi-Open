using PlcHmi.Core.Models;

namespace PlcHmi.Core.Abstractions;

/// <summary>
/// 历史记录服务：按触发位采集数据体、按天保存、供界面查询 / 导出 / 清除。
///
/// <para><b>为什么查询与采集放在同一个接口：</b>界面上"这一页能看到的记录"
/// 与"采集正在写入的记录"必须是同一份内存真相——两处各自读文件，
/// 就会出现"刚记的那条要重进页面才看得见"的错觉。</para>
/// </summary>
public interface IHistoryRecordService
{
    /// <summary>当前配置（永不返回 <c>null</c>；宿主启动前返回内置默认值）。</summary>
    HistoryRecordConfig Config { get; }

    /// <summary>配置已变更（触发地址 / 数据项被管理员修改）。</summary>
    event EventHandler? configChanged;

    /// <summary>新增了一条记录（采集线程推送，界面据此刷新并切回 UI 线程）。</summary>
    event EventHandler<HistoryRecordEntry>? recordAlreadyAdd;

    /// <summary>读取某一天的记录（按时间升序；无记录返回空列表）。</summary>
    Task<IReadOnlyList<HistoryRecordEntry>> ReadDayAsync(DateTime Date, CancellationToken cancellationToken = default);

    /// <summary>
    /// 有记录的日期集合（升序）。供"前一天 / 后一天"翻页时跳过空白日——
    /// 停机几天后翻页不该让人连点十几下。
    /// </summary>
    IReadOnlyList<DateTime> HasRecordDateCollection();

    /// <summary>校验一份配置（编辑器确定前调用，错误逐条给出）。</summary>
    HistoryConfigValidate Validate(HistoryRecordConfig configItem);

    /// <summary>初始化：加载配置、装载当天记录、清理超期文件。</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>保存配置（仅管理员，服务层二次校验权限）。</summary>
    Task SaveConfigAsync(HistoryRecordConfig configItem, CancellationToken cancellationToken = default);

    /// <summary>清除全部历史记录（仅管理员，服务层二次校验权限）。</summary>
    Task ClearRemoveAllAsync(CancellationToken cancellationToken = default);

    /// <summary>把某一天的记录导出为 CSV 文件（UTF-8 带 BOM）。</summary>
    Task ExportCsvToFileAsync(string filePath, DateTime Date, CancellationToken cancellationToken = default);
}
