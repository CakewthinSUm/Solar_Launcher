namespace Solar.Core.Accounts;

/// <summary>账户类型。本项目只支持微软正版登录。</summary>
public enum AccountType
{
    Microsoft = 0,
}

/// <summary>
/// 一个已登录的正版账户。
/// <para>
/// 这里只保存"展示用的身份信息"和"下次静默刷新所需的句柄"。
/// 真正的 OAuth / XSTS / Minecraft 令牌由 CmlLib 的账户管理器保管，
/// 存放在 <c>%APPDATA%\SolarLauncher\msa-accounts.json</c>，不经过本类。
/// </para>
/// </summary>
public sealed class GameAccount
{
    /// <summary>稳定标识：Minecraft 账户 UUID（不带连字符）。</summary>
    public string Id { get; set; } = "";

    public AccountType Type { get; set; } = AccountType.Microsoft;

    /// <summary>Minecraft 用户名（游戏内显示名）。</summary>
    public string Username { get; set; } = "";

    /// <summary>带连字符的 UUID，用于启动参数。</summary>
    public string Uuid { get; set; } = "";

    /// <summary>Xbox 用户 ID，1.21+ 的 <c>${auth_xuid}</c> 需要它。</summary>
    public string? Xuid { get; set; }

    /// <summary>微软账户邮箱或标识，仅用于在账户列表里区分同一个人。</summary>
    public string? MicrosoftAccount { get; set; }

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>上次成功登录的时间；用于判断是否需要重新走交互式登录。</summary>
    public DateTimeOffset? LastAuthenticatedAt { get; set; }
}

/// <summary>
/// 一次启动所需的账户凭据快照。
/// 令牌有时效，因此不持久化，每次启动前重新获取。
/// </summary>
public sealed record AccountSession(
    string Username,
    string Uuid,
    string AccessToken,
    string? Xuid,
    string UserType = "msa");

/// <summary>账户操作失败的原因，供 UI 决定提示什么。</summary>
public enum AccountErrorKind
{
    NotSignedIn,
    NoMinecraftOwnership,
    XboxAccountRequired,
    XboxBanned,
    RegionBlocked,
    NeedsAdultVerification,
    ChildAccountRestricted,
    RequiresInteractiveLogin,
    ConfigurationMissing,

    /// <summary>
    /// 微软登录与 Xbox 验证都通过，但 api.minecraftservices.com 拒绝签发令牌。
    /// 通常是客户端 ID 未通过 Minecraft 应用审核。
    /// </summary>
    ClientNotApproved,

    Unknown,
}

public sealed class AccountException(AccountErrorKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public AccountErrorKind Kind { get; } = kind;
}
