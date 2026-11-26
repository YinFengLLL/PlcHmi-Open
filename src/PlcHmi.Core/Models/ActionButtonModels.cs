namespace PlcHmi.Core.Models;

/// <summary>
/// 一个点动/动作按钮（出胶阀、电机复位、电机点动…）。地址为 M 区线圈。
///
/// <para><b>固定按钮（2026-10-21）：</b>点胶页的「参数模式」按钮是页面必备元件——
/// 始终排在动作区首位、可改地址与名称、不可删除，并按线圈状态切换文字
/// （0 = 默认参数 / 1 = 独立参数）。从 <c>AuxPointModels.cs</c> 拆出，守住单文件 400 行门禁。</para>
/// </summary>
public sealed class ActionButtonItem
{
    /// <summary>键。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>界面显示名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>所属页面分组（<c>锁附</c> / <c>点胶</c> / <c>标定</c>），空表示所有页面都显示。</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>M 区线圈地址。</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>是否为脉冲（置位一段时间后自动复位）。</summary>
    public bool IsPulse { get; set; } = true;

    /// <summary>危险等级：需要二次确认的动作用 <c>true</c>。</summary>
    public bool IsDanger { get; set; }

    /// <summary>
    /// 是否为<b>固定按钮</b>：始终显示在本页动作区的<b>首位</b>，可改地址与名称，不可删除。
    /// <para>2026-10-21 现场要求：点胶页需要一个"参数模式"按钮（M5006，0 = 默认参数、
    /// 1 = 独立参数），它属于页面必备元件而不是管理员自定义动作，因此与普通动作按钮分开对待。</para>
    /// </summary>
    public bool Fixed { get; set; }

    /// <summary>状态 0（线圈复位）时按钮显示的文字；为空时用 <see cref="DisplayName"/>。</summary>
    public string? State0Text { get; set; }

    /// <summary>状态 1（线圈置位）时按钮显示的文字；为空时用 <see cref="DisplayName"/>。</summary>
    public string? State1Text { get; set; }

    /// <summary>按线圈实际状态取按钮文字（未配状态文本的普通按钮始终显示名称）。</summary>
    public string StateText(bool Activate) =>
        (Activate ? State1Text : State0Text) is { Length: > 0 } Text ? Text : DisplayName;

    /// <summary>显示名称（为空时回退到键）。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Key : Name;
}
