using Microsoft.Extensions.Logging;
using 上位机.核心.抽象;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// 生产用权限守卫：按当前登录身份判定。
///
/// <para>未登录时 <see cref="用户服务接口.当前角色"/> 是操作员（最低权限），
/// 因此"没登录"天然等于"只能看"，不需要额外分支。</para>
/// </summary>
public sealed class 用户权限守卫 : 权限守卫接口
{
    private readonly 用户服务接口 _用户项;
    private readonly ILogger<用户权限守卫> _日志;

    public 用户权限守卫(用户服务接口 用户, ILogger<用户权限守卫> 日志)
    {
        _用户项 = 用户;
        _日志 = 日志;
    }

    /// <inheritdoc />
    public void 要求(权限 权限项)
    {
        if (_用户项.有(权限项))
        {
            return;
        }

        // 记 Warning 而不是 Debug：拒绝要么是有人在越权，要么是权限配置错了，
        // 两种都值得在现场日志里留痕。
        _日志.LogWarning(
            "权限拒绝：身份「{User}」缺少 {Permission}，操作未执行",
            _用户项.当前显示名称, 权限项);

        throw new UnauthorizedAccessException(
            $"当前身份「{_用户项.当前显示名称}」没有「{权限项}」权限，操作已被拒绝。");
    }
}

/// <summary>
/// 全部放行的权限守卫。<b>只用于单元测试与无界面的批处理</b>（如 <c>--export-csv</c>），
/// 绝不可注册进生产 DI。
///
/// <para>它存在的意义是让测试显式声明"本用例不验证权限边界"，
/// 而不是让服务悄悄在缺少守卫时默认放行。</para>
/// </summary>
public sealed class 允许全部权限守卫 : 权限守卫接口
{
    /// <inheritdoc />
    public void 要求(权限 权限项)
    {
        // 刻意空实现：调用方已在测试里显式声明本用例不覆盖权限分支
    }
}
