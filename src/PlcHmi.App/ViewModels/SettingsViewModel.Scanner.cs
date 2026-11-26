namespace 上位机.应用.视图模型;

/// <summary>
/// <see cref="设置视图模型"/> 的「扫码枪设置」部分（partial，2026-11-12）。
///
/// <para>扫码枪的编辑状态与保存命令都放在独立的
/// <see cref="扫码枪设置视图模型"/> 里——主设置页只负责把它挂到界面上，
/// 这样"连接参数 / 变量表 / 主题 / 扫码枪"四块各改各的，互不挤行数。</para>
/// </summary>
public sealed partial class 设置视图模型
{
    /// <summary>扫码枪设置板块（未注册时为 <c>null</c>，界面整块隐藏）。</summary>
    private readonly 扫码枪设置视图模型? 扫码枪设置区;

    /// <summary>扫码枪设置板块（供界面绑定）。</summary>
    public 扫码枪设置视图模型? 扫码枪设置 => 扫码枪设置区;

    /// <summary>是否显示扫码枪设置卡（未注册该板块时整块隐藏）。</summary>
    public bool 显示扫码枪设置 => 扫码枪设置区 is not null;
}
