using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using 上位机.业务.配置;
using 上位机.核心.抽象;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// 用户与权限服务。
///
/// <para><b>存储：</b><c>config/users.json</c>，只存 PBKDF2-SHA256 派生出的哈希与盐，
/// <b>绝不存明文密码</b>。校验用固定时间比较，避免时序侧信道。</para>
///
/// <para><b>登录策略：</b>密码登录，<b>不超时回落</b>——适合无人看守的触摸屏。</para>
/// </summary>
public sealed class 用户服务 : 用户服务接口
{
    /// <summary>PBKDF2 迭代次数。10 万次在工控机上约 30ms，足以拖慢暴力破解。</summary>
    private const int PBKDF2迭代次数 = 100_000;

    private const int 盐值大小 = 16;
    private const int 哈希大小 = 32;

    /// <summary>新密码最短长度。</summary>
    private const int 最小密码长度 = 6;

    private readonly ILogger<用户服务> _日志;
    private readonly 配置健康接口? _健康;
    private readonly 用户文件 _文件 = new();

    private 用户账号? _当前;

    public 用户服务(ILogger<用户服务> 日志, 配置健康接口? 健康 = null)
    {
        _日志 = 日志;
        _健康 = 健康;
    }

    /// <inheritdoc />
    public 用户角色 当前角色 => _当前?.角色 ?? 用户角色.操作员;

    /// <inheritdoc />
    public string 当前用户名称 => _当前?.名称 ?? string.Empty;

    /// <inheritdoc />
    public string 当前显示名称 => _当前?.显示名称 ?? "未登录（操作员）";

    /// <inheritdoc />
    public bool 是否已登录 => _当前 is not null;

    /// <inheritdoc />
    public 权限 当前权限 => 角色权限.的(当前角色);

    /// <inheritdoc />
    public bool 是否使用默认密码 => _当前?.是否默认密码 ?? false;

    /// <inheritdoc />
    public IReadOnlyList<用户账号> 账号集合 => _文件.用户;

    /// <inheritdoc />
    public event EventHandler? 用户已变更;

    /// <inheritdoc />
    public bool 有(权限 权限项) => 角色权限.有(当前角色, 权限项);

    // ── 初始化 ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task 异步初始化(CancellationToken 取消令牌 = default)
    {
        var 文件路径 = 配置路径.用户;

        var 结果值 = await 配置文件加载器
            .异步加载JSON<用户文件>(文件路径, JSON配置.读取, _日志, 取消令牌)
            .ConfigureAwait(false);

        _健康.上报("账号配置", 结果值);

        // 缺失（首次运行）与失败（主文件与 .bak 双损坏）都回退出厂三账号：
        // 账号文件损坏若不清空回填，现场将无人能登录（含 admin），只能手工删文件——
        // 这是"双损坏永久锁死"，比丢一次账号配置严重得多。
        if (结果值.加载结果 is 加载结果分类.缺失 or 加载结果分类.失败)
        {
            _日志.LogWarning("账号文件{Reason}，已恢复出厂默认账号（初始密码 {Password}）：{Path}",
                结果值.加载结果 is 加载结果分类.缺失 ? "不存在" : "已损坏（主文件与备份均无法读取）",
                用户服务接口.默认密码, 文件路径);

            _文件.用户.Clear();
            _文件.用户.Add(创建默认("operator", "操作员", 用户角色.操作员));
            _文件.用户.Add(创建默认("engineer", "工程师", 用户角色.工程师));
            _文件.用户.Add(创建默认("admin", "管理员", 用户角色.管理员));

            await 异步保存(取消令牌).ConfigureAwait(false);
            return;
        }

        _文件.用户.Clear();

        if (结果值.值 is not null)
        {
            _文件.用户.AddRange(结果值.值.用户);
        }

        if (结果值.加载结果 is 加载结果分类.已加载)
        {
            _日志.LogInformation("已加载 {Count} 个账号", _文件.用户.Count);
        }

        用户已变更?.Invoke(this, EventArgs.Empty);
    }

    // ── 登录 / 登出 ─────────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// PBKDF2 迭代 10 万次约需 30–50ms，属<b>同步 CPU 计算</b>。
    /// 放在线程池执行，避免登录时把 UI 线程卡住（也便于将来调高迭代次数）。
    /// </remarks>
    public async Task<bool> 异步登录(
        string 用户名称, string 密码, CancellationToken 取消令牌 = default)
    {
        var 账号项 = 查找(用户名称);

        if (账号项 is null)
        {
            _日志.LogWarning("登录失败：账号「{Name}」不存在", 用户名称);
            return false;
        }

        var 密码正确 = await Task.Run(() => 验证密码(密码, 账号项), 取消令牌)
            .ConfigureAwait(false);

        if (!密码正确)
        {
            _日志.LogWarning("登录失败：账号「{Name}」密码不正确", 用户名称);
            return false;
        }

        _当前 = 账号项;

        _日志.LogInformation("已登录：{Display}（{Role}）",
            账号项.显示名称, 角色权限.显示名称(账号项.角色));

        用户已变更?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <inheritdoc />
    public void 退出登录()
    {
        if (_当前 is null)
        {
            return;
        }

        _日志.LogInformation("已登出：{Display}", _当前.显示名称);

        _当前 = null;
        用户已变更?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    /// <remarks>与登录同理：PBKDF2 是同步 CPU 计算，放线程池执行避免卡住界面。</remarks>
    public async Task<bool> 异步变更密码(
        string 用户名称, string 当前密码, string 新密码, CancellationToken 取消令牌 = default)
    {
        var 账号项 = 查找(用户名称);
        var 实际用户 = 用户名称;

        if (账号项 is null)
        {
            _日志.LogWarning("修改密码失败：账号「{Name}」不存在", 实际用户);
            return false;
        }

        if (string.IsNullOrWhiteSpace(新密码) || 新密码.Length < 最小密码长度)
        {
            _日志.LogWarning("修改密码失败：新密码长度不足 {Min} 位", 最小密码长度);
            return false;
        }

        var 密码正确 = await Task.Run(() => 验证密码(当前密码, 账号项), 取消令牌)
            .ConfigureAwait(false);

        if (!密码正确)
        {
            _日志.LogWarning("修改密码失败：原密码不正确（{Name}）", 实际用户);
            return false;
        }

        await Task.Run(() => 设置密码(账号项, 新密码), 取消令牌).ConfigureAwait(false);

        await 异步保存(取消令牌).ConfigureAwait(false);

        _日志.LogInformation("账号「{Name}」密码已修改", 实际用户);

        用户已变更?.Invoke(this, EventArgs.Empty);
        return true;
    }

    // ── 密码学 ──────────────────────────────────────────────────────────

    /// <summary>写入新密码（生成新盐 + 派生哈希）。</summary>
    private static void 设置密码(用户账号 账号项, string 密码)
    {
        var 盐值 = RandomNumberGenerator.GetBytes(盐值大小);

        账号项.盐值 = Convert.ToBase64String(盐值);
        账号项.密码哈希 = Convert.ToBase64String(推导(密码, 盐值));
        账号项.是否默认密码 = false;
    }

    /// <summary>校验密码。固定时间比较，避免根据耗时推断密码。</summary>
    private bool 验证密码(string 密码, 用户账号 账号项)
    {
        if (string.IsNullOrEmpty(账号项.盐值) || string.IsNullOrEmpty(账号项.密码哈希))
        {
            return false;
        }

        try
        {
            var 盐值 = Convert.FromBase64String(账号项.盐值);
            var 期望 = Convert.FromBase64String(账号项.密码哈希);
            var 实际 = 推导(密码, 盐值);

            return CryptographicOperations.FixedTimeEquals(期望, 实际);
        }
        catch (FormatException)
        {
            // 账号文件里的盐/哈希不是合法 Base64（被手工改坏）。
            // 不记日志的表现为"密码明明没错却登不进去"，现场无从下手排障。
            _日志.LogWarning("账号「{Name}」的密码数据损坏（盐或哈希不是合法 Base64），已拒绝登录", 账号项.名称);
            return false;
        }
    }

    private static byte[] 推导(string 密码, byte[] 盐值) =>
        Rfc2898DeriveBytes.Pbkdf2(密码, 盐值, PBKDF2迭代次数, HashAlgorithmName.SHA256, 哈希大小);

    private 用户账号? 查找(string 用户名称) =>
        _文件.用户.FirstOrDefault(用户 => string.Equals(用户.名称, 用户名称, StringComparison.OrdinalIgnoreCase));

    private static 用户账号 创建默认(string 名称值, string 显示名称值, 用户角色 角色项)
    {
        var 账号项 = new 用户账号 { 名称 = 名称值, 显示名称 = 显示名称值, 角色 = 角色项 };
        设置密码(账号项, 用户服务接口.默认密码);
        账号项.是否默认密码 = true;   // 标记为初始密码，界面提示尽快修改

        return 账号项;
    }

    private async Task 异步保存(CancellationToken 取消令牌)
    {
        var JSON文本 = JsonSerializer.Serialize(_文件, JSON配置.写入);

        try
        {
            await 原子文件.异步写入全部文本(配置路径.用户, JSON文本, 取消令牌).ConfigureAwait(false);
            _日志.LogDebug("账号已保存：{Path}", 配置路径.用户);
        }
        catch (Exception 异常)
        {
            _日志.LogError(异常, "保存账号文件失败：{Path}", 配置路径.用户);
            throw;
        }
    }
}
