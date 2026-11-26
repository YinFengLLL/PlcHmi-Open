using PlcHmi.Core.Models;

namespace PlcHmi.Business.Configuration;

/// <summary>
/// 点胶页「参数模式」固定按钮的出厂定义与<b>老现场配置补齐</b>（2026-10-21 现场要求）。
///
/// <para><b>语义：</b>读一个 M 区位——<c>0</c> → 按钮显示「默认参数」（按机种统一参数点胶），
/// <c>1</c> → 显示「独立参数」（按每个点的单点工艺参数点胶）。按钮固定排在动作区首位，
/// 可改地址（缺省 <c>M5006</c>）与名称，但不可删除。</para>
///
/// <para><b>为什么要补齐：</b>覆盖升级不会覆盖现场的 <c>recipe-config.json</c>，
/// 老文件里没有这条动作按钮 → 新版本装上后按钮不会出现（现场表现为"功能没生效"）。
/// 这里在加载时补一条配置（并随下一次保存落盘），管理员可照常改地址。</para>
/// </summary>
public static class ParameterModeButtonDefault
{
    /// <summary>按钮键（配置内唯一）。</summary>
    public const string Key = "点胶-参数模式";

    /// <summary>所属页面分组。</summary>
    public const string Group = "点胶";

    /// <summary>M 区缺省地址（现场口径）。</summary>
    public const string defaultAddress = "M5006";

    /// <summary>状态 0 文字。</summary>
    public const string State0Text = "默认参数";

    /// <summary>状态 1 文字。</summary>
    public const string State1Text = "独立参数";

    /// <summary>给缺少该按钮的配置补一条；返回补齐条数（0 = 已存在，无需补齐）。</summary>
    public static int App(RecipeConfig configPairObject)
    {
        ArgumentNullException.ThrowIfNull(configPairObject);

        if (configPairObject.ActionButton.Any(Item => Item.Fixed
                && string.Equals(Item.Group, Group, StringComparison.OrdinalIgnoreCase)))
        {
            return 0;
        }

        // 插到最前：即使别处按配置顺序展示，它也是第一个（服务层另有"固定按钮排首位"的排序）
        configPairObject.ActionButton.Insert(0, new ActionButtonItem
        {
            Key = Key,
            Name = "参数模式",
            Group = Group,
            Address = defaultAddress,
            IsPulse = false,
            IsDanger = false,
            Fixed = true,
            State0Text = State0Text,
            State1Text = State1Text,
        });

        return 1;
    }
}
