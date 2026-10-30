using Solar.Core.Json;
using Solar.Core.Paths;

namespace Solar.Core.Accounts;

public interface IAccountStore
{
    Task<IReadOnlyList<GameAccount>> GetAllAsync(CancellationToken ct = default);
    Task<GameAccount?> GetAsync(string id, CancellationToken ct = default);
    Task SaveAsync(GameAccount account, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>当前选中的账户 Id；为空表示还没有选择。</summary>
    string? ActiveAccountId { get; set; }

    Task LoadAsync(CancellationToken ct = default);
}

/// <summary>
/// 账户列表持久化到 <c>%APPDATA%\SolarLauncher\accounts.json</c>。
/// <para>
/// 这里只存展示信息（用户名、UUID、XUID），不含任何令牌。
/// OAuth / XSTS / Minecraft 令牌由 CmlLib 的账户管理器写在
/// <c>%APPDATA%\SolarLauncher\msa-accounts.json</c>，它自己负责刷新与存储，
/// 我们不复制一份，避免出现两份互相不同步的令牌。
/// </para>
/// </summary>
public sealed class AccountStore(AppPaths paths) : IAccountStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AccountFile? _cache;

    public string? ActiveAccountId { get; set; }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _cache ??= await ReadAsync(ct).ConfigureAwait(false);
            ActiveAccountId ??= _cache.ActiveAccountId;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<GameAccount>> GetAllAsync(CancellationToken ct = default)
    {
        var file = await ReadAsync(ct).ConfigureAwait(false);
        return file.Accounts.OrderByDescending(a => a.LastUsedAt ?? a.AddedAt).ToList();
    }

    public async Task<GameAccount?> GetAsync(string id, CancellationToken ct = default)
    {
        var file = await ReadAsync(ct).ConfigureAwait(false);
        return file.Accounts.FirstOrDefault(a => a.Id == id);
    }

    public async Task SaveAsync(GameAccount account, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var file = await ReadAsync(ct).ConfigureAwait(false);
            var index = file.Accounts.FindIndex(a => a.Id == account.Id);
            if (index >= 0)
            {
                file.Accounts[index] = account;
            }
            else
            {
                file.Accounts.Add(account);
            }

            file.ActiveAccountId ??= account.Id;
            ActiveAccountId = file.ActiveAccountId;

            await WriteAsync(file, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var file = await ReadAsync(ct).ConfigureAwait(false);
            file.Accounts.RemoveAll(a => a.Id == id);

            if (file.ActiveAccountId == id)
            {
                file.ActiveAccountId = file.Accounts.FirstOrDefault()?.Id;
                ActiveAccountId = file.ActiveAccountId;
            }

            await WriteAsync(file, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AccountFile> ReadAsync(CancellationToken ct)
    {
        _cache = await JsonFile.ReadAsync<AccountFile>(paths.AccountsFile, ct, new AccountFile()).ConfigureAwait(false)
                 ?? new AccountFile();
        return _cache;
    }

    private Task WriteAsync(AccountFile file, CancellationToken ct)
    {
        _cache = file;
        return JsonFile.WriteAsync(paths.AccountsFile, file, ct);
    }

    private sealed class AccountFile
    {
        public List<GameAccount> Accounts { get; set; } = new();
        public string? ActiveAccountId { get; set; }
    }
}
