using 上位机.核心.模型;

namespace 上位机.业务.配置;

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
public static class 参数模式按钮缺省
{
    /// <summary>按钮键（配置内唯一）。</summary>
    public const string 键 = "点胶-参数模式";

    /// <summary>所属页面分组。</summary>
    public const string 分组 = "点胶";

    /// <summary>M 区缺省地址（现场口径）。</summary>
    public const string 缺省地址 = "M5006";

    /// <summary>状态 0 文字。</summary>
    public const string 状态0文本 = "默认参数";

    /// <summary>状态 1 文字。</summary>
    public const string 状态1文本 = "独立参数";

    /// <summary>给缺少该按钮的配置补一条；返回补齐条数（0 = 已存在，无需补齐）。</summary>
    public static int 应用(配方配置 配置对象)
    {
        ArgumentNullException.ThrowIfNull(配置对象);

        if (配置对象.动作按钮.Any(项 => 项.固定
                && string.Equals(项.分组, 分组, StringComparison.OrdinalIgnoreCase)))
        {
            return 0;
        }

        // 插到最前：即使别处按配置顺序展示，它也是第一个（服务层另有"固定按钮排首位"的排序）
        配置对象.动作按钮.Insert(0, new 动作按钮项
        {
            键 = 键,
            名称 = "参数模式",
            分组 = 分组,
            地址 = 缺省地址,
            是否脉冲 = false,
            是否危险 = false,
            固定 = true,
            状态0文本 = 状态0文本,
            状态1文本 = 状态1文本,
        });

        return 1;
    }
}
