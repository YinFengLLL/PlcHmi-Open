using System.Text.Json;
using System.Windows;
using PlcHmi.Business.Configuration;

namespace PlcHmi.App.Services;

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
public static class ThemeManager
{
    private const string darkColorAdjustColorBoardPath = "Themes/Palette.Dark.xaml";
    private const string lightColorAdjustColorBoardPath = "Themes/Palette.Light.xaml";

    /// <summary>当前生效主题（<see cref="UiOptions.themeDarkColor"/> / <see cref="UiOptions.themeLightColor"/>）。</summary>
    public static string CurrentTheme { get; private set; } = UiOptions.themeDarkColor;

    /// <summary>
    /// 主题切换完成后触发。绝大多数引用走 DynamicResource 自动刷新，
    /// 本事件只服务需要主动重绘的场合（如代码后置绘制的图表控件）。
    /// </summary>
    public static event EventHandler? themeChanged;

    /// <summary>解析配置文本为主题名；未知值一律回退深色（与 <see cref="UiOptions"/> 默认值一致）。</summary>
    public static string Parse(string? Text) =>
        string.Equals(Text, UiOptions.themeLightColor, StringComparison.OrdinalIgnoreCase)
            ? UiOptions.themeLightColor
            : UiOptions.themeDarkColor;

    /// <summary>
    /// 应用主题：替换合并字典中的主题调色板。与当前主题相同且字典已就位则跳过。
    /// 必须在界面线程调用（启动期与设置页交互均满足）。
    /// </summary>
    public static void App(string? Theme)
    {
        var target = Parse(Theme);

        var applyPairObject = Application.Current
            ?? throw new InvalidOperationException("主题管理器须在 Application 启动后使用");

        var dictionaryCollection = applyPairObject.Resources.MergedDictionaries;
        var index = -1;

        for (var Position = 0; Position < dictionaryCollection.Count; Position++)
        {
            var Source = dictionaryCollection[Position].Source?.OriginalString ?? string.Empty;

            if (Source.Contains(darkColorAdjustColorBoardPath) || Source.Contains(lightColorAdjustColorBoardPath))
            {
                index = Position;
                break;
            }
        }

        if (index < 0)
        {
            throw new InvalidOperationException("未在合并字典中找到主题调色板（Palette.Dark/Light.xaml）");
        }

        var expectedPath = target == UiOptions.themeDarkColor ? darkColorAdjustColorBoardPath : lightColorAdjustColorBoardPath;

        if (target == CurrentTheme && dictionaryCollection[index].Source?.OriginalString.Contains(expectedPath) == true)
        {
            return;
        }

        dictionaryCollection[index] = new ResourceDictionary { Source = new Uri(expectedPath, UriKind.Relative) };
        CurrentTheme = target;
        themeChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// 应用主题并写入 <c>config/appsettings.json</c>（界面.主题），重启后仍生效。
    /// 同步更新共享的 <see cref="UiOptions"/> 单例，界面显示与落盘值一致。
    /// </summary>
    /// <remarks>
    /// 异常（含落盘失败）向上抛：调用方一律经 <see cref="SafeInvokeAsync"/> 即发即忘，
    /// 由它统一记录日志——不在此处再包一层 try/catch（catch 总额受规模门禁约束）。
    /// 落盘失败不回滚界面：本次切换仍生效、重启回旧主题，比"点了没反应"好排查。
    /// </remarks>
    public static async Task ApplyAndSaveAsync(
        string Theme, UiOptions Ui, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(Ui);

        var target = Parse(Theme);
        App(target);
        Ui.Theme = target;

        // 与其余节共存：读全量文档 → 只改主题字段 → 原子写回（同 PlcConnectionSettingsService 模式）
        var Document = AppSettingsLoader.Load(out _);
        Document.Ui.Theme = target;

        var jsonText = JsonSerializer.Serialize(Document, JsonConfig.Write);

        await AtomicFile.WriteAllTextAsync(ConfigPath.AppSettings, jsonText, cancellationToken)
            .ConfigureAwait(false);
    }
}
