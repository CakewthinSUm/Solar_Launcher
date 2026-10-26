using Solar.Core.Json;
using Solar.Core.Paths;

namespace Solar.Core.Instances;

public interface IInstanceRepository
{
    Task<IReadOnlyList<GameInstance>> GetAllAsync(CancellationToken ct = default);
    Task<GameInstance?> GetAsync(string id, CancellationToken ct = default);
    Task SaveAsync(GameInstance instance, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
}

/// <summary>
/// 实例列表存放在 <c>SolarData/instances.json</c>，实例自身目录里也留一份
/// <c>instance.json</c>，便于手动搬移目录。
/// </summary>
public sealed class InstanceRepository(AppPaths paths) : IInstanceRepository
{
    private const string FileName = "instance.json";

    public async Task<IReadOnlyList<GameInstance>> GetAllAsync(CancellationToken ct = default)
    {
        var index = await JsonFile.ReadAsync<List<string>>(paths.InstanceIndexFile, ct, []).ConfigureAwait(false) ?? [];
        var result = new List<GameInstance>(index.Count);

        foreach (var id in index)
        {
            var instance = await GetAsync(id, ct).ConfigureAwait(false);
            if (instance is not null)
            {
                result.Add(instance);
            }
        }

        return result;
    }

    public Task<GameInstance?> GetAsync(string id, CancellationToken ct = default) =>
        JsonFile.ReadAsync<GameInstance>(paths.GetInstanceFile(id, FileName), ct);

    public async Task SaveAsync(GameInstance instance, CancellationToken ct = default)
    {
        Directory.CreateDirectory(paths.GetInstanceDirectory(instance.Id));
        Directory.CreateDirectory(paths.GetInstanceMinecraftDirectory(instance.Id));

        await JsonFile.WriteAsync(paths.GetInstanceFile(instance.Id, FileName), instance, ct).ConfigureAwait(false);

        var index = await JsonFile.ReadAsync<List<string>>(paths.InstanceIndexFile, ct, []).ConfigureAwait(false) ?? [];
        if (!index.Contains(instance.Id))
        {
            index.Add(instance.Id);
            await JsonFile.WriteAsync(paths.InstanceIndexFile, index, ct).ConfigureAwait(false);
        }
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        var index = await JsonFile.ReadAsync<List<string>>(paths.InstanceIndexFile, ct, []).ConfigureAwait(false) ?? [];
        if (index.Remove(id))
        {
            await JsonFile.WriteAsync(paths.InstanceIndexFile, index, ct).ConfigureAwait(false);
        }

        var directory = paths.GetInstanceDirectory(id);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
