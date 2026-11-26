using PlcHmi.Core.Models;

namespace PlcHmi.Core.Abstractions;

/// <summary>
/// 服务层权限守卫。
///
/// <para><b>为什么界面隐藏按钮不算保护：</b>界面只是防误触，不是安全边界。
/// 界面隐藏后命令仍可被任何持有服务引用的代码调用 —— 一段脚本、一个将来的 Web 接口、
/// 或者一个忘了隐藏按钮的新页面。按本项目审查标准 R5：
/// <i>"管理员专有功能（延时表 / 按钮表 / 报警 / 元件编辑）双重校验 → 缺失 = 🔴"</i>。</para>
///
/// <para>因此每个<b>写操作</b>在服务层入口再校验一次身份。校验发生在参数校验之前、
/// 任何状态变更之前 —— 被拒绝的调用不留半点痕迹。</para>
/// </summary>
public interface IPermissionGuard
{
    /// <summary>
    /// 要求当前身份具备指定权限。
    /// </summary>
    /// <param name="permission">该操作所需权限。</param>
    /// <exception cref="UnauthorizedAccessException">当前身份不具备该权限。</exception>
    void WantRequire(Permission permissionItem);
}
