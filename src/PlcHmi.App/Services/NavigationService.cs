using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using 上位机.应用.视图模型;
using 上位机.核心.抽象;
using 上位机.核心.模型;

namespace 上位机.应用.服务;

/// <summary>页面导航服务。</summary>
public interface 导航服务接口
{
    /// <summary>当前页面 ViewModel。</summary>
    视图模型基类? 当前 { get; }

    /// <summary>页面切换事件。</summary>
    event EventHandler<视图模型基类>? 已导航;

    /// <summary>导航到指定类型的页面。</summary>
    void 导航<TViewModel>() where TViewModel : 视图模型基类;

    /// <summary>按类型导航（供非泛型场景使用）。</summary>
    void 导航(Type 视图模型类型);
}

/// <summary>
/// 基于 DI 的页面导航实现。页面 ViewModel 由容器解析（单例），
/// 因此切换页面不会丢失已有状态。
/// </summary>
public sealed class 导航服务 : ObservableObject, 导航服务接口
{
    private readonly IServiceProvider _服务;
    private readonly ILogger<导航服务> _日志;

    private 视图模型基类? _当前;

    public 导航服务(IServiceProvider 服务集合, ILogger<导航服务> 日志)
    {
        _服务 = 服务集合;
        _日志 = 日志;
    }

    /// <inheritdoc />
    public 视图模型基类? 当前 => _当前;

    /// <inheritdoc />
    public event EventHandler<视图模型基类>? 已导航;

    /// <inheritdoc />
    public void 导航<TViewModel>() where TViewModel : 视图模型基类 =>
        导航(typeof(TViewModel));

    /// <inheritdoc />
    public void 导航(Type 视图模型类型)
    {
        if (!typeof(视图模型基类).IsAssignableFrom(视图模型类型))
        {
            throw new ArgumentException($"{视图模型类型.Name} 不是有效的页面 ViewModel。", nameof(视图模型类型));
        }

        var 视图模型 = (视图模型基类)_服务.GetRequiredService(视图模型类型);

        if (ReferenceEquals(_当前, 视图模型))
        {
            return;
        }

        _当前 = 视图模型;
        _日志.LogDebug("导航到页面 {Page}", 视图模型类型.Name);

        已导航?.Invoke(this, 视图模型);
    }
}

/// <summary>对话框服务。</summary>
public interface 对话框服务接口
{
    /// <summary>显示确认对话框（普通级别，样式化确认框）。</summary>
    bool 确认(string 标题文本, string 消息);

    /// <summary>
    /// 显示分级确认对话框。
    /// <para>默认焦点在「取消」，回车 = 取消，执行必须显式点击（防惯性回车误触）。</para>
    /// </summary>
    /// <param name="title">标题（如「清除历史」）。</param>
    /// <param name="message">后果描述：写"会发生什么"，不复述按钮名。</param>
    /// <param name="level">危险分级（写 PLC = Warning，不可恢复 = Danger）。</param>
    /// <param name="confirmText">确认按钮文字。</param>
    /// <param name="detail">可选补充说明（影响范围 / 撤销方式）。</param>
    bool 确认(string 标题文本, string 消息, 视图.确认等级 等级,
        string 确认文本 = "确认执行", string? 问题详情 = null);

    /// <summary>显示提示对话框。</summary>
    void 提示(string 标题文本, string 消息);

    /// <summary>显示错误对话框。</summary>
    void 错误信息(string 标题文本, string 消息);

    /// <summary>
    /// 编辑手动页元件。
    /// </summary>
    /// <param name="widget">待编辑的元件（已拷贝）。</param>
    /// <returns>是否点了确定。<b>确认时已就地修改传入对象。</b></returns>
    bool 编辑手动元件(手动元件定义 元件项);

    /// <summary>弹出登录对话框。返回是否登录成功（取消则以当前身份继续）。</summary>
    bool 显示登录();

    /// <summary>新增 / 编辑报警定义。确认时已就地修改传入对象。</summary>
    /// <param name="alarm">待编辑的定义（已拷贝）。</param>
    /// <param name="isNew">
    /// 新增传 <c>true</c>（编号可填写）；编辑传 <c>false</c>（编号是主键，锁定不可改，
    /// 否则改完编号 <c>Update</c> 就按新编号定位，找不到原条目）。
    /// </param>
    bool 编辑报警定义(报警定义 报警项, bool 是否新建);

    /// <summary>编辑参数页的延时 / 按钮条目。确认时已就地修改传入对象。</summary>
    bool 编辑参数条目(参数表格类别 类别, 参数条目定义 条目);

    /// <summary>
    /// 编辑运行监控页元件地址（11 个元件一览编辑）。
    /// 确认时已就地修改传入列表中各元件的 <see cref="运行监控元件.地址"/>。
    /// </summary>
    bool 编辑运行监控地址(IReadOnlyList<运行监控元件> 元件列表);

    /// <summary>
    /// 弹出变量表选择器。返回选中的变量条目；取消或变量表为空返回 <c>null</c>。
    /// </summary>
    变量表条目? 选择变量(string 标题文本);

    /// <summary>
    /// 编辑历史记录配置（触发位、数据体名称与数据项增删）。
    /// 确认时已就地修改传入对象（含数据项集合），返回是否点了确定。
    /// </summary>
    bool 编辑历史记录配置(历史记录配置 配置项);

    /// <summary>
    /// 编辑点表各列的基址（仅管理员入口）。确认时已就地修改传入列表中各项的 <see cref="点表列地址项.基址文本"/>。
    /// </summary>
    /// <summary>
    /// 编辑页面上所有与 PLC 交互的元件地址（点表列 / 辅助地址 / 动作按钮 / 回零 / 点动）。
    /// 确认时已就地修改 <see cref="地址编辑请求.项集合"/> 与 <see cref="地址编辑请求.已删除项"/>。
    /// </summary>
    bool 编辑页面地址(地址编辑请求 请求);



    /// <summary>
    /// 新增 / 编辑其它点。确认时已就地修改传入对象。
    /// </summary>
    /// <param name="点项">待编辑的点位（新增时已预填默认地址）。</param>
    /// <param name="是否新建">
    /// 新增传 <c>true</c>（标题为「新增其它点」）；编辑传 <c>false</c>。
    /// <b>两种模式下名称都可改</b>——编辑时改名由服务层按原名定位后整体替换，重名会被拒绝。
    /// </param>
    bool 编辑辅助点(辅助点定义 点项, bool 是否新建);
}

/// <summary>基于 WPF MessageBox 的对话框实现。</summary>
public sealed class 对话框服务 : 对话框服务接口
{
    private readonly 用户服务接口 _用户;
    private readonly 变量表服务接口? _变量表;

    public 对话框服务(用户服务接口 用户, 变量表服务接口? 变量表 = null)
    {
        _用户 = 用户;
        _变量表 = 变量表;
    }

    /// <inheritdoc />
    public bool 确认(string 标题文本, string 消息) =>
        确认(标题文本, 消息, 视图.确认等级.中性);

    /// <inheritdoc />
    public bool 确认(string 标题文本, string 消息, 视图.确认等级 等级,
        string 确认文本 = "确认执行", string? 问题详情 = null) =>
        视图.确认对话框.显示(标题文本, 消息, 等级, 确认文本, 问题详情);

    /// <inheritdoc />
    /// <remarks>
    /// 内部经 <see cref="界面派发.投递"/> 切到界面线程：大量命令是
    /// <c>await XxxAsync().ConfigureAwait(false)</c> 之后直接弹窗，续接运行在
    /// 线程池上——MessageBox 会脱离主窗显示（无人值守机上可能被主窗挡住，
    /// 错误提示不可见）。在服务层统一收口，调用点无需各自包裹。
    /// 已在界面线程时零开销直行。
    /// </remarks>
    public void 提示(string 标题文本, string 消息) =>
        界面派发.投递(() => System.Windows.MessageBox.Show(消息, 标题文本,
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information));

    /// <inheritdoc />
    public void 错误信息(string 标题文本, string 消息) =>
        界面派发.投递(() => System.Windows.MessageBox.Show(消息, 标题文本,
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Error));

    /// <inheritdoc />
    public bool 编辑手动元件(手动元件定义 元件项) =>
        视图.元件编辑器对话框.显示(元件项, _变量表?.条目集合);

    /// <inheritdoc />
    public bool 显示登录() => 视图.登录对话框.显示(_用户);

    /// <inheritdoc />
    public bool 编辑报警定义(报警定义 报警项, bool 是否新建) =>
        视图.报警编辑器对话框.显示(报警项, 是否新建);

    /// <inheritdoc />
    public bool 编辑参数条目(参数表格类别 类别, 参数条目定义 条目) =>
        视图.参数条目编辑器对话框.显示(类别, 条目, _变量表?.条目集合);

    /// <inheritdoc />
    public bool 编辑运行监控地址(IReadOnlyList<运行监控元件> 元件列表) =>
        视图.运行监控地址编辑对话框.显示(元件列表);

    /// <inheritdoc />
    public 变量表条目? 选择变量(string 标题文本) =>
        _变量表 is null ? null : 视图.变量选择对话框.显示(_变量表.条目集合, 标题文本);

    /// <inheritdoc />
    public bool 编辑历史记录配置(历史记录配置 配置项) =>
        视图.历史配置编辑器对话框.显示(配置项, _变量表?.条目集合);

    /// <inheritdoc />
    public bool 编辑页面地址(地址编辑请求 请求) =>
        视图.地址编辑对话框.显示(请求);

    /// <inheritdoc />
    public bool 编辑辅助点(辅助点定义 点项, bool 是否新建) =>
        视图.辅助点编辑对话框.显示(点项, 是否新建);
}
