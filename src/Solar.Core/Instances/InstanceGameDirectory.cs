using Solar.Core.Paths;
using Solar.Core.Settings;

namespace Solar.Core.Instances;

/// <summary>复制共享游戏目录内容的结果。</summary>
public sealed record CopySummary(int CopiedFiles, int SkippedFiles);

/// <summary>
/// 决定一个实例该用哪个游戏目录（.minecraft），对齐 PCL 的版本隔离：
/// 隔离开启时游戏目录就是该版本的版本文件夹，关闭时用全局设置里的共享目录。
/// <para>
/// 游戏目录优先看实例的 <see cref="GameInstance.GameDirectoryOverride"/>
/// （整合包实例的私有目录），否则在该版本的版本文件夹里找：
/// <c>SolarShared/versions</c> 与共享游戏目录的 <c>versions</c>。
/// assets 与 libraries 始终在 <c>SolarShared/</c> 下，不受隔离影响。
/// </para>
/// </summary>
public sealed class InstanceGameDirectory(AppPaths paths, ISettingsService settings)
{
    /// <summary>共享游戏目录，即未开启隔离的实例所用的那份 .minecraft。</summary>
    public string Shared => paths.ResolveMinecraftDirectory(settings.Current.MinecraftDirectory);

    /// <summary>
    /// 实例目录下的 .minecraft。整合包实例在加载器装好前用它
    /// （版本 id 会变，不能按 id 推版本文件夹）。
    /// </summary>
    public string GetOwnGameDirectory(string instanceId) => paths.GetInstanceMinecraftDirectory(instanceId);

    /// <summary>
    /// 隔离实例的游戏目录：覆盖路径优先，否则是该版本的版本文件夹。
    /// 版本还没下载时返回它的落点 <c>SolarShared/versions/&lt;id&gt;</c>。
    /// </summary>
    public string GetIsolatedPath(GameInstance instance)
    {
        var overridePath = instance.GameDirectoryOverride;
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            // 相对路径按程序根目录解析，与 MinecraftDirectory 的解析规则一致（便携性）；
            // 绝对路径（整合包实例目录，可能在 Root 外）原样使用。
            var expanded = Environment.ExpandEnvironmentVariables(overridePath.Trim());
            return Path.GetFullPath(expanded, paths.Root);
        }

        var versionId = instance.EffectiveVersionId;
        if (string.IsNullOrWhiteSpace(versionId))
        {
            // 没有版本信息的实例（理论上不会出现）退回实例目录，避免把路径拼到 versions 根上
            return paths.GetInstanceMinecraftDirectory(instance.Id);
        }

        return VersionFolders.Find(paths, Shared, versionId)
               ?? Path.Combine(paths.Versions, versionId);
    }

    /// <summary>解析实例实际使用的游戏目录：隔离用版本文件夹，否则用共享目录。</summary>
    public string Resolve(GameInstance instance) =>
        instance.VersionIsolation ? GetIsolatedPath(instance) : Shared;

    /// <summary>
    /// 把共享目录里属于玩家的内容复制到隔离实例的版本文件夹，用于刚开启隔离时给用户一个可用的起点。
    /// 目标已存在的文件不覆盖（共享目录里那份可能更旧），源目录只读不动。
    /// 复制可能涉及上千个文件，调用方应放到后台线程执行。
    /// </summary>
    public CopySummary CopySharedContent(GameInstance instance, CancellationToken ct = default)
    {
        var source = Shared;
        var target = GetIsolatedPath(instance);

        if (!Directory.Exists(source))
        {
            return new CopySummary(0, 0);
        }

        Directory.CreateDirectory(target);

        var copied = 0;
        var skipped = 0;

        foreach (var entry in CopyableEntries)
        {
            ct.ThrowIfCancellationRequested();

            var sourcePath = Path.Combine(source, entry);
            var targetPath = Path.Combine(target, entry);

            if (Directory.Exists(sourcePath))
            {
                CopyDirectory(sourcePath, targetPath, ct, ref copied, ref skipped);
            }
            else if (File.Exists(sourcePath))
            {
                CopyFile(sourcePath, targetPath, ref copied, ref skipped);
            }
        }

        return new CopySummary(copied, skipped);
    }

    /// <summary>开启隔离时值得带走的内容。其余（logs、crash-reports、缓存）留一份旧的没有意义。</summary>
    private static readonly string[] CopyableEntries =
    [
        "mods",
        "config",
        "defaultconfigs",
        "saves",
        "resourcepacks",
        "shaderpacks",
        "options.txt",
        "servers.dat",
    ];

    private static void CopyDirectory(string source, string target, CancellationToken ct, ref int copied, ref int skipped)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(source, file);
            CopyFile(file, Path.Combine(target, relative), ref copied, ref skipped);
        }
    }

    private static void CopyFile(string source, string target, ref int copied, ref int skipped)
    {
        if (File.Exists(target))
        {
            skipped++;
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        try
        {
            // overwrite: false —— 与上面的存在性检查配合，避免并发下悄悄覆盖
            File.Copy(source, target, overwrite: false);
            copied++;
        }
        catch (IOException)
        {
            // 检查与复制之间被创建（或文件正被占用），按已存在处理
            skipped++;
        }
    }
}
