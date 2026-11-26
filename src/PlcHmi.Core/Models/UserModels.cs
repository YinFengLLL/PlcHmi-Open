namespace PlcHmi.Core.Models;

/// <summary>
/// 用户角色。
/// <para>枚举值即权限等级，数值大的包含数值小的全部权限。</para>
/// </summary>
public enum UserRole
{
    /// <summary>操作员：只能看运行监控、IO 点位、报警记录（均只读）。</summary>
    Operator = 0,

    /// <summary>工程师：可进入全部页面、修改参数值，但不能改绑定地址。</summary>
    Engineer = 1,

    /// <summary>管理员：在工程师基础上，可改绑定地址、增删手动页元件、管理账号。</summary>
    Admin = 2,
}

/// <summary>
/// 权限点。
///
/// <para>用位标志而非"角色等级比较"，是因为权限并非严格线性——
/// 例如将来可能给某个角色单独开一项权限而不影响其他角色。</para>
/// </summary>
[Flags]
public enum Permission
{
    /// <summary>无权限。</summary>
    None = 0,

    /// <summary>查看运行监控页。</summary>
    ViewRunPage = 1 << 0,

    /// <summary>查看 IO 点位与报警记录（只读）。</summary>
    ViewMonitorControlPage = 1 << 1,

    /// <summary>查看全部页面（参数 / 示教 / 机种 / 手动 / 系统设置）。</summary>
    ViewAllPage = 1 << 2,

    /// <summary>修改参数值。</summary>
    EditParameterValueList = 1 << 3,

    /// <summary>修改绑定地址（参数项地址、手动页元件绑定）。</summary>
    EditBindAddress = 1 << 4,

    /// <summary>增删手动页元件。</summary>
    EditManualComponent = 1 << 5,

    /// <summary>切换机种。</summary>
    SwitchRecipe = 1 << 6,

    /// <summary>示教编辑。</summary>
    EditTeach = 1 << 7,

    /// <summary>管理账号（改密码、加账号）。</summary>
    ManageUser = 1 << 8,

    /// <summary>修改 PLC 连接参数（IP / 端口 / 从站号 / 超时等），保存后断开重连立即生效。</summary>
    EditPlcConnect = 1 << 9,

    /// <summary>导入 / 查看汇川变量表（系统设置页）。</summary>
    ImportVariableTable = 1 << 10,

    /// <summary>增删 IO 点位区（IO 页新增 X / Y 区，改变采集范围）。</summary>
    EditIoPoint = 1 << 11,

    /// <summary>
    /// 修改历史记录采集配置（触发位、数据项地址与小数位），并清除历史数据。
    /// <para>改的是"设备动作会被记成什么"，删的是"已经记下来的追溯依据"——
    /// 两者都不可由非管理员操作。</para>
    /// </summary>
    EditHistoryRecord = 1 << 12,

    /// <summary>
    /// 切换运行模式（Modbus 模式下在真实 PLC / 模拟 PLC 之间切换）。
    /// <para>切到模拟模式后上位机不再受真实设备反馈约束，且会开放本地 Modbus 从站——
    /// 属必须由管理员显式授权的调试能力。</para>
    /// </summary>
    SwitchRunMode = 1 << 13,
}

/// <summary>角色 → 权限的映射。</summary>
public static class RolePermission
{
    /// <summary>操作员：运行监控 + 只读的 IO/报警。</summary>
    private const Permission OperatorPermission =
        Permission.ViewRunPage | Permission.ViewMonitorControlPage;

    /// <summary>工程师：加全部页面、改参数值、切机种、示教。**不含改绑定地址**。</summary>
    private const Permission engineerPermission =
        OperatorPermission |
        Permission.ViewAllPage |
        Permission.EditParameterValueList |
        Permission.SwitchRecipe |
        Permission.EditTeach;

    /// <summary>管理员：加改绑定地址、增删元件、账号管理、PLC 连接参数、变量表导入、增删 IO 点位区、历史记录配置。</summary>
    private const Permission adminPermission =
        engineerPermission |
        Permission.EditBindAddress |
        Permission.EditManualComponent |
        Permission.ManageUser |
        Permission.EditPlcConnect |
        Permission.ImportVariableTable |
        Permission.EditIoPoint |
        Permission.EditHistoryRecord |
        Permission.SwitchRunMode;

    /// <summary>取某角色的全部权限。</summary>
    public static Permission Of(UserRole roleItem) => roleItem switch
    {
        UserRole.Operator => OperatorPermission,
        UserRole.Engineer => engineerPermission,
        UserRole.Admin => adminPermission,
        _ => Permission.None,
    };

    /// <summary>判断角色是否具备某项权限。</summary>
    public static bool Has(UserRole roleItem, Permission permissionItem) =>
        (Of(roleItem) & permissionItem) == permissionItem;

    /// <summary>角色的中文名。</summary>
    public static string DisplayName(UserRole roleItem) => roleItem switch
    {
        UserRole.Operator => "操作员",
        UserRole.Engineer => "工程师",
        UserRole.Admin => "管理员",
        _ => "未知",
    };
}

/// <summary>
/// 账号。
/// <para><b>只存密码哈希与盐，绝不存明文。</b></para>
/// </summary>
public sealed class UserAccount
{
    /// <summary>登录名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>显示名。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>角色。</summary>
    public UserRole Role { get; set; } = UserRole.Operator;

    /// <summary>PBKDF2 派生出的密码哈希（Base64）。</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>盐（Base64）。</summary>
    public string Salt { get; set; } = string.Empty;

    /// <summary>是否使用初始密码（为真时界面提示尽快修改）。</summary>
    public bool IsDefaultPassword { get; set; }
}

/// <summary><c>config/users.json</c> 的顶层结构。</summary>
public sealed class UserFile
{
    /// <summary>账号列表。</summary>
    public List<UserAccount> User { get; set; } = new();
}
