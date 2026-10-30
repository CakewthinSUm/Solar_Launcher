using Solar.Core.Settings;

namespace Solar.Core.Paths;

/// <summary>
/// MC 文件夹（默认程序目录下的 <c>.minecraft</c>）里的所有目录。
/// <para>
/// 与 PCL 一致：版本、库、资源、运行时都在同一个 MC 文件夹下，
/// 版本文件夹（<c>versions/&lt;id&gt;</c>）在开启隔离时就是游戏目录。
/// 每次访问都按当前设置解析，切换 MC 文件夹后立即生效。
/// </para>
/// </summary>
public sealed class MinecraftPaths(AppPaths paths, ISettingsService settings)
{
    /// <summary>当前设置指向的 MC 文件夹。</summary>
    public string MinecraftRoot => paths.ResolveMinecraftDirectory(settings.Current.MinecraftDirectory);

    public string Versions => Path.Combine(MinecraftRoot, "versions");
    public string Libraries => Path.Combine(MinecraftRoot, "libraries");
    public string Assets => Path.Combine(MinecraftRoot, "assets");
    public string Runtimes => Path.Combine(MinecraftRoot, "runtime");

    public string GetVersionDirectory(string versionId) => Path.Combine(Versions, versionId);

    public string GetVersionJsonPath(string versionId) =>
        Path.Combine(GetVersionDirectory(versionId), versionId + ".json");

    public string GetVersionJarPath(string versionId) =>
        Path.Combine(GetVersionDirectory(versionId), versionId + ".jar");

    /// <summary>版本自己的启动器数据目录：<c>versions/&lt;id&gt;/.solar</c>。</summary>
    public string GetVersionSettingsDirectory(string versionId) =>
        Path.Combine(GetVersionDirectory(versionId), ".solar");

    public string GetVersionSettingsFile(string versionId) =>
        Path.Combine(GetVersionSettingsDirectory(versionId), "version.json");

    /// <summary>版本实际使用的游戏目录：隔离时是版本文件夹，否则是 MC 文件夹根。</summary>
    public string ResolveGameDirectory(string versionId, bool isolated) =>
        isolated ? GetVersionDirectory(versionId) : MinecraftRoot;

    /// <summary>
    /// 版本的原生库解压目录。
    /// <para>
    /// 路径里有非 ASCII 字符时回退到 <c>%LOCALAPPDATA%\SolarLauncher\natives</c>：
    /// 部分原生库按 ANSI 路径加载，中文目录下会直接加载失败（PCL 同样做了这个回退）。
    /// 版本名本身也可能带中文，所以回退目录用哈希而不是版本名。
    /// </para>
    /// </summary>
    public string GetNativesDirectory(string versionId)
    {
        var primary = Path.Combine(GetVersionSettingsDirectory(versionId), "natives");
        if (primary.All(char.IsAscii))
        {
            return primary;
        }

        var safeName = versionId.All(char.IsAscii)
            ? versionId
            : Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.UTF8.GetBytes(versionId)))[..12].ToLowerInvariant();

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SolarLauncher",
            "natives",
            safeName);
    }

    /// <summary>确保 MC 文件夹的标准目录都存在。</summary>
    public void EnsureCreated()
    {
        foreach (var dir in new[] { Versions, Libraries, Assets, Runtimes })
        {
            Directory.CreateDirectory(dir);
        }
    }
}
