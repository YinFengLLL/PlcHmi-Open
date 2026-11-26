using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PlcHmi.Business.Configuration;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// 用户与权限服务。
///
/// <para><b>存储：</b><c>config/users.json</c>，只存 PBKDF2-SHA256 派生出的哈希与盐，
/// <b>绝不存明文密码</b>。校验用固定时间比较，避免时序侧信道。</para>
///
/// <para><b>登录策略：</b>密码登录，<b>不超时回落</b>——适合无人看守的触摸屏。</para>
/// </summary>
public sealed class UserService : IUserService
{
    /// <summary>PBKDF2 迭代次数。10 万次在工控机上约 30ms，足以拖慢暴力破解。</summary>
    private const int pBKDF2IterateIterationCount = 100_000;

    private const int saltSize = 16;
    private const int hashSize = 32;

    /// <summary>新密码最短长度。</summary>
    private const int minPasswordLength = 6;

    private readonly ILogger<UserService> _log;
    private readonly IConfigHealth? _health;
    private readonly UserFile _file = new();

    private UserAccount? _current;

    public UserService(ILogger<UserService> Log, IConfigHealth? Health = null)
    {
        _log = Log;
        _health = Health;
    }

    /// <inheritdoc />
    public UserRole CurrentRole => _current?.Role ?? UserRole.Operator;

    /// <inheritdoc />
    public string CurrentUserName => _current?.Name ?? string.Empty;

    /// <inheritdoc />
    public string CurrentDisplayName => _current?.DisplayName ?? "未登录（操作员）";

    /// <inheritdoc />
    public bool IsAlreadyLogin => _current is not null;

    /// <inheritdoc />
    public Permission CurrentPermission => RolePermission.Of(CurrentRole);

    /// <inheritdoc />
    public bool IsUseDefaultPassword => _current?.IsDefaultPassword ?? false;

    /// <inheritdoc />
    public IReadOnlyList<UserAccount> AccountNumberCollection => _file.User;

    /// <inheritdoc />
    public event EventHandler? userChanged;

    /// <inheritdoc />
    public bool Has(Permission permissionItem) => RolePermission.Has(CurrentRole, permissionItem);

    // ── 初始化 ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var filePath = ConfigPath.User;

        var resultValue = await ConfigFileLoader
            .LoadJsonAsync<UserFile>(filePath, JsonConfig.Read, _log, cancellationToken)
            .ConfigureAwait(false);

        _health.Report("账号配置", resultValue);

        // 缺失（首次运行）与失败（主文件与 .bak 双损坏）都回退出厂三账号：
        // 账号文件损坏若不清空回填，现场将无人能登录（含 admin），只能手工删文件——
        // 这是"双损坏永久锁死"，比丢一次账号配置严重得多。
        if (resultValue.loadResult is LoadResultSplitCategory.Missing or LoadResultSplitCategory.Failed)
        {
            _log.LogWarning("账号文件{Reason}，已恢复出厂默认账号（初始密码 {Password}）：{Path}",
                resultValue.loadResult is LoadResultSplitCategory.Missing ? "不存在" : "已损坏（主文件与备份均无法读取）",
                IUserService.DefaultPassword, filePath);

            _file.User.Clear();
            _file.User.Add(CreateDefault("operator", "操作员", UserRole.Operator));
            _file.User.Add(CreateDefault("engineer", "工程师", UserRole.Engineer));
            _file.User.Add(CreateDefault("admin", "管理员", UserRole.Admin));

            await SaveAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        _file.User.Clear();

        if (resultValue.Value is not null)
        {
            _file.User.AddRange(resultValue.Value.User);
        }

        if (resultValue.loadResult is LoadResultSplitCategory.Loaded)
        {
            _log.LogInformation("已加载 {Count} 个账号", _file.User.Count);
        }

        userChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── 登录 / 登出 ─────────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// PBKDF2 迭代 10 万次约需 30–50ms，属<b>同步 CPU 计算</b>。
    /// 放在线程池执行，避免登录时把 UI 线程卡住（也便于将来调高迭代次数）。
    /// </remarks>
    public async Task<bool> LoginAsync(
        string userName, string password, CancellationToken cancellationToken = default)
    {
        var accountNumberItem = Find(userName);

        if (accountNumberItem is null)
        {
            _log.LogWarning("登录失败：账号「{Name}」不存在", userName);
            return false;
        }

        var passwordPositiveConfirm = await Task.Run(() => VerifyPassword(password, accountNumberItem), cancellationToken)
            .ConfigureAwait(false);

        if (!passwordPositiveConfirm)
        {
            _log.LogWarning("登录失败：账号「{Name}」密码不正确", userName);
            return false;
        }

        _current = accountNumberItem;

        _log.LogInformation("已登录：{Display}（{Role}）",
            accountNumberItem.DisplayName, RolePermission.DisplayName(accountNumberItem.Role));

        userChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <inheritdoc />
    public void ExitLogin()
    {
        if (_current is null)
        {
            return;
        }

        _log.LogInformation("已登出：{Display}", _current.DisplayName);

        _current = null;
        userChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    /// <remarks>与登录同理：PBKDF2 是同步 CPU 计算，放线程池执行避免卡住界面。</remarks>
    public async Task<bool> ChangePasswordAsync(
        string userName, string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        var accountNumberItem = Find(userName);
        var actualUser = userName;

        if (accountNumberItem is null)
        {
            _log.LogWarning("修改密码失败：账号「{Name}」不存在", actualUser);
            return false;
        }

        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < minPasswordLength)
        {
            _log.LogWarning("修改密码失败：新密码长度不足 {Min} 位", minPasswordLength);
            return false;
        }

        var passwordPositiveConfirm = await Task.Run(() => VerifyPassword(currentPassword, accountNumberItem), cancellationToken)
            .ConfigureAwait(false);

        if (!passwordPositiveConfirm)
        {
            _log.LogWarning("修改密码失败：原密码不正确（{Name}）", actualUser);
            return false;
        }

        await Task.Run(() => SetPassword(accountNumberItem, newPassword), cancellationToken).ConfigureAwait(false);

        await SaveAsync(cancellationToken).ConfigureAwait(false);

        _log.LogInformation("账号「{Name}」密码已修改", actualUser);

        userChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    // ── 密码学 ──────────────────────────────────────────────────────────

    /// <summary>写入新密码（生成新盐 + 派生哈希）。</summary>
    private static void SetPassword(UserAccount accountNumberItem, string password)
    {
        var Salt = RandomNumberGenerator.GetBytes(saltSize);

        accountNumberItem.Salt = Convert.ToBase64String(Salt);
        accountNumberItem.PasswordHash = Convert.ToBase64String(Resolve(password, Salt));
        accountNumberItem.IsDefaultPassword = false;
    }

    /// <summary>校验密码。固定时间比较，避免根据耗时推断密码。</summary>
    private bool VerifyPassword(string password, UserAccount accountNumberItem)
    {
        if (string.IsNullOrEmpty(accountNumberItem.Salt) || string.IsNullOrEmpty(accountNumberItem.PasswordHash))
        {
            return false;
        }

        try
        {
            var Salt = Convert.FromBase64String(accountNumberItem.Salt);
            var expected = Convert.FromBase64String(accountNumberItem.PasswordHash);
            var actual = Resolve(password, Salt);

            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            // 账号文件里的盐/哈希不是合法 Base64（被手工改坏）。
            // 不记日志的表现为"密码明明没错却登不进去"，现场无从下手排障。
            _log.LogWarning("账号「{Name}」的密码数据损坏（盐或哈希不是合法 Base64），已拒绝登录", accountNumberItem.Name);
            return false;
        }
    }

    private static byte[] Resolve(string password, byte[] Salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, Salt, pBKDF2IterateIterationCount, HashAlgorithmName.SHA256, hashSize);

    private UserAccount? Find(string userName) =>
        _file.User.FirstOrDefault(User => string.Equals(User.Name, userName, StringComparison.OrdinalIgnoreCase));

    private static UserAccount CreateDefault(string nameValue, string displayNameValue, UserRole roleItem)
    {
        var accountNumberItem = new UserAccount { Name = nameValue, DisplayName = displayNameValue, Role = roleItem };
        SetPassword(accountNumberItem, IUserService.DefaultPassword);
        accountNumberItem.IsDefaultPassword = true;   // 标记为初始密码，界面提示尽快修改

        return accountNumberItem;
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        var jsonText = JsonSerializer.Serialize(_file, JsonConfig.Write);

        try
        {
            await AtomicFile.WriteAllTextAsync(ConfigPath.User, jsonText, cancellationToken).ConfigureAwait(false);
            _log.LogDebug("账号已保存：{Path}", ConfigPath.User);
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "保存账号文件失败：{Path}", ConfigPath.User);
            throw;
        }
    }
}
