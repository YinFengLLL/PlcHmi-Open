using Microsoft.Extensions.Logging;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// 生产用权限守卫：按当前登录身份判定。
///
/// <para>未登录时 <see cref="IUserService.CurrentRole"/> 是操作员（最低权限），
/// 因此"没登录"天然等于"只能看"，不需要额外分支。</para>
/// </summary>
public sealed class UserPermissionGuard : IPermissionGuard
{
    private readonly IUserService _userItem;
    private readonly ILogger<UserPermissionGuard> _log;

    public UserPermissionGuard(IUserService User, ILogger<UserPermissionGuard> Log)
    {
        _userItem = User;
        _log = Log;
    }

    /// <inheritdoc />
    public void WantRequire(Permission permissionItem)
    {
        if (_userItem.Has(permissionItem))
        {
            return;
        }

        // 记 Warning 而不是 Debug：拒绝要么是有人在越权，要么是权限配置错了，
        // 两种都值得在现场日志里留痕。
        _log.LogWarning(
            "权限拒绝：身份「{User}」缺少 {Permission}，操作未执行",
            _userItem.CurrentDisplayName, permissionItem);

        throw new UnauthorizedAccessException(
            $"当前身份「{_userItem.CurrentDisplayName}」没有「{permissionItem}」权限，操作已被拒绝。");
    }
}

/// <summary>
/// 全部放行的权限守卫。<b>只用于单元测试与无界面的批处理</b>（如 <c>--export-csv</c>），
/// 绝不可注册进生产 DI。
///
/// <para>它存在的意义是让测试显式声明"本用例不验证权限边界"，
/// 而不是让服务悄悄在缺少守卫时默认放行。</para>
/// </summary>
public sealed class AllowAllPermissionGuard : IPermissionGuard
{
    /// <inheritdoc />
    public void WantRequire(Permission permissionItem)
    {
        // 刻意空实现：调用方已在测试里显式声明本用例不覆盖权限分支
    }
}
