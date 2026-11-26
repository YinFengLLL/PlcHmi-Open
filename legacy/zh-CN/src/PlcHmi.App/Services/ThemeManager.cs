using System.Text.Json;
using System.Windows;
using 上位机.业务.配置;

namespace 上位机.应用.服务;

/// <summary>
/// 深浅双主题管理。
///
/// <para><b>切换机制</b>：整体替换 Application 资源中的主题调色板字典
/// （<c>Themes/Palette.Dark.xaml</c> ↔ <c>Themes/Palette.Light.xaml</c>，两字典键集合严格一致），
/// 全站颜色类令牌以 DynamicResource 引用，替换后界面即时刷新，无需重启。</para>
///
/// <para><b>已知边界</b>：转换器（Converter）在绑定求值时经 TryFindResource 取色，
/// 切换瞬间不主动重估——监控类绑定随下一轮采集刷新（≤1s），静态文案本就走
/// DynamicResource 不受影响。</para>
/// </summary>
public static class 主题管理器
{
    private const string 深色调色板路径 = "Themes/Palette.Dark.xaml";
    private const string 浅色调色板路径 = "Themes/Palette.Light.xaml";

    /// <summary>当前生效主题（<see cref="界面选项.主题深色"/> / <see cref="界面选项.主题浅色"/>）。</summary>
    public static string 当前主题 { get; private set; } = 界面选项.主题深色;

    /// <summary>
    /// 主题切换完成后触发。绝大多数引用走 DynamicResource 自动刷新，
    /// 本事件只服务需要主动重绘的场合（如代码后置绘制的图表控件）。
    /// </summary>
    public static event EventHandler? 主题已变更;

    /// <summary>解析配置文本为主题名；未知值一律回退深色（与 <see cref="界面选项"/> 默认值一致）。</summary>
    public static string 解析(string? 文本) =>
        string.Equals(文本, 界面选项.主题浅色, StringComparison.OrdinalIgnoreCase)
            ? 界面选项.主题浅色
            : 界面选项.主题深色;

    /// <summary>
    /// 应用主题：替换合并字典中的主题调色板。与当前主题相同且字典已就位则跳过。
    /// 必须在界面线程调用（启动期与设置页交互均满足）。
    /// </summary>
    public static void 应用(string? 主题)
    {
        var 目标 = 解析(主题);

        var 应用对象 = Application.Current
            ?? throw new InvalidOperationException("主题管理器须在 Application 启动后使用");

        var 字典集合 = 应用对象.Resources.MergedDictionaries;
        var 索引 = -1;

        for (var 位置 = 0; 位置 < 字典集合.Count; 位置++)
        {
            var 来源 = 字典集合[位置].Source?.OriginalString ?? string.Empty;

            if (来源.Contains(深色调色板路径) || 来源.Contains(浅色调色板路径))
            {
                索引 = 位置;
                break;
            }
        }

        if (索引 < 0)
        {
            throw new InvalidOperationException("未在合并字典中找到主题调色板（Palette.Dark/Light.xaml）");
        }

        var 期望路径 = 目标 == 界面选项.主题深色 ? 深色调色板路径 : 浅色调色板路径;

        if (目标 == 当前主题 && 字典集合[索引].Source?.OriginalString.Contains(期望路径) == true)
        {
            return;
        }

        字典集合[索引] = new ResourceDictionary { Source = new Uri(期望路径, UriKind.Relative) };
        当前主题 = 目标;
        主题已变更?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// 应用主题并写入 <c>config/appsettings.json</c>（界面.主题），重启后仍生效。
    /// 同步更新共享的 <see cref="界面选项"/> 单例，界面显示与落盘值一致。
    /// </summary>
    /// <remarks>
    /// 异常（含落盘失败）向上抛：调用方一律经 <see cref="异步安全调用"/> 即发即忘，
    /// 由它统一记录日志——不在此处再包一层 try/catch（catch 总额受规模门禁约束）。
    /// 落盘失败不回滚界面：本次切换仍生效、重启回旧主题，比"点了没反应"好排查。
    /// </remarks>
    public static async Task 异步应用并保存(
        string 主题, 界面选项 界面, CancellationToken 取消令牌 = default)
    {
        ArgumentNullException.ThrowIfNull(界面);

        var 目标 = 解析(主题);
        应用(目标);
        界面.主题 = 目标;

        // 与其余节共存：读全量文档 → 只改主题字段 → 原子写回（同 PlcConnectionSettingsService 模式）
        var 文档 = 应用设置加载器.加载(out _);
        文档.界面.主题 = 目标;

        var JSON文本 = JsonSerializer.Serialize(文档, JSON配置.写入);

        await 原子文件.异步写入全部文本(配置路径.应用设置, JSON文本, 取消令牌)
            .ConfigureAwait(false);
    }
}
