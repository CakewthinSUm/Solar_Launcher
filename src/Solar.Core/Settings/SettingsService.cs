using Solar.Core.Json;
using Solar.Core.Paths;

namespace Solar.Core.Settings;

public interface ISettingsService
{
    LauncherSettings Current { get; }
    Task LoadAsync(CancellationToken ct = default);
    Task SaveAsync(CancellationToken ct = default);
}

public sealed class SettingsService(AppPaths paths) : ISettingsService
{
    public LauncherSettings Current { get; private set; } = new();

    public async Task LoadAsync(CancellationToken ct = default)
    {
        Current = await JsonFile.ReadAsync<LauncherSettings>(paths.SettingsFile, ct, new LauncherSettings()).ConfigureAwait(false)
                  ?? new LauncherSettings();
    }

    public Task SaveAsync(CancellationToken ct = default) => JsonFile.WriteAsync(paths.SettingsFile, Current, ct);
}
