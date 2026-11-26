using CommunityToolkit.Mvvm.ComponentModel;
using PlcHmi.App.Services;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Models;

namespace PlcHmi.App.ViewModels;

/// <summary>
/// 页面上一个辅助地址行（具名数值元件或开关元件）。
///
/// <para><b>结构体数组（2026-10-21）：</b>点胶页的单点工艺参数在 PLC 里是"每点一份"的结构体数组
/// （见 <see cref="AuxAddressItem.ByPointAddress"/>）。行本身记住<b>当前点序号</b>，
/// 地址与读写都按它实时换算——切换点时只改序号、不重建行，
/// 这样界面控件不会闪、也不会丢焦点。</para>
///
/// <para><b>实时读写（2026-11-12 现场要求）：</b>辅助地址的值改为
/// ① 页面停留期间周期从 PLC 读回（见 <see cref="PointTableTeachViewModel.RefreshActionButtonStateAsync"/>），
/// ② 填完一个数（回车或离开输入框）立刻写 PLC——不再有「写入」按钮。
/// 写入前必须已打开手动使能（<see cref="Editable"/> 由页根据使能状态下发），
/// 避免"随手改一个数就把工艺参数改了"。</para>
/// </summary>
public sealed partial class AuxAddressRowViewModel : ObservableObject, InputSubmitRow
{
    private readonly IPageAddressService _service;

    /// <summary>程序正在按 PLC 值刷新（此时赋值不得反向触发写入）。</summary>
    private bool _refresh;

    [ObservableProperty] private string _text = "0.00";
    [ObservableProperty] private bool _switchState;

    /// <summary>当前点序号（0 基）。结构体数组模式下行寻址用；固定地址模式下无影响。</summary>
    [ObservableProperty] private int _pointSequence;

    /// <summary>是否允许修改（= 已打开手动使能）。关闭时输入框与开关置灰。</summary>
    [ObservableProperty] private bool _editable;

    /// <summary>
    /// 操作员正在这个输入框里打字（周期回读要跳过它，否则读到一半被 PLC 值覆盖）。
    /// 由 <see cref="InputSubmitGesture"/> 在焦点变化时维护。
    /// </summary>
    [ObservableProperty] private bool _editing;

    public AuxAddressRowViewModel(AuxAddressItem DefinitionItem, IPageAddressService Service)
    {
        Definition = DefinitionItem;
        _service = Service;

        // 数组模式的初始值：取主定义里的"第 0 点"地址（结构体基址 + 偏移）
        AddressText = Definition.ByPointAddress(0);
    }

    /// <summary>底层定义（含结构体参数）。</summary>
    public AuxAddressItem Definition { get; }

    /// <summary>显示名称。</summary>
    public string Name => Definition.DisplayName;

    /// <summary>
    /// 提交请求：本行有一次"用户改完"要落 PLC。由页面接管（权限 / 手动使能 / 失败提示都在那边统一做）。
    /// </summary>
    public Action<AuxAddressRowViewModel>? SubmitRequest { get; set; }

    /// <summary>当前点对应的地址文本（结构体数组模式随 <see cref="PointSequence"/> 变化）。</summary>
    [ObservableProperty] private string _addressText = string.Empty;

    /// <summary>是否开关类（M 区布尔）。</summary>
    public bool IsSwitch => Definition.IsSwitch;

    /// <summary>是否数值类（非开关）。</summary>
    public bool IsNumericValue => !Definition.IsSwitch;

    /// <summary>是否跟随点序号寻址（界面上给个提示）。</summary>
    public bool IsStructArray => Definition.IsStructArray;

    /// <summary>单位文本。</summary>
    public string Unit => Definition.Unit ?? string.Empty;

    /// <summary>输入提示：未开手动使能时说明为什么改不了。</summary>
    public string InputHint => Editable
        ? $"{Name}：填完按回车（或点别处）即写入 PLC"
        : $"{Name}：请先打开手动使能，才能修改辅助地址的值";

    partial void OnPointSequenceChanged(int value) => RefreshAddress();

    partial void OnEditableChanged(bool value) => OnPropertyChanged(nameof(InputHint));

    /// <summary>
    /// 按当前点序号重算地址文本（2026-10-21）。
    /// <para><b>为什么单独一个方法：</b>结构体成员的类型 / 长度被改过之后（编辑器里保存），
    /// 点序号可能没变，但地址必须重算——否则界面还显示旧偏移（例如点胶速度停在 D3506，
    /// 而结构体重排后它已经前移到 D3505）。</para>
    /// </summary>
    public void RefreshAddress() => AddressText = Definition.ByPointAddress(PointSequence);

    /// <summary>开关勾选/取消：立即提交（与数值框"填完提交"同一套语义）。</summary>
    partial void OnSwitchStateChanged(bool value)
    {
        if (_refresh || !Editable)
        {
            return;
        }

        SubmitRequest?.Invoke(this);
    }

    /// <inheritdoc />
    public void MarkEdit(bool Value) => Editing = Value;

    /// <inheritdoc />
    public void Submit()
    {
        if (!Editable)
        {
            return;
        }

        SubmitRequest?.Invoke(this);
    }

    /// <summary>按当前点序号换算出"用于读写的那一份定义"（地址已解析）。</summary>
    private AuxAddressItem CurrentItem()
    {
        if (!Definition.IsStructArray)
        {
            return Definition;
        }

        var copy = Definition.Clone();
        copy.Address = AddressText;
        return copy;
    }

    /// <summary>从 PLC 读取当前值（数值读 D 区，开关读 M 区线圈）。</summary>
    public async Task ReadAsync()
    {
        if (IsSwitch)
        {
            var State = await _service.ReadAuxSwitchAsync(CurrentItem()).ConfigureAwait(false);

            _refresh = true;
            SwitchState = State;
            _refresh = false;
            return;
        }

        var Value = await _service.ReadAuxAddressAsync(CurrentItem()).ConfigureAwait(false);
        Text = CoordinateAux.Format(Value);
    }

    /// <summary>
    /// 写回 PLC 后按 PLC 的值刷新显示（写入失败或求值失败时用，避免界面停在"看起来成功了"的值）。
    /// </summary>
    public Task RefreshShowAsync() => ReadAsync();

    /// <summary>
    /// 一整页辅助地址行的批量刷新（2026-11-19 性能改造）。
    /// <para><b>为什么放在行类里：</b>所有行都持有同一个 <see cref="_service"/> 实例（由
    /// <c>辅助地址实时写入手柄.创建</c> 统一注入），这里取第 0 行的服务即可，
    /// 不必改手柄的构造器、也不必改各页 ViewModel 的构造流程。</para>
    /// <para><b>与逐行读语义一致：</b>跳过正在输入的行（不能把 PLC 旧值盖到打字中的输入框）、
    /// 开关类（M 区线圈）仍逐点读、单项/单块失败只跳过该项。</para>
    /// </summary>
    public static async Task BatchRefreshAsync(IReadOnlyList<AuxAddressRowViewModel> RowCollection)
    {
        if (RowCollection.Count == 0)
        {
            return;
        }

        // 开关（M 区）不进 D 区合并计划，逐点读保持原有语义
        foreach (var rowItem in RowCollection.Where(Row => !Row.Editing && Row.IsSwitch))
        {
            try
            {
                await rowItem.ReadAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // 单项失败只跳过，与逐行读取的容错口径一致
            }
            catch (System.IO.IOException)
            {
            }
            catch (System.Net.Sockets.SocketException)
            {
            }
        }

        var numericValueRow = RowCollection.Where(Row => !Row.Editing && !Row.IsSwitch).ToList();

        if (numericValueRow.Count == 0)
        {
            return;
        }

        var itemList = numericValueRow.Select(Row => Row.CurrentItem()).ToList();

        IReadOnlyList<double?> ValueList;

        try
        {
            ValueList = await numericValueRow[0]._service
                .BatchReadAuxAddressAsync(itemList)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return;   // 整批失败：本周期跳过，不打断页面
        }
        catch (System.IO.IOException)
        {
            return;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return;
        }

        for (var Sequence = 0; Sequence < numericValueRow.Count && Sequence < ValueList.Count; Sequence++)
        {
            if (ValueList[Sequence] is { } Value)
            {
                numericValueRow[Sequence].Text = CoordinateAux.Format(Value);
            }
        }
    }

    /// <summary>把当前文本写入 PLC。</summary>
    public async Task WriteAsync()
    {
        if (!CoordinateAux.TryParse(Text, out var Value))
        {
            throw new InvalidOperationException($"{Name} 的值「{Text}」不是有效数字。");
        }

        await _service.WriteAuxAddressAsync(CurrentItem(), Value).ConfigureAwait(false);
    }

    /// <summary>写入开关状态（M 区线圈）。</summary>
    public async Task WriteSwitchAsync() =>
        await _service.WriteAuxSwitchAsync(CurrentItem(), SwitchState).ConfigureAwait(false);
}

/// <summary>
/// 页面上一个动作按钮（电机复位、电批启动、出胶阀、吸螺丝真空…）。
///
/// <para><b>切换型（现场口径 2026-10-13）：</b>首次按下置位（激活），再次按下复位（取消激活）。
/// 激活状态直接显示在按钮外观上——比弹确认框更直观，也不会让人对弹窗麻木。</para>
/// </summary>
public sealed partial class ActionButtonViewModel : ObservableObject
{
    /// <summary>是否已激活（= 对应 M 线圈已置位）。</summary>
    [ObservableProperty] private bool _activated;

    public ActionButtonViewModel(ActionButtonItem DefinitionItem) => Definition = DefinitionItem;

    /// <summary>按钮定义。</summary>
    public ActionButtonItem Definition { get; }

    /// <summary>显示名称。</summary>
    public string Name => Definition.DisplayName;

    /// <summary>是否需要二次确认（切换型按钮已不再弹确认，保留供配置兼容与后续使用）。</summary>
    public bool IsDanger => Definition.IsDanger;

    /// <summary>地址文本（只读展示）。</summary>
    public string AddressText => Definition.Address;

    /// <summary>
    /// 按钮上显示的文字：固定按钮按线圈状态切换（0 = 默认参数 / 1 = 独立参数），
    /// 普通动作按钮始终显示名称。
    /// </summary>
    public string DisplayName => Definition.StateText(Activated);

    /// <summary>状态提示（悬停显示当前状态与下一次按下的效果）。</summary>
    public string StateHint => Activated
        ? $"{Name}：{DisplayName}（{AddressText} 置位中，再次按下取消）"
        : $"{Name}：{DisplayName}（按下置位 {AddressText}）";

    partial void OnActivatedChanged(bool value)
    {
        // 固定按钮的文字随状态变化，必须显式通知（否则回读到 1 后按钮仍写着"默认参数"）
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(StateHint));
    }
}
