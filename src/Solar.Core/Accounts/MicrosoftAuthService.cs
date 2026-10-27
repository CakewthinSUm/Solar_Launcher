using CmlLib.Core.Auth.Microsoft;
using CmlLib.Core.Auth.Microsoft.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using Solar.Core.Paths;
using XboxAuthNet.Game.Accounts;
using XboxAuthNet.Game.Msal;
using XboxAuthNet.Game.Msal.OAuth;
using XboxAuthNet.XboxLive;

namespace Solar.Core.Accounts;

/// <summary>
/// 设备码登录过程中需要展示给用户的信息。
/// </summary>
public sealed record DeviceCodePrompt(string UserCode, string VerificationUri, string Message);

public interface IMicrosoftAuthService
{
    /// <summary>
    /// 交互式登录：优先尝试静默刷新，失败则走设备码流程。
    /// 会通过 <paramref name="onDeviceCode"/> 把验证码交给 UI 展示。
    /// </summary>
    Task<GameAccount> SignInAsync(
        Func<DeviceCodePrompt, Task> onDeviceCode,
        CancellationToken ct = default);

    /// <summary>
    /// 静默获取令牌，用于启动前刷新。
    /// 若需要重新交互（令牌彻底过期或被撤销），抛
    /// <see cref="AccountException"/> 且 Kind 为 <see cref="AccountErrorKind.RequiresInteractiveLogin"/>。
    /// </summary>
    Task<AccountSession> AcquireSessionAsync(string accountId, CancellationToken ct = default);

    /// <summary>移除账户，同时清掉本地保存的令牌。</summary>
    Task SignOutAsync(string accountId, CancellationToken ct = default);
}

/// <summary>
/// 微软正版登录。
/// <para>
/// 令牌链（OAuth → Xbox Live → XSTS → Minecraft）由
/// CmlLib.Core.Auth.Microsoft 完成，它内部使用 MSAL 做 OAuth，
/// 并自行缓存与刷新令牌。本类只负责：
/// 把设备码提示转成 UI 能显示的形式、
/// 把 CmlLib 的会话映射成 <see cref="GameAccount"/>、
/// 以及把各类失败翻译成可读的中文提示。
/// </para>
/// </summary>
public sealed class MicrosoftAuthService(
    AppPaths paths,
    IAccountStore store,
    AuthOptions options,
    Solar.Core.Settings.ISettingsService settings,
    ILogger<MicrosoftAuthService> logger) : IMicrosoftAuthService
{
    private const string AccountFileName = "msa-accounts.json";

    private JELoginHandler? _handler;
    private IPublicClientApplication? _msalApp;
    private readonly SemaphoreSlim _initGate = new(1, 1);

    public async Task<GameAccount> SignInAsync(
        Func<DeviceCodePrompt, Task> onDeviceCode,
        CancellationToken ct = default)
    {
        EnsureConfigured();
        var (handler, msalApp) = await GetOrCreateAsync(ct).ConfigureAwait(false);

        var authenticator = handler.CreateAuthenticatorWithNewAccount(ct);

        authenticator.AddMsalOAuth(msalApp, msal => msal.DeviceCode(code =>
            onDeviceCode(new DeviceCodePrompt(
                code.UserCode,
                code.VerificationUrl,
                code.Message))));

        authenticator.AddXboxAuthForJE(xbox => xbox.Basic());
        authenticator.AddForceJEAuthenticator();

        try
        {
            var session = await authenticator.ExecuteForLauncherAsync().ConfigureAwait(false);

            var account = new GameAccount
            {
                Id = NormalizeId(session.UUID),
                Type = AccountType.Microsoft,
                Username = session.Username ?? "未知玩家",
                Uuid = FormatUuid(session.UUID),
                Xuid = session.Xuid,
                MicrosoftAccount = TryGetMicrosoftAccount(handler),
                AddedAt = DateTimeOffset.Now,
                LastUsedAt = DateTimeOffset.Now,
                LastAuthenticatedAt = DateTimeOffset.Now,
            };

            await store.SaveAsync(account, ct).ConfigureAwait(false);
            store.ActiveAccountId = account.Id;
            await store.LoadAsync(ct).ConfigureAwait(false);

            logger.LogInformation("微软账户登录成功: {Username}", account.Username);
            return account;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Translate(ex);
        }
    }

    public async Task<AccountSession> AcquireSessionAsync(string accountId, CancellationToken ct = default)
    {
        EnsureConfigured();
        var (handler, _) = await GetOrCreateAsync(ct).ConfigureAwait(false);

        var account = FindCmlAccount(handler, accountId)
                      ?? throw new AccountException(
                          AccountErrorKind.NotSignedIn,
                          "该账户的登录信息已失效，请重新登录。");

        try
        {
            var session = await handler.AuthenticateSilently(account, ct).ConfigureAwait(false);

            var stored = await store.GetAsync(accountId, ct).ConfigureAwait(false);
            if (stored is not null)
            {
                stored.LastAuthenticatedAt = DateTimeOffset.Now;
                stored.LastUsedAt = DateTimeOffset.Now;
                if (!string.IsNullOrEmpty(session.Username))
                {
                    stored.Username = session.Username;
                }

                await store.SaveAsync(stored, ct).ConfigureAwait(false);
            }

            return new AccountSession(
                session.Username ?? stored?.Username ?? "玩家",
                FormatUuid(session.UUID ?? stored?.Uuid ?? ""),
                session.AccessToken ?? "",
                session.Xuid ?? stored?.Xuid,
                session.UserType ?? "msa");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var translated = Translate(ex);
            if (translated.Kind == AccountErrorKind.Unknown)
            {
                // 静默刷新失败最常见的根因是刷新令牌过期，需要用户重新登录
                throw new AccountException(
                    AccountErrorKind.RequiresInteractiveLogin,
                    "登录状态已过期，请重新登录。",
                    ex);
            }

            throw translated;
        }
    }

    public async Task SignOutAsync(string accountId, CancellationToken ct = default)
    {
        var (handler, msalApp) = await GetOrCreateAsync(ct).ConfigureAwait(false);

        var account = FindCmlAccount(handler, accountId);
        if (account is not null)
        {
            try
            {
                await handler.Signout(account, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 清理本地令牌失败不应阻止用户移除账户
                logger.LogWarning(ex, "清理账户令牌失败: {AccountId}", accountId);
            }
        }

        await store.DeleteAsync(accountId, ct).ConfigureAwait(false);
        logger.LogInformation("已移除账户: {AccountId}", accountId);
    }

    private async Task<(JELoginHandler Handler, IPublicClientApplication MsalApp)> GetOrCreateAsync(
        CancellationToken ct)
    {
        var clientId = ResolveClientId();
        if (_handler is not null && _msalApp is not null && _builtForClientId == clientId)
        {
            return (_handler, _msalApp);
        }

        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_handler is not null && _msalApp is not null && _builtForClientId == clientId)
            {
                return (_handler, _msalApp);
            }

            Directory.CreateDirectory(paths.Data);

            _handler = new JELoginHandlerBuilder()
                .WithLogger(logger)
                .WithAccountManager(Path.Combine(paths.Data, AccountFileName))
                .Build();

            _msalApp = await MsalClientHelper
                .BuildApplicationWithCache(clientId)
                .ConfigureAwait(false);

            _builtForClientId = clientId;
            return (_handler, _msalApp);
        }
        finally
        {
            _initGate.Release();
        }
    }

    /// <summary>
    /// 客户端 ID 以设置为准，回退到注入的默认值。
    /// 这样用户在设置页填写后无需重启启动器。
    /// </summary>
    private string ResolveClientId()
    {
        var configured = settings.Current.MicrosoftClientId;
        return string.IsNullOrWhiteSpace(configured) ? options.ClientId : configured.Trim();
    }

    /// <summary>
    /// 客户端 ID 变了要重建 MSAL 应用，否则缓存的令牌属于旧应用，静默刷新会失败。
    /// </summary>
    private string? _builtForClientId;

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(ResolveClientId()))
        {
            throw new AccountException(
                AccountErrorKind.ConfigurationMissing,
                "尚未配置微软应用客户端 ID，无法登录。请在设置中填写 ClientId。");
        }
    }

    private static IXboxGameAccount? FindCmlAccount(JELoginHandler handler, string accountId)
    {
        return handler.AccountManager.GetAccounts()
            .FirstOrDefault(a => NormalizeId(a.Identifier) == NormalizeId(accountId));
    }

    private static string? TryGetMicrosoftAccount(JELoginHandler handler)
    {
        try
        {
            var account = handler.AccountManager.GetDefaultAccount();
            return account?.Identifier;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>UUID 去掉连字符并小写，作为稳定主键。</summary>
    public static string NormalizeId(string? uuid) =>
        (uuid ?? "").Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();

    /// <summary>把 32 位十六进制补成带连字符的标准 UUID 格式。</summary>
    public static string FormatUuid(string? uuid)
    {
        var raw = NormalizeId(uuid);
        if (raw.Length != 32)
        {
            return uuid ?? "";
        }

        return $"{raw[..8]}-{raw[8..12]}-{raw[12..16]}-{raw[16..20]}-{raw[20..]}";
    }

    /// <summary>把底层异常翻译成带中文说明的 <see cref="AccountException"/>。</summary>
    public static AccountException Translate(Exception ex)
    {
        var text = ex.ToString();

        // XSTS 的错误码定义见 minecraft.wiki/w/Microsoft_authentication
        if (text.Contains("2148916227", StringComparison.Ordinal))
        {
            return new AccountException(AccountErrorKind.XboxBanned, "该 Xbox 账户已被封禁。", ex);
        }

        if (text.Contains("2148916233", StringComparison.Ordinal))
        {
            return new AccountException(
                AccountErrorKind.XboxAccountRequired,
                "该微软账户还没有 Xbox 账户，请先在 Xbox 官网创建。",
                ex);
        }

        if (text.Contains("2148916235", StringComparison.Ordinal))
        {
            return new AccountException(
                AccountErrorKind.RegionBlocked,
                "该地区无法使用 Xbox Live。",
                ex);
        }

        if (text.Contains("2148916236", StringComparison.Ordinal)
            || text.Contains("2148916237", StringComparison.Ordinal))
        {
            return new AccountException(
                AccountErrorKind.NeedsAdultVerification,
                "该账户需要完成成年人验证。",
                ex);
        }

        if (text.Contains("2148916238", StringComparison.Ordinal))
        {
            return new AccountException(
                AccountErrorKind.ChildAccountRestricted,
                "儿童账户需要先加入家庭组才能登录。",
                ex);
        }

        if (text.Contains("NOT_FOUND", StringComparison.Ordinal)
            || text.Contains("does not own", StringComparison.OrdinalIgnoreCase))
        {
            return new AccountException(
                AccountErrorKind.NoMinecraftOwnership,
                "该账户没有 Minecraft: Java 版的所有权。",
                ex);
        }

        if (ex is MsalUiRequiredException || text.Contains("invalid_grant", StringComparison.Ordinal))
        {
            return new AccountException(
                AccountErrorKind.RequiresInteractiveLogin,
                "登录状态已过期，请重新登录。",
                ex);
        }

        // 走到这一步说明 OAuth、Xbox Live、XSTS 都成功了，
        // 只是 api.minecraftservices.com 拒绝签发令牌。最常见的原因是
        // 这个客户端 ID 没有通过 Minecraft 的审核，服务器只回一个裸 403。
        if (text.Contains("403", StringComparison.Ordinal)
            && text.Contains("Forbidden", StringComparison.OrdinalIgnoreCase))
        {
            return new AccountException(
                AccountErrorKind.ClientNotApproved,
                "微软账户验证已通过，但 Minecraft 服务拒绝了该应用的访问（403）。"
                + "这通常表示该客户端 ID 尚未通过 Minecraft 应用审核："
                + "请到 https://aka.ms/mce-reviewappid 提交申请，审核通过后再试。",
                ex);
        }

        return new AccountException(AccountErrorKind.Unknown, ex.Message, ex);
    }
}

/// <summary>
/// 微软登录所需的配置。
/// <para>
/// <c>ClientId</c> 必须在 Azure 门户注册应用后获得：
/// 应用类型选「移动和桌面应用」，启用「允许公共客户端流」，
/// 重定向 URI 填 <c>http://localhost</c>，
/// 并在 aka.ms/mce-reviewappid 提交审核，否则调用 Minecraft API 会返回 403。
/// </para>
/// </summary>
public sealed class AuthOptions
{
    public string ClientId { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);
}
