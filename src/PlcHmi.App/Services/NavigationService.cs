using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PlcHmi.App.ViewModels;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Models;

namespace PlcHmi.App.Services;

/// <summary>页面导航服务。</summary>
public interface INavigationService
{
    /// <summary>当前页面 ViewModel。</summary>
    ViewModelBase? Current { get; }

    /// <summary>页面切换事件。</summary>
    event EventHandler<ViewModelBase>? alreadyNavigation;

    /// <summary>导航到指定类型的页面。</summary>
    void Navigation<TViewModel>() where TViewModel : ViewModelBase;

    /// <summary>按类型导航（供非泛型场景使用）。</summary>
    void Navigation(Type viewModelType);
}

/// <summary>
/// 基于 DI 的页面导航实现。页面 ViewModel 由容器解析（单例），
/// 因此切换页面不会丢失已有状态。
/// </summary>
public sealed class NavigationService : ObservableObject, INavigationService
{
    private readonly IServiceProvider _service;
    private readonly ILogger<NavigationService> _log;

    private ViewModelBase? _current;

    public NavigationService(IServiceProvider serviceCollection, ILogger<NavigationService> Log)
    {
        _service = serviceCollection;
        _log = Log;
    }

    /// <inheritdoc />
    public ViewModelBase? Current => _current;

    /// <inheritdoc />
    public event EventHandler<ViewModelBase>? alreadyNavigation;

    /// <inheritdoc />
    public void Navigation<TViewModel>() where TViewModel : ViewModelBase =>
        Navigation(typeof(TViewModel));

    /// <inheritdoc />
    public void Navigation(Type viewModelType)
    {
        if (!typeof(ViewModelBase).IsAssignableFrom(viewModelType))
        {
            throw new ArgumentException($"{viewModelType.Name} 不是有效的页面 ViewModel。", nameof(viewModelType));
        }

        var viewModel = (ViewModelBase)_service.GetRequiredService(viewModelType);

        if (ReferenceEquals(_current, viewModel))
        {
            return;
        }

        _current = viewModel;
        _log.LogDebug("导航到页面 {Page}", viewModelType.Name);

        alreadyNavigation?.Invoke(this, viewModel);
    }
}

/// <summary>对话框服务。</summary>
public interface IDialogService
{
    /// <summary>显示确认对话框（普通级别，样式化确认框）。</summary>
    bool Confirm(string titleText, string Message);

    /// <summary>
    /// 显示分级确认对话框。
    /// <para>默认焦点在「取消」，回车 = 取消，执行必须显式点击（防惯性回车误触）。</para>
    /// </summary>
    /// <param name="title">标题（如「清除历史」）。</param>
    /// <param name="message">后果描述：写"会发生什么"，不复述按钮名。</param>
    /// <param name="level">危险分级（写 PLC = Warning，不可恢复 = Danger）。</param>
    /// <param name="confirmText">确认按钮文字。</param>
    /// <param name="detail">可选补充说明（影响范围 / 撤销方式）。</param>
    bool Confirm(string titleText, string Message, Views.ConfirmLevel Level,
        string confirmText = "确认执行", string? issueDetail = null);

    /// <summary>显示提示对话框。</summary>
    void Hint(string titleText, string Message);

    /// <summary>显示错误对话框。</summary>
    void ErrorInfo(string titleText, string Message);

    /// <summary>
    /// 编辑手动页元件。
    /// </summary>
    /// <param name="widget">待编辑的元件（已拷贝）。</param>
    /// <returns>是否点了确定。<b>确认时已就地修改传入对象。</b></returns>
    bool EditManualComponent(ManualComponentDefinition ComponentItem);

    /// <summary>弹出登录对话框。返回是否登录成功（取消则以当前身份继续）。</summary>
    bool ShowLogin();

    /// <summary>新增 / 编辑报警定义。确认时已就地修改传入对象。</summary>
    /// <param name="alarm">待编辑的定义（已拷贝）。</param>
    /// <param name="isNew">
    /// 新增传 <c>true</c>（编号可填写）；编辑传 <c>false</c>（编号是主键，锁定不可改，
    /// 否则改完编号 <c>Update</c> 就按新编号定位，找不到原条目）。
    /// </param>
    bool EditAlarmDefinition(AlarmDefinition AlarmItem, bool isNewBuild);

    /// <summary>编辑参数页的延时 / 按钮条目。确认时已就地修改传入对象。</summary>
    bool EditParameterEntry(ParameterTableCategory Category, ParameterEntryDefinition Entry);

    /// <summary>
    /// 编辑运行监控页元件地址（11 个元件一览编辑）。
    /// 确认时已就地修改传入列表中各元件的 <see cref="RunMonitorComponent.Address"/>。
    /// </summary>
    bool EditRunMonitorAddress(IReadOnlyList<RunMonitorComponent> ComponentList);

    /// <summary>
    /// 弹出变量表选择器。返回选中的变量条目；取消或变量表为空返回 <c>null</c>。
    /// </summary>
    VariableTableEntry? SelectVariable(string titleText);

    /// <summary>
    /// 编辑历史记录配置（触发位、数据体名称与数据项增删）。
    /// 确认时已就地修改传入对象（含数据项集合），返回是否点了确定。
    /// </summary>
    bool EditHistoryRecordConfig(HistoryRecordConfig configItem);

    /// <summary>
    /// 编辑点表各列的基址（仅管理员入口）。确认时已就地修改传入列表中各项的 <see cref="点表列地址项.基址文本"/>。
    /// </summary>
    /// <summary>
    /// 编辑页面上所有与 PLC 交互的元件地址（点表列 / 辅助地址 / 动作按钮 / 回零 / 点动）。
    /// 确认时已就地修改 <see cref="AddressEditRequest.ItemCollection"/> 与 <see cref="AddressEditRequest.DeletedItem"/>。
    /// </summary>
    bool EditPageAddress(AddressEditRequest Request);



    /// <summary>
    /// 新增 / 编辑其它点。确认时已就地修改传入对象。
    /// </summary>
    /// <param name="PointEntry">待编辑的点位（新增时已预填默认地址）。</param>
    /// <param name="isNewBuild">
    /// 新增传 <c>true</c>（标题为「新增其它点」）；编辑传 <c>false</c>。
    /// <b>两种模式下名称都可改</b>——编辑时改名由服务层按原名定位后整体替换，重名会被拒绝。
    /// </param>
    bool EditAuxPoint(AuxPointDefinition PointEntry, bool isNewBuild);
}

/// <summary>基于 WPF MessageBox 的对话框实现。</summary>
public sealed class DialogService : IDialogService
{
    private readonly IUserService _user;
    private readonly IVariableTableService? _variableTable;

    public DialogService(IUserService User, IVariableTableService? VariableTable = null)
    {
        _user = User;
        _variableTable = VariableTable;
    }

    /// <inheritdoc />
    public bool Confirm(string titleText, string Message) =>
        Confirm(titleText, Message, Views.ConfirmLevel.Neutral);

    /// <inheritdoc />
    public bool Confirm(string titleText, string Message, Views.ConfirmLevel Level,
        string confirmText = "确认执行", string? issueDetail = null) =>
        Views.ConfirmDialog.Show(titleText, Message, Level, confirmText, issueDetail);

    /// <inheritdoc />
    /// <remarks>
    /// 内部经 <see cref="UiDispatch.Post"/> 切到界面线程：大量命令是
    /// <c>await XxxAsync().ConfigureAwait(false)</c> 之后直接弹窗，续接运行在
    /// 线程池上——MessageBox 会脱离主窗显示（无人值守机上可能被主窗挡住，
    /// 错误提示不可见）。在服务层统一收口，调用点无需各自包裹。
    /// 已在界面线程时零开销直行。
    /// </remarks>
    public void Hint(string titleText, string Message) =>
        UiDispatch.Post(() => System.Windows.MessageBox.Show(Message, titleText,
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information));

    /// <inheritdoc />
    public void ErrorInfo(string titleText, string Message) =>
        UiDispatch.Post(() => System.Windows.MessageBox.Show(Message, titleText,
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Error));

    /// <inheritdoc />
    public bool EditManualComponent(ManualComponentDefinition ComponentItem) =>
        Views.ComponentEditorDialog.Show(ComponentItem, _variableTable?.EntryCollection);

    /// <inheritdoc />
    public bool ShowLogin() => Views.LoginDialog.Show(_user);

    /// <inheritdoc />
    public bool EditAlarmDefinition(AlarmDefinition AlarmItem, bool isNewBuild) =>
        Views.AlarmEditorDialog.Show(AlarmItem, isNewBuild);

    /// <inheritdoc />
    public bool EditParameterEntry(ParameterTableCategory Category, ParameterEntryDefinition Entry) =>
        Views.ParameterEntryEditorDialog.Show(Category, Entry, _variableTable?.EntryCollection);

    /// <inheritdoc />
    public bool EditRunMonitorAddress(IReadOnlyList<RunMonitorComponent> ComponentList) =>
        Views.RunMonitorAddressEditDialog.Show(ComponentList);

    /// <inheritdoc />
    public VariableTableEntry? SelectVariable(string titleText) =>
        _variableTable is null ? null : Views.VariableSelectDialog.Show(_variableTable.EntryCollection, titleText);

    /// <inheritdoc />
    public bool EditHistoryRecordConfig(HistoryRecordConfig configItem) =>
        Views.HistoryConfigEditorDialog.Show(configItem, _variableTable?.EntryCollection);

    /// <inheritdoc />
    public bool EditPageAddress(AddressEditRequest Request) =>
        Views.AddressEditDialog.Show(Request);

    /// <inheritdoc />
    public bool EditAuxPoint(AuxPointDefinition PointEntry, bool isNewBuild) =>
        Views.AuxPointEditDialog.Show(PointEntry, isNewBuild);
}
