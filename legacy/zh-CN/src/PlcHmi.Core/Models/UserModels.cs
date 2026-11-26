namespace 上位机.核心.模型;

/// <summary>
/// 用户角色。
/// <para>枚举值即权限等级，数值大的包含数值小的全部权限。</para>
/// </summary>
public enum 用户角色
{
    /// <summary>操作员：只能看运行监控、IO 点位、报警记录（均只读）。</summary>
    操作员 = 0,

    /// <summary>工程师：可进入全部页面、修改参数值，但不能改绑定地址。</summary>
    工程师 = 1,

    /// <summary>管理员：在工程师基础上，可改绑定地址、增删手动页元件、管理账号。</summary>
    管理员 = 2,
}

/// <summary>
/// 权限点。
///
/// <para>用位标志而非"角色等级比较"，是因为权限并非严格线性——
/// 例如将来可能给某个角色单独开一项权限而不影响其他角色。</para>
/// </summary>
[Flags]
public enum 权限
{
    /// <summary>无权限。</summary>
    无 = 0,

    /// <summary>查看运行监控页。</summary>
    视图运行页面 = 1 << 0,

    /// <summary>查看 IO 点位与报警记录（只读）。</summary>
    视图监控页面 = 1 << 1,

    /// <summary>查看全部页面（参数 / 示教 / 机种 / 手动 / 系统设置）。</summary>
    视图全部页面 = 1 << 2,

    /// <summary>修改参数值。</summary>
    编辑参数值列表 = 1 << 3,

    /// <summary>修改绑定地址（参数项地址、手动页元件绑定）。</summary>
    编辑绑定地址 = 1 << 4,

    /// <summary>增删手动页元件。</summary>
    编辑手动元件 = 1 << 5,

    /// <summary>切换机种。</summary>
    切换配方 = 1 << 6,

    /// <summary>示教编辑。</summary>
    编辑示教 = 1 << 7,

    /// <summary>管理账号（改密码、加账号）。</summary>
    管理用户 = 1 << 8,

    /// <summary>修改 PLC 连接参数（IP / 端口 / 从站号 / 超时等），保存后断开重连立即生效。</summary>
    编辑PLC连接 = 1 << 9,

    /// <summary>导入 / 查看汇川变量表（系统设置页）。</summary>
    导入变量表 = 1 << 10,

    /// <summary>增删 IO 点位区（IO 页新增 X / Y 区，改变采集范围）。</summary>
    编辑IO点位 = 1 << 11,

    /// <summary>
    /// 修改历史记录采集配置（触发位、数据项地址与小数位），并清除历史数据。
    /// <para>改的是"设备动作会被记成什么"，删的是"已经记下来的追溯依据"——
    /// 两者都不可由非管理员操作。</para>
    /// </summary>
    编辑历史记录 = 1 << 12,

    /// <summary>
    /// 切换运行模式（Modbus 模式下在真实 PLC / 模拟 PLC 之间切换）。
    /// <para>切到模拟模式后上位机不再受真实设备反馈约束，且会开放本地 Modbus 从站——
    /// 属必须由管理员显式授权的调试能力。</para>
    /// </summary>
    切换运行模式 = 1 << 13,
}

/// <summary>角色 → 权限的映射。</summary>
public static class 角色权限
{
    /// <summary>操作员：运行监控 + 只读的 IO/报警。</summary>
    private const 权限 操作员权限 =
        权限.视图运行页面 | 权限.视图监控页面;

    /// <summary>工程师：加全部页面、改参数值、切机种、示教。**不含改绑定地址**。</summary>
    private const 权限 工程师权限 =
        操作员权限 |
        权限.视图全部页面 |
        权限.编辑参数值列表 |
        权限.切换配方 |
        权限.编辑示教;

    /// <summary>管理员：加改绑定地址、增删元件、账号管理、PLC 连接参数、变量表导入、增删 IO 点位区、历史记录配置。</summary>
    private const 权限 管理员权限 =
        工程师权限 |
        权限.编辑绑定地址 |
        权限.编辑手动元件 |
        权限.管理用户 |
        权限.编辑PLC连接 |
        权限.导入变量表 |
        权限.编辑IO点位 |
        权限.编辑历史记录 |
        权限.切换运行模式;

    /// <summary>取某角色的全部权限。</summary>
    public static 权限 的(用户角色 角色项) => 角色项 switch
    {
        用户角色.操作员 => 操作员权限,
        用户角色.工程师 => 工程师权限,
        用户角色.管理员 => 管理员权限,
        _ => 权限.无,
    };

    /// <summary>判断角色是否具备某项权限。</summary>
    public static bool 有(用户角色 角色项, 权限 权限项) =>
        (的(角色项) & 权限项) == 权限项;

    /// <summary>角色的中文名。</summary>
    public static string 显示名称(用户角色 角色项) => 角色项 switch
    {
        用户角色.操作员 => "操作员",
        用户角色.工程师 => "工程师",
        用户角色.管理员 => "管理员",
        _ => "未知",
    };
}

/// <summary>
/// 账号。
/// <para><b>只存密码哈希与盐，绝不存明文。</b></para>
/// </summary>
public sealed class 用户账号
{
    /// <summary>登录名。</summary>
    public string 名称 { get; set; } = string.Empty;

    /// <summary>显示名。</summary>
    public string 显示名称 { get; set; } = string.Empty;

    /// <summary>角色。</summary>
    public 用户角色 角色 { get; set; } = 用户角色.操作员;

    /// <summary>PBKDF2 派生出的密码哈希（Base64）。</summary>
    public string 密码哈希 { get; set; } = string.Empty;

    /// <summary>盐（Base64）。</summary>
    public string 盐值 { get; set; } = string.Empty;

    /// <summary>是否使用初始密码（为真时界面提示尽快修改）。</summary>
    public bool 是否默认密码 { get; set; }
}

/// <summary><c>config/users.json</c> 的顶层结构。</summary>
public sealed class 用户文件
{
    /// <summary>账号列表。</summary>
    public List<用户账号> 用户 { get; set; } = new();
}
